using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Harness.Sandbox;
using Harness.Sandbox.Egress;
using Harness.Sdk;

namespace Harness.Tests;

public sealed class EgressTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("harness-egress-").FullName;
    public void Dispose() => Directory.Delete(_ws, recursive: true);

    [Fact]
    public void Allowlist_matches_hosts_wildcards_and_ports()
    {
        EgressAllowlist list = new(["github.com", "*.nuget.org", "registry.local:8443", "10.1.2.3"]);
        Assert.True(list.Allows("github.com", 443));
        Assert.True(list.Allows("GitHub.com.", 80));
        Assert.False(list.Allows("github.com", 22));
        Assert.False(list.Allows("api.github.com", 443));
        Assert.True(list.Allows("api.nuget.org", 443));
        Assert.False(list.Allows("nuget.org", 443));
        Assert.False(list.Allows("evilnuget.org", 443));
        Assert.True(list.Allows("registry.local", 8443));
        Assert.False(list.Allows("registry.local", 443));
        Assert.True(list.Allows("10.1.2.3", 443));
    }

    [Fact]
    public void Host_port_targets_parse()
    {
        Assert.True(EgressProxy.TrySplitHostPort("github.com:443", 80, out string h, out int p) && h == "github.com" && p == 443);
        Assert.True(EgressProxy.TrySplitHostPort("[::1]:8080", 80, out h, out p) && h == "::1" && p == 8080);
        Assert.False(EgressProxy.TrySplitHostPort("github.com:http", 80, out _, out _));
    }

    [Fact]
    public async Task Proxy_tunnels_and_forwards_only_to_allowed_hosts()
    {
        (int port, Task server) = Serve("hello from host");
        string socket = Path.Combine(_ws, "p.sock");
        ConcurrentQueue<EgressAttempt> log = new();
        await using EgressProxy proxy = EgressProxy.Start(socket, [$"localhost:{port}"], log.Enqueue, useEnvironmentProxy: false);

        string plain = await RawAsync(socket, $"GET http://localhost:{port}/x HTTP/1.1\r\nHost: localhost:{port}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", plain);
        Assert.EndsWith("hello from host", plain);

        string tunnel = await RawAsync(socket, $"CONNECT localhost:{port} HTTP/1.1\r\nHost: localhost:{port}\r\n\r\nGET /x HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200 Connection established", tunnel);
        Assert.EndsWith("hello from host", tunnel);

        string denied = await RawAsync(socket, $"CONNECT example.com:443 HTTP/1.1\r\nHost: example.com\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", denied);
        Assert.Contains("example.com:443 is not in this sandbox's allowHosts", denied);
        Assert.Contains(log, e => e is { Host: "example.com", Port: 443, Allowed: false });
        Assert.Contains(log, e => e.Host == "localhost" && e.Allowed);
    }

    [Fact]
    public async Task Allowlisted_bubblewrap_reaches_allowed_hosts_only_through_the_proxy()
    {
        if (!OperatingSystem.IsLinux() || BubblewrapSandboxProvider.FindOnPath("bwrap") is null) return;
        (int port, Task server) = Serve("hello from host");
        ConcurrentQueue<EgressAttempt> log = new();
        SandboxSpec spec = new()
        {
            Name = "t", Type = "bubblewrap", WorkspaceHostPath = _ws, Network = NetworkMode.Allowlist,
            AllowHosts = [$"localhost:{port}"], OnEgress = log.Enqueue,
        };
        await using ISandbox sandbox = await new BubblewrapSandboxProvider().CreateAsync(spec, default);

        string Get(string url) => $$"""
            exec 3<>/dev/tcp/127.0.0.1/3128 && printf 'GET {{url}} HTTP/1.1\r\nHost: x\r\n\r\n' >&3 && cat <&3
            """;
        ExecResult allowed = await sandbox.ExecAsync(new ExecRequest { Command = Get($"http://localhost:{port}/") + "; echo \"proxy=$HTTPS_PROXY\"" }, default);
        Assert.True(allowed.ExitCode == 0, allowed.Output);
        Assert.Contains("hello from host", allowed.Output);
        Assert.Contains("proxy=http://127.0.0.1:3128", allowed.Output);

        ExecResult denied = await sandbox.ExecAsync(new ExecRequest { Command = Get($"http://127.0.0.2:{port}/") }, default);
        Assert.Contains("403 Forbidden", denied.Output);

        // Without the proxy there is no route: the host's loopback is not the sandbox's.
        ExecResult direct = await sandbox.ExecAsync(new ExecRequest { Command = $"(exec 3<>/dev/tcp/127.0.0.1/{port}) 2>/dev/null && echo reached || echo blocked" }, default);
        Assert.Equal("blocked", direct.Output.Trim());

        // The command's own exit code and output come through the forwarder.
        ExecResult exit = await sandbox.ExecAsync(new ExecRequest { Command = "echo out; echo err >&2; exit 7" }, default);
        Assert.Equal(7, exit.ExitCode);
        Assert.Contains("out", exit.Output);
        Assert.Contains("err", exit.Output);
        Assert.Contains(log, e => e.Host == "127.0.0.2" && !e.Allowed);
    }

    [Fact]
    public async Task A_tcp_proxy_answers_only_its_container()
    {
        if (!OperatingSystem.IsLinux()) return;   // 127.0.0.2 is a loopback address on Linux only
        (int port, Task server) = Serve("hello from host");
        await using EgressProxy proxy = EgressProxy.StartTcp(IPAddress.Loopback, IPAddress.Parse("127.0.0.2"), [$"localhost:{port}"], useEnvironmentProxy: false);
        string request = $"GET http://localhost:{port}/ HTTP/1.1\r\nHost: localhost\r\n\r\n";

        async Task<string> From(string source)
        {
            using Socket s = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(new IPEndPoint(IPAddress.Parse(source), 0));
            await s.ConnectAsync(proxy.Endpoint!);
            await using NetworkStream stream = new(s);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
            using StreamReader reader = new(stream);
            try { return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (IOException) { return ""; }
        }

        Assert.EndsWith("hello from host", await From("127.0.0.2"));
        Assert.Equal("", await From("127.0.0.1"));   // anyone else is hung up on
    }

    [Fact]
    public async Task Allowlisted_container_reaches_allowed_hosts_only_through_the_proxy()
    {
        // Needs a running Docker daemon and the bash image already pulled; skipped otherwise, like the bubblewrap tests.
        if (!OperatingSystem.IsLinux() || BubblewrapSandboxProvider.FindOnPath("docker") is null) return;
        using (System.Diagnostics.Process? check = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", ["image", "inspect", "bash:5.2"])
            { RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            if (check is null) return;
            await check.WaitForExitAsync();
            if (check.ExitCode != 0) return;
        }
        (int port, Task server) = Serve("hello from host");
        ConcurrentQueue<EgressAttempt> log = new();
        SandboxSpec spec = new()
        {
            Name = "t", Type = "container", Image = "bash:5.2", WorkspaceHostPath = _ws, WorkspaceSandboxPath = "/workspace",
            Network = NetworkMode.Allowlist, AllowHosts = [$"localhost:{port}"], OnEgress = log.Enqueue,
            Options = new Dictionary<string, string> { ["runtime"] = "docker" },
        };
        string network;
        await using (ISandbox sandbox = await new ContainerSandboxProvider().CreateAsync(spec, default))
        {
            network = sandbox.Id + "-egress";
            string Get(string target) => "p=${HTTPS_PROXY#http://}; exec 3<>/dev/tcp/${p%:*}/${p##*:} && " +
                $"printf 'GET {target} HTTP/1.1\\r\\nHost: x\\r\\n\\r\\n' >&3 && cat <&3";
            ExecResult allowed = await sandbox.ExecAsync(new ExecRequest { Command = Get($"http://localhost:{port}/") }, default);
            Assert.True(allowed.ExitCode == 0, allowed.Output);
            Assert.Contains("hello from host", allowed.Output);
            ExecResult denied = await sandbox.ExecAsync(new ExecRequest { Command = Get("http://example.com/") }, default);
            Assert.Contains("403 Forbidden", denied.Output);
            // The internal network has no route out: a direct connection fails.
            ExecResult direct = await sandbox.ExecAsync(new ExecRequest { Command = "(timeout 5 bash -c 'exec 3<>/dev/tcp/1.1.1.1/53') 2>/dev/null && echo reached || echo blocked" }, default);
            Assert.Equal("blocked", direct.Output.Trim());
        }
        Assert.Contains(log, e => e.Host == "example.com" && !e.Allowed);
        using System.Diagnostics.Process gone = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", ["network", "inspect", network])
            { RedirectStandardOutput = true, RedirectStandardError = true })!;
        await gone.WaitForExitAsync();
        Assert.NotEqual(0, gone.ExitCode);   // the sandbox's network went with it
    }

    [Fact]
    public void No_proxy_rules_bypass_the_upstream_proxy()
    {
        string? before = Environment.GetEnvironmentVariable("HTTPS_PROXY"), noBefore = Environment.GetEnvironmentVariable("NO_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://user:pw@proxy.corp:3128");
            Environment.SetEnvironmentVariable("NO_PROXY", "localhost,.internal,10.0.0.0/8");
            UpstreamProxy upstream = UpstreamProxy.FromEnvironment()!;
            Assert.Equal(("proxy.corp", 3128), (upstream.Host, upstream.Port));
            Assert.Equal("Basic " + Convert.ToBase64String("user:pw"u8.ToArray()), upstream.Authorization);
            Assert.True(upstream.Bypass("localhost"));
            Assert.True(upstream.Bypass("git.internal"));
            Assert.True(upstream.Bypass("10.2.3.4"));
            Assert.False(upstream.Bypass("github.com"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", before);
            Environment.SetEnvironmentVariable("NO_PROXY", noBefore);
        }
    }

    [Fact]
    public async Task Sandbox_test_checks_mounts_network_and_limits_from_inside()
    {
        if (!OperatingSystem.IsLinux() || BubblewrapSandboxProvider.FindOnPath("bwrap") is null) return;
        (int port, Task server) = Serve("hello from host");
        await using Harness.Tests.TestSupport.TestHome home = new();
        string shared = Path.Combine(home.Root, "shared");
        Directory.CreateDirectory(shared);
        File.WriteAllText(home.Paths.SandboxesFile, $$"""
            sealed:
              type: bubblewrap
              network: none
              mounts: [{ host: "{{shared}}", path: /shared, mode: ro }]
              limits: { memoryMb: 512, wallClockMinutes: 5 }
            egress:
              type: bubblewrap
              network: allowlist
              allowHosts: ["localhost:{{port}}"]
            """);
        home.Build(new Harness.Tests.TestSupport.ScriptedChatClient((_, _) => Harness.Tests.TestSupport.ScriptedChatClient.Text("unused")));
        SandboxProbe probe = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SandboxProbe>(home.Services!);

        IReadOnlyList<ProbeResult> sealedResults = await probe.RunAsync("sealed", home.Paths.Home, default);
        string Show(IEnumerable<ProbeResult> r) => string.Join("\n", r.Select(x => $"{x.Status} {x.Check}: {x.Detail}"));
        Assert.True(sealedResults.Where(r => r.Status != "pass").Select(r => r.Check).SequenceEqual(["limits: cpus, memory, pids"]), Show(sealedResults));
        Assert.Contains(sealedResults, r => r.Check == "mount /shared (ro)" && r.Status == "pass");
        Assert.Contains(sealedResults, r => r.Check == "network: none" && r.Status == "pass");

        IReadOnlyList<ProbeResult> egressResults = await probe.RunAsync("egress", home.Paths.Home, default);
        Assert.True(egressResults.All(r => r.Status == "pass"), Show(egressResults));
        Assert.Contains(egressResults, r => r.Check == $"allowlist: localhost:{port}");
        Assert.Contains(egressResults, r => r.Check == "allowlist: other hosts refused");
    }

    private static async Task<string> RawAsync(string socketPath, string request)
    {
        using Socket s = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await s.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        await using NetworkStream stream = new(s);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using MemoryStream all = new();
        byte[] buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                all.Write(buffer, 0, read);
                string text = Encoding.UTF8.GetString(all.ToArray());
                if (text.EndsWith("hello from host", StringComparison.Ordinal) || (text.Contains("\r\n\r\n") && text.StartsWith("HTTP/1.1 403", StringComparison.Ordinal) && text.EndsWith("\n", StringComparison.Ordinal)))
                    break;
            }
        }
        catch (OperationCanceledException) { }
        return Encoding.UTF8.GetString(all.ToArray());
    }

    /// <summary>A tiny HTTP server on the host's loopback that answers every request with <paramref name="body"/>.</summary>
    private static (int Port, Task Server) Serve(string body)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using TcpClient c = client;
                    NetworkStream s = c.GetStream();
                    await EgressProxy.ReadHeadAsync(s, CancellationToken.None);
                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
                    await s.WriteAsync(payload);
                });
            }
        });
        return (port, server);
    }
}
