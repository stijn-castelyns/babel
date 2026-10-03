using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Harness.Sandbox.Egress;
using Harness.Sdk;

namespace Harness.Sandbox;

/// <summary>
/// One Docker or Podman container per run, with <c>exec</c> per command and CPU, memory and PID limits.
/// The container runtime socket is never mounted into the container. With <c>network: allowlist</c> the container gets
/// its own <c>--internal</c> network (no route out) and a fixed address on it; the daemon's egress proxy listens on that
/// network's gateway address and answers only that container.
/// </summary>
public sealed class ContainerSandboxProvider : ISandboxProvider
{
    public string Type => "container";

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken)
    {
        string runtime = spec.Options.GetValueOrDefault("runtime")
            ?? (BubblewrapSandboxProvider.FindOnPath("podman") is not null ? "podman" : "docker");
        string image = spec.Image ?? throw new InvalidOperationException($"Sandbox profile '{spec.Name}' needs an image.");
        string name = "harness-" + Guid.NewGuid().ToString("N")[..12];

        List<string> args = ["run", "-d", "--rm", "--name", name, "--init",
            "-v", $"{spec.WorkspaceHostPath}:{spec.WorkspaceSandboxPath}:rw", "-w", spec.WorkspaceSandboxPath];
        foreach (MountSpec m in spec.Mounts)
            args.AddRange(["-v", $"{m.HostPath}:{m.SandboxPath}:{(m.Mode == MountMode.ReadWrite ? "rw" : "ro")}"]);
        Egress? egress = null;
        if (spec.Network == NetworkMode.Allowlist)
        {
            egress = await Egress.CreateAsync(runtime, name, spec, cancellationToken);
            args.AddRange(["--network", egress.Network, "--ip", egress.ContainerAddress.ToString()]);
            foreach ((string k, string v) in egress.Environment()) args.AddRange(["-e", $"{k}={v}"]);
        }
        else if (spec.Network != NetworkMode.Full) args.AddRange(["--network", "none"]);
        if (spec.Limits.Cpus is double cpus) args.AddRange(["--cpus", cpus.ToString(CultureInfo.InvariantCulture)]);
        if (spec.Limits.MemoryMb is int mem) args.AddRange(["--memory", $"{mem}m"]);
        if (spec.Limits.Pids is int pids) args.AddRange(["--pids-limit", pids.ToString(CultureInfo.InvariantCulture)]);
        foreach ((string k, string v) in spec.Environment) args.AddRange(["-e", $"{k}={v}"]);
        args.AddRange([image, "sleep", "infinity"]);

        ExecResult started = await ProcessRunner.RunAsync(Shells.StartInfo([runtime, .. args], null),
            new ExecRequest { Command = runtime, Timeout = TimeSpan.FromMinutes(5) }, null, cancellationToken);
        if (started.ExitCode != 0)
        {
            if (egress is not null) await egress.DisposeAsync();
            throw new InvalidOperationException($"{runtime} could not start the sandbox container: {started.Output.Trim()}");
        }
        return new ContainerSandbox(spec, runtime, name, egress);
    }

    internal static Task<ExecResult> Run(string runtime, IEnumerable<string> args, CancellationToken ct) =>
        ProcessRunner.RunAsync(Shells.StartInfo([runtime, .. args], null), new ExecRequest { Command = runtime, Timeout = TimeSpan.FromMinutes(2) }, null, ct);

    /// <summary>The per-container internal network and the proxy on its gateway.</summary>
    internal sealed class Egress(string runtime, string network, IPAddress gateway, IPAddress container, EgressProxy proxy) : IAsyncDisposable
    {
        public string Network => network;
        public IPAddress ContainerAddress => container;
        public EgressProxy Proxy => proxy;

        public static async Task<Egress> CreateAsync(string runtime, string name, SandboxSpec spec, CancellationToken ct)
        {
            string network = name + "-egress";
            ExecResult created = await Run(runtime, ["network", "create", "--internal", network], ct);
            if (created.ExitCode != 0) throw new InvalidOperationException($"{runtime} could not create the sandbox network: {created.Output.Trim()}");
            try
            {
                ExecResult inspect = await Run(runtime, ["network", "inspect", network, "--format", "{{range .IPAM.Config}}{{.Gateway}} {{.Subnet}}{{end}}"], ct);
                string[] parts = inspect.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (inspect.ExitCode != 0 || parts.Length < 2 || !IPAddress.TryParse(parts[0], out IPAddress? gateway) || gateway.AddressFamily != AddressFamily.InterNetwork)
                    throw new InvalidOperationException($"{runtime} gave the sandbox network no IPv4 gateway: {inspect.Output.Trim()}");
                byte[] bytes = gateway.GetAddressBytes();
                bytes[3] = (byte)(bytes[3] + 1);   // the gateway is .1 of a fresh subnet; the container takes the next address
                IPAddress container = new(bytes);
                EgressProxy proxy;
                try { proxy = EgressProxy.StartTcp(gateway, container, spec.AllowHosts, spec.OnEgress); }
                catch (SocketException ex)
                {
                    // Rootless Podman keeps container networks out of the host's reach; there is nothing to listen on.
                    throw new InvalidOperationException($"network: allowlist needs the daemon to listen on the container network's gateway {gateway} ({ex.Message}); " +
                        "use Docker or rootful Podman, or a bubblewrap profile.");
                }
                return new Egress(runtime, network, gateway, container, proxy);
            }
            catch
            {
                await Run(runtime, ["network", "rm", network], CancellationToken.None);
                throw;
            }
        }

        public IReadOnlyDictionary<string, string> Environment()
        {
            string url = $"http://{gateway}:{proxy.Endpoint!.Port}";
            const string direct = "localhost,127.0.0.1,::1";
            return new Dictionary<string, string>
            {
                ["HTTP_PROXY"] = url, ["HTTPS_PROXY"] = url, ["ALL_PROXY"] = url, ["http_proxy"] = url, ["https_proxy"] = url, ["all_proxy"] = url,
                ["NO_PROXY"] = direct, ["no_proxy"] = direct,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await proxy.DisposeAsync();
            await Run(runtime, ["network", "rm", network], CancellationToken.None);
        }
    }
}

