using System.Diagnostics;
using System.Globalization;
using Harness.Sdk;

namespace Harness.Sandbox;

/// <summary>
/// One Docker or Podman container per run, with <c>exec</c> per command and CPU, memory and PID limits.
/// The container runtime socket is never mounted into the container.
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
        // The egress proxy is wired into bubblewrap only so far; for containers allowlist still means no network (fail closed).
        if (spec.Network != NetworkMode.Full) args.AddRange(["--network", "none"]);
        if (spec.Limits.Cpus is double cpus) args.AddRange(["--cpus", cpus.ToString(CultureInfo.InvariantCulture)]);
        if (spec.Limits.MemoryMb is int mem) args.AddRange(["--memory", $"{mem}m"]);
        if (spec.Limits.Pids is int pids) args.AddRange(["--pids-limit", pids.ToString(CultureInfo.InvariantCulture)]);
        foreach ((string k, string v) in spec.Environment) args.AddRange(["-e", $"{k}={v}"]);
        args.AddRange([image, "sleep", "infinity"]);

        ExecResult started = await ProcessRunner.RunAsync(Shells.StartInfo([runtime, .. args], null),
            new ExecRequest { Command = runtime, Timeout = TimeSpan.FromMinutes(5) }, null, cancellationToken);
        if (started.ExitCode != 0)
            throw new InvalidOperationException($"{runtime} could not start the sandbox container: {started.Output.Trim()}");
        return new ContainerSandbox(spec, runtime, name);
    }
}

internal sealed class ContainerSandbox(SandboxSpec spec, string runtime, string container) : ISandbox
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
    }
}
