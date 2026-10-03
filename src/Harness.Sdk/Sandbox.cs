namespace Harness.Sdk;

/// <summary>Creates sandboxes of one type ("none", "bubblewrap", "container", or a plugin type).</summary>
public interface ISandboxProvider
{
    string Type { get; }
    Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken);
}

/// <summary>A per-run execution environment. Everything that executes code goes through one.</summary>
public interface ISandbox : IAsyncDisposable
{
    string Id { get; }
    string Type { get; }
    PathMap Paths { get; }
    /// <summary>Runs a command to completion (shell, skill scripts).</summary>
    Task<ExecResult> ExecAsync(ExecRequest request, CancellationToken cancellationToken);
    /// <summary>Starts a long-lived process with piped stdio (stdio MCP servers, background jobs).</summary>
    Task<ISandboxProcess> StartAsync(ExecRequest request, CancellationToken cancellationToken);
}

public interface ISandboxProcess : IAsyncDisposable
{
    int? ProcessId { get; }
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

public enum NetworkMode { None, Allowlist, Full }

public enum MountMode { ReadOnly, ReadWrite }

public sealed record MountSpec(string HostPath, string SandboxPath, MountMode Mode);

public sealed record SandboxLimits(double? Cpus = null, int? MemoryMb = null, int? Pids = null, int? WallClockMinutes = null);

/// <summary>A resolved sandbox profile: placeholders such as <c>{workspace}</c> are already expanded.</summary>
public sealed record SandboxSpec
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string WorkspaceHostPath { get; init; }
    public string WorkspaceSandboxPath { get; init; } = "/workspace";
    public NetworkMode Network { get; init; } = NetworkMode.Full;
    public IReadOnlyList<string> AllowHosts { get; init; } = [];
    public IReadOnlyList<MountSpec> Mounts { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public SandboxLimits Limits { get; init; } = new();
    public string? Image { get; init; }
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}

/// <summary>Translates between host paths and the paths a sandboxed process sees.</summary>
public sealed class PathMap
{
    private readonly IReadOnlyList<MountSpec> _mounts;

    public PathMap(IReadOnlyList<MountSpec> mounts) =>
        _mounts = [.. mounts.OrderByDescending(m => m.HostPath.Length)];

    public static PathMap Identity { get; } = new([]);

    public IReadOnlyList<MountSpec> Mounts => _mounts;

    public string ToSandbox(string hostPath)
    {
        foreach (MountSpec m in _mounts)
            if (IsUnder(hostPath, m.HostPath))
                return Join(m.SandboxPath, Path.GetRelativePath(m.HostPath, hostPath));
        return hostPath;
    }

    public string ToHost(string sandboxPath)
    {
        foreach (MountSpec m in _mounts.OrderByDescending(m => m.SandboxPath.Length))
            if (IsUnder(sandboxPath, m.SandboxPath))
                return Path.GetFullPath(Path.Combine(m.HostPath, Path.GetRelativePath(m.SandboxPath, sandboxPath)));
        return sandboxPath;
    }

    private static bool IsUnder(string path, string root) =>
        path == root || path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal)
        || (OperatingSystem.IsWindows() && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    private static string Join(string root, string relative) =>
        relative == "." ? root : root.TrimEnd('/') + "/" + relative.Replace('\\', '/');
}

public sealed record ExecRequest
{
    public required string Command { get; init; }
    /// <summary>When set, the command is executed directly with these arguments instead of through the shell.</summary>
    public IReadOnlyList<string>? Arguments { get; init; }
    /// <summary>Host path of the working directory; the sandbox maps it.</summary>
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Bytes of combined output kept from the start and from the end each.</summary>
    public int OutputHeadTailBytes { get; init; } = 8 * 1024;
    public string? StandardInput { get; init; }
}

public sealed record ExecResult(int ExitCode, string Output, bool TimedOut, bool Truncated, long TotalOutputBytes, string? FullOutputPath, TimeSpan Duration);
