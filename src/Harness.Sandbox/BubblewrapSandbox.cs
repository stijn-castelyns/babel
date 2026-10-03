using Harness.Sandbox.Egress;
using Harness.Sdk;

namespace Harness.Sandbox;

/// <summary>
/// Per-command <c>bwrap</c>: system directories read-only, the workspace read-write, private <c>/tmp</c> and home,
/// every namespace unshared (network too, unless the profile asks for <c>full</c>), and the sandbox dies with its parent.
/// </summary>
public sealed class BubblewrapSandboxProvider : ISandboxProvider
{
    public string Type => "bubblewrap";

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The bubblewrap sandbox is only available on Linux.");
        string bwrap = FindOnPath("bwrap") ?? throw new InvalidOperationException("bwrap is not installed. Install the 'bubblewrap' package.");
        if (spec.Network != NetworkMode.Allowlist) return Task.FromResult<ISandbox>(new BubblewrapSandbox(spec, bwrap, null));

        // The network stays unshared; the daemon's egress proxy is the only way out, through a socket in a private folder.
        string folder = Path.Combine(Path.GetTempPath(), "harness-egress-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        EgressProxy proxy = EgressProxy.Start(Path.Combine(folder, EgressForwarder.SocketName), spec.AllowHosts, spec.OnEgress);
        return Task.FromResult<ISandbox>(new BubblewrapSandbox(spec, bwrap, new EgressSetup(folder, proxy, EgressForwarder.Launcher())));
    }

    internal static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
}

/// <summary>The egress proxy of an allowlisted sandbox, the folder holding its socket, and how to start the forwarder.</summary>
internal sealed record EgressSetup(string Folder, EgressProxy Proxy, (IReadOnlyList<string> Prefix, IReadOnlyList<string> Mounts) Launcher);

internal sealed class BubblewrapSandbox : ISandbox
{
    private static readonly string[] SystemDirs = ["/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/etc", "/opt", "/nix"];
    private readonly SandboxSpec _spec;
    private readonly string _bwrap;
    private readonly EgressSetup? _egress;

    public BubblewrapSandbox(SandboxSpec spec, string bwrap, EgressSetup? egress)
    {
        _spec = spec;
        _bwrap = bwrap;
        _egress = egress;
        Paths = new PathMap([new MountSpec(spec.WorkspaceHostPath, spec.WorkspaceSandboxPath, MountMode.ReadWrite), .. spec.Mounts]);
    }

    public string Id { get; } = "bwrap-" + Guid.NewGuid().ToString("N")[..8];
    public string Type => "bubblewrap";
    public PathMap Paths { get; }

    public Task<ExecResult> ExecAsync(ExecRequest request, CancellationToken ct) =>
        ProcessRunner.RunAsync(Shells.StartInfo(Arguments(request), null), request, NoneSandbox.SpillDir(_spec), ct);

    public Task<ISandboxProcess> StartAsync(ExecRequest request, CancellationToken ct) =>
        Task.FromResult<ISandboxProcess>(LocalSandboxProcess.Start(Shells.StartInfo(Arguments(request), null)));

    public async ValueTask DisposeAsync()
    {
        if (_egress is null) return;
        await _egress.Proxy.DisposeAsync();
        try { Directory.Delete(_egress.Folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>The full <c>bwrap</c> command line for one request.</summary>
    internal IReadOnlyList<string> Arguments(ExecRequest request)
    {
        List<string> a = [_bwrap, "--die-with-parent", "--new-session", "--unshare-all"];
        // Allowlisted egress keeps the network unshared: the only route out is the daemon's proxy (see EgressSetup).
        if (_spec.Network == NetworkMode.Full) a.Add("--share-net");

        foreach (string dir in SystemDirs)
            if (Directory.Exists(dir)) a.AddRange(["--ro-bind", dir, dir]);
        a.AddRange(["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--tmpfs", "/home", "--dir", "/home/agent"]);

        foreach (MountSpec m in Paths.Mounts.OrderBy(m => m.SandboxPath.Length))
        {
            if (!Directory.Exists(m.HostPath) && !File.Exists(m.HostPath)) continue;
            a.AddRange([m.Mode == MountMode.ReadWrite ? "--bind" : "--ro-bind", m.HostPath, m.SandboxPath]);
        }
        if (_egress is not null)
        {
            a.AddRange(["--bind", _egress.Folder, EgressForwarder.SandboxDirectory]);
            foreach (string path in _egress.Launcher.Mounts) a.AddRange(["--ro-bind", path, path]);
        }

        a.AddRange(["--clearenv",
            "--setenv", "HOME", "/home/agent",
            "--setenv", "USER", "agent",
            "--setenv", "PATH", "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            "--setenv", "LANG", "C.UTF-8",
            "--setenv", "TERM", "dumb"]);
        if (_egress is not null)
        {
            foreach ((string k, string v) in EgressForwarder.Environment()) a.AddRange(["--setenv", k, v]);
            if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root) a.AddRange(["--setenv", "DOTNET_ROOT", root]);
        }
        foreach ((string k, string v) in _spec.Environment) a.AddRange(["--setenv", k, v]);
        if (request.Environment is { } env) foreach ((string k, string v) in env) a.AddRange(["--setenv", k, v]);

        string workdir = Paths.ToSandbox(request.WorkingDirectory ?? _spec.WorkspaceHostPath);
        a.AddRange(["--chdir", workdir, "--"]);
        if (_egress is not null)
            a.AddRange([.. _egress.Launcher.Prefix, EgressForwarder.Command, EgressForwarder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EgressForwarder.SandboxDirectory + "/" + EgressForwarder.SocketName, "--"]);
        a.AddRange(Shells.CommandLine(request, windows: false));
        return a;
    }
}