internal sealed class ContainerSandbox(SandboxSpec spec, string runtime, string container, ContainerSandboxProvider.Egress? egress) : ISandbox
{
    public string Id => container;
    public string Type => "container";
    public PathMap Paths { get; } = new([new MountSpec(spec.WorkspaceHostPath, spec.WorkspaceSandboxPath, MountMode.ReadWrite), .. spec.Mounts]);

    public Task<ExecResult> ExecAsync(ExecRequest request, CancellationToken ct) =>
        ProcessRunner.RunAsync(Shells.StartInfo(ExecArgs(request, interactive: request.StandardInput is not null), null), request, NoneSandbox.SpillDir(spec), ct);

    public Task<ISandboxProcess> StartAsync(ExecRequest request, CancellationToken ct) =>
        Task.FromResult<ISandboxProcess>(LocalSandboxProcess.Start(Shells.StartInfo(ExecArgs(request, interactive: true), null)));

    private List<string> ExecArgs(ExecRequest request, bool interactive)
    {
        List<string> a = [runtime, "exec"];
        if (interactive) a.Add("-i");
        a.AddRange(["-w", Paths.ToSandbox(request.WorkingDirectory ?? spec.WorkspaceHostPath)]);
        if (request.Environment is { } env) foreach ((string k, string v) in env) a.AddRange(["-e", $"{k}={v}"]);
        a.Add(container);
        a.AddRange(Shells.CommandLine(request, windows: false));
        return a;
    }

    public async ValueTask DisposeAsync()
    {
        using Process? p = Process.Start(new ProcessStartInfo(runtime, ["rm", "-f", container]) { RedirectStandardOutput = true, RedirectStandardError = true });
        if (p is not null) await p.WaitForExitAsync();
        // The network can only go once the container has left it.
        if (egress is not null) await egress.DisposeAsync();
    }
}
