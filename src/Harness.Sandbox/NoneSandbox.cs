using Harness.Sdk;

namespace Harness.Sandbox;

/// <summary>Host execution: the path policy and approvals are the only protection. Meant for interactive work on your own repo.</summary>
public sealed class NoneSandboxProvider : ISandboxProvider
{
    public string Type => "none";

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken) =>
        Task.FromResult<ISandbox>(new NoneSandbox(spec));
}

internal sealed class NoneSandbox(SandboxSpec spec) : ISandbox
{
    public string Id { get; } = "none-" + Guid.NewGuid().ToString("N")[..8];
    public string Type => "none";
    public PathMap Paths => PathMap.Identity;

    public Task<ExecResult> ExecAsync(ExecRequest request, CancellationToken ct)
    {
        var psi = Shells.StartInfo(Shells.CommandLine(request, OperatingSystem.IsWindows()), request.WorkingDirectory ?? spec.WorkspaceHostPath);
        foreach ((string k, string v) in spec.Environment) psi.Environment[k] = v;
        if (request.Environment is { } env) foreach ((string k, string v) in env) psi.Environment[k] = v;
        return ProcessRunner.RunAsync(psi, request, SpillDir(spec), ct);
    }

    public Task<ISandboxProcess> StartAsync(ExecRequest request, CancellationToken ct)
    {
        var psi = Shells.StartInfo(Shells.CommandLine(request, OperatingSystem.IsWindows()), request.WorkingDirectory ?? spec.WorkspaceHostPath);
        foreach ((string k, string v) in spec.Environment) psi.Environment[k] = v;
        if (request.Environment is { } env) foreach ((string k, string v) in env) psi.Environment[k] = v;
        return Task.FromResult<ISandboxProcess>(LocalSandboxProcess.Start(psi));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static string SpillDir(SandboxSpec spec) => Path.Combine(spec.WorkspaceHostPath, ".harness", "spill");
}
