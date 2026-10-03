using Harness.Sandbox;
using Harness.Sdk;

namespace Harness.Tests;

public sealed class SandboxTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("harness-sbx-").FullName;
    public void Dispose() => Directory.Delete(_ws, recursive: true);

    private SandboxSpec Spec(string type, NetworkMode network = NetworkMode.None) =>
        new() { Name = "t", Type = type, WorkspaceHostPath = _ws, Network = network };

    [Fact]
    public async Task None_sandbox_captures_head_and_tail_and_spills_the_rest()
    {
        await using ISandbox sandbox = await new NoneSandboxProvider().CreateAsync(Spec("none"), default);
        ExecResult small = await sandbox.ExecAsync(new ExecRequest { Command = "echo hi; exit 3" }, default);
        Assert.Equal(3, small.ExitCode);
        Assert.Equal("hi", small.Output.Trim());

        ExecResult big = await sandbox.ExecAsync(new ExecRequest { Command = "seq 1 100000", OutputHeadTailBytes = 1024 }, default);
        Assert.True(big.Truncated);
        Assert.StartsWith("1\n2\n", big.Output);
        Assert.EndsWith("100000\n", big.Output);
        Assert.NotNull(big.FullOutputPath);
        Assert.EndsWith("100000\n", File.ReadAllText(big.FullOutputPath!));
    }

    [Fact]
    public async Task Timeouts_kill_the_command()
    {
        await using ISandbox sandbox = await new NoneSandboxProvider().CreateAsync(Spec("none"), default);
        ExecResult r = await sandbox.ExecAsync(new ExecRequest { Command = "sleep 30", Timeout = TimeSpan.FromMilliseconds(300) }, default);
        Assert.True(r.TimedOut);
        Assert.True(r.Duration < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Bubblewrap_maps_the_workspace_and_isolates_network_and_files()
    {
        if (!OperatingSystem.IsLinux() || BubblewrapSandboxProvider.FindOnPath("bwrap") is null) return;
        await using ISandbox sandbox = await new BubblewrapSandboxProvider().CreateAsync(Spec("bubblewrap"), default);
        File.WriteAllText(Path.Combine(_ws, "in.txt"), "inside");

        ExecResult pwd = await sandbox.ExecAsync(new ExecRequest { Command = "pwd; cat in.txt; echo out > out.txt" }, default);
        Assert.Equal(0, pwd.ExitCode);
        Assert.Equal("/workspace\ninside", pwd.Output.Trim());
        Assert.Equal("out", File.ReadAllText(Path.Combine(_ws, "out.txt")).Trim());

        // No network namespace routes: only loopback exists.
        ExecResult net = await sandbox.ExecAsync(new ExecRequest { Command = "cat /proc/net/dev | tail -n +3 | cut -d: -f1 | tr -d ' '" }, default);
        Assert.Equal("lo", net.Output.Trim());

        // The host's home (where ~/.harness and its secrets live) is not visible.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ExecResult ls = await sandbox.ExecAsync(new ExecRequest { Command = $"ls {home} 2>&1 || true" }, default);
        Assert.DoesNotContain(".harness", ls.Output);

        // System directories are read-only.
        ExecResult write = await sandbox.ExecAsync(new ExecRequest { Command = "touch /usr/x 2>&1; echo $?" }, default);
        Assert.NotEqual("0", write.Output.Trim().Split('\n')[^1]);
    }

    [Fact]
    public void Path_map_translates_both_ways()
    {
        PathMap map = new([new MountSpec("/home/u/src/api", "/workspace", MountMode.ReadWrite), new MountSpec("/home/u/.nuget", "/home/agent/.nuget", MountMode.ReadOnly)]);
        Assert.Equal("/workspace/src/a.cs", map.ToSandbox("/home/u/src/api/src/a.cs"));
        Assert.Equal("/workspace", map.ToSandbox("/home/u/src/api"));
        Assert.Equal("/home/u/src/api/x", map.ToHost("/workspace/x"));
        Assert.Equal("/other", map.ToSandbox("/other"));
        Assert.Equal("/home/u/src/apiary", map.ToSandbox("/home/u/src/apiary"));
    }
}
