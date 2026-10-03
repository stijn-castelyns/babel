using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Harness.Sandbox.Egress;

/// <summary>
/// Runs inside a <c>network: allowlist</c> sandbox, where the network namespace has only loopback: it listens on
/// <c>127.0.0.1:&lt;port&gt;</c>, relays every connection to the daemon's egress proxy through the bind-mounted Unix socket, and
/// runs the sandboxed command as its child (stdio inherited), exiting with its exit code. The harness binary carries it as
/// the hidden command <c>harness __egress-forward &lt;port&gt; &lt;socket&gt; -- &lt;command&gt; [args…]</c>.
/// </summary>
public static class EgressForwarder
{
    public const string Command = "__egress-forward";
    public const int Port = 3128;
    /// <summary>Where the folder holding the proxy socket appears inside the sandbox.</summary>
    public const string SandboxDirectory = "/harness/egress";
    public const string SocketName = "proxy.sock";

    /// <summary>Entry point for <c>harness __egress-forward</c>; <paramref name="args"/> excludes the command name.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        int separator = Array.IndexOf(args, "--");
        if (args.Length < 4 || separator != 2 || !int.TryParse(args[0], out int port))
        {
            await Console.Error.WriteLineAsync($"usage: harness {Command} <port> <socket> -- <command> [args...]");
            return 2;
        }
        string socketPath = args[1];
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => RelayAsync(client, socketPath));
            }
        });

        ProcessStartInfo psi = new(args[3]) { UseShellExecute = false };
        foreach (string a in args.Skip(4)) psi.ArgumentList.Add(a);
        Process child;
        try { child = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception ex)
        {
            await Console.Error.WriteLineAsync($"{args[3]}: {ex.Message}");
            return 127;
        }
        using Process owned = child;
        await child.WaitForExitAsync();
        return child.ExitCode;
    }

    private static async Task RelayAsync(TcpClient client, string socketPath)
    {
        using TcpClient _ = client;
        using Socket proxy = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await proxy.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            await using NetworkStream a = client.GetStream();
            await using NetworkStream b = new(proxy, ownsSocket: false);
            await Task.WhenAll(CopyAsync(a, b), CopyAsync(b, a));
        }
        catch (Exception ex) when (ex is IOException or SocketException) { }
    }

    private static async Task CopyAsync(NetworkStream from, NetworkStream to)
    {
        try
        {
            await from.CopyToAsync(to);
            to.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
    }

    /// <summary>The proxy settings a sandboxed command sees; loopback stays direct (it is the sandbox's own).</summary>
    public static IReadOnlyDictionary<string, string> Environment()
    {
        string url = $"http://127.0.0.1:{Port}";
        const string direct = "localhost,127.0.0.1,::1";
        return new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = url, ["HTTPS_PROXY"] = url, ["ALL_PROXY"] = url, ["http_proxy"] = url, ["https_proxy"] = url, ["all_proxy"] = url,
            ["NO_PROXY"] = direct, ["no_proxy"] = direct,
        };
    }

    /// <summary>
    /// How to start the forwarder inside a sandbox: the command line prefix, and the host folders it needs mounted read-only
    /// (at the same paths). A published single-file <c>harness</c> needs only itself; <c>dotnet harness.dll</c> needs the
    /// application folder and the .NET install.
    /// </summary>
    public static (IReadOnlyList<string> Prefix, IReadOnlyList<string> Mounts) Launcher()
    {
        string process = System.Environment.ProcessPath ?? throw new InvalidOperationException("cannot locate the harness executable.");
        string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string name = Path.GetFileNameWithoutExtension(process);
        if (name == "harness")
        {
            // A single-file publish runs alone; an apphost next to harness.dll also needs its folder and the runtime.
            bool singleFile = !File.Exists(Path.Combine(appDir, "harness.dll"));
            return ([process], singleFile ? [process] : [appDir, .. DotnetRoots(null)]);
        }
        string dll = Path.Combine(appDir, "harness.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException($"cannot find harness.dll in {appDir} to run the egress forwarder.");
        string dotnet = name == "dotnet" ? process : BubblewrapSandboxProvider.FindOnPath("dotnet")
            ?? throw new InvalidOperationException("cannot find dotnet to run the egress forwarder.");
        return ([dotnet, dll], [appDir, .. DotnetRoots(dotnet)]);
    }

    private static IEnumerable<string> DotnetRoots(string? dotnet)
    {
        HashSet<string> roots = [];
        if (dotnet is not null)
        {
            string real = new FileInfo(dotnet).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dotnet;
            roots.Add(Path.GetDirectoryName(real)!);
        }
        if (System.Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root) roots.Add(root);
        // System directories (/usr, /opt…) are mounted anyway.
        return roots.Where(r => !r.StartsWith("/usr/", StringComparison.Ordinal) && Directory.Exists(r));
    }
}
