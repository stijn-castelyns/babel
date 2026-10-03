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
        return Task.FromResult<ISandbox>(new BubblewrapSandbox(spec, bwrap));
    }

    internal static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
}

internal sealed class BubblewrapSandbox : ISandbox
{
    private static readonly string[] SystemDirs = ["/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/etc", "/opt", "/nix"];
    private readonly SandboxSpec _spec;
    private readonly string _bwrap;

    public BubblewrapSandbox(SandboxSpec spec, string bwrap)
    {
        _spec = spec;
        _bwrap = bwrap;
        Paths = new PathMap([new MountSpec(spec.WorkspaceHostPath, spec.WorkspaceSandboxPath, MountMode.ReadWrite), .. spec.Mounts]);
    }

    public string Id { get; } = "bwrap-" + Guid.NewGuid().ToString("N")[..8];
    public string Type => "bubblewrap";
    public PathMap Paths { get; }

    public Task<ExecResult> ExecAsync(ExecRequest request, CancellationToken ct) =>
        ProcessRunner.RunAsync(Shells.StartInfo(Arguments(request), null), request, NoneSandbox.SpillDir(_spec), ct);

    public Task<ISandboxProcess> StartAsync(ExecRequest request, CancellationToken ct) =>
        Task.FromResult<ISandboxProcess>(LocalSandboxProcess.Start(Shells.StartInfo(Arguments(request), null)));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The full <c>bwrap</c> command line for one request.</summary>
    internal IReadOnlyList<string> Arguments(ExecRequest request)
    {
        List<string> a = [_bwrap, "--die-with-parent", "--new-session", "--unshare-all"];
        // Allowlisted egress needs the daemon's proxy, which is not built yet; until then allowlist means no network.
        if (_spec.Network == NetworkMode.Full) a.Add("--share-net");

        foreach (string dir in SystemDirs)
            if (Directory.Exists(dir)) a.AddRange(["--ro-bind", dir, dir]);
        a.AddRange(["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--tmpfs", "/home", "--dir", "/home/agent"]);

        foreach (MountSpec m in Paths.Mounts.OrderBy(m => m.SandboxPath.Length))
        {
            if (!Directory.Exists(m.HostPath) && !File.Exists(m.HostPath)) continue;
            a.AddRange([m.Mode == MountMode.ReadWrite ? "--bind" : "--ro-bind", m.HostPath, m.SandboxPath]);
        }

        a.AddRange(["--clearenv",
            "--setenv", "HOME", "/home/agent",
            "--setenv", "USER", "agent",
            "--setenv", "PATH", "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            "--setenv", "LANG", "C.UTF-8",
            "--setenv", "TERM", "dumb"]);
        foreach ((string k, string v) in _spec.Environment) a.AddRange(["--setenv", k, v]);
        if (request.Environment is { } env) foreach ((string k, string v) in env) a.AddRange(["--setenv", k, v]);

        string workdir = Paths.ToSandbox(request.WorkingDirectory ?? _spec.WorkspaceHostPath);
        a.AddRange(["--chdir", workdir, "--"]);
        a.AddRange(Shells.CommandLine(request, windows: false));
        return a;
    }
}
