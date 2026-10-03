using System.Collections.Concurrent;
using System.Globalization;
using Harness.Sandbox.Egress;
using Harness.Sdk;

namespace Harness.Sandbox;

/// <summary>One check of <c>harness sandbox test</c>: <c>pass</c>, <c>fail</c> or <c>warn</c> (not enforced, or not checkable).</summary>
public sealed record ProbeResult(string Check, string Status, string Detail);

/// <summary>
/// <c>harness sandbox test &lt;profile&gt;</c>: starts the profile against a scratch workspace and checks that its promises hold
/// from the inside: the workspace is mounted read-write, system directories and read-only mounts cannot be written, the
/// harness home (secrets, sessions) is invisible, the network matches <c>none | allowlist | full</c>, and limits are applied.
/// </summary>
public sealed class SandboxProbe(SandboxFactory factory)
{
    public async Task<IReadOnlyList<ProbeResult>> RunAsync(string profile, string harnessHome, CancellationToken ct)
    {
        List<ProbeResult> results = [];
        string workspace = Directory.CreateTempSubdirectory("harness-probe-").FullName;
        try
        {
            ConcurrentQueue<EgressAttempt> egress = new();
            SandboxSpec spec = factory.Resolve(profile, workspace) with { OnEgress = egress.Enqueue };
            await using ISandbox sandbox = await factory.CreateAsync(spec, ct);
            bool isolated = spec.Type != "none";

            async Task<ExecResult> Sh(string command) =>
                await sandbox.ExecAsync(new ExecRequest { Command = command, Timeout = TimeSpan.FromSeconds(30) }, ct);
            void Add(string check, bool ok, string detail, bool warnOnly = false) =>
                results.Add(new ProbeResult(check, ok ? "pass" : warnOnly ? "warn" : "fail", detail));

            ExecResult hello = await Sh("echo harness-probe");
            Add("runs commands", hello.ExitCode == 0 && hello.Output.Contains("harness-probe"), hello.ExitCode == 0 ? $"{spec.Type} sandbox" : hello.Output.Trim());
            if (hello.ExitCode != 0) return results;

            string inside = sandbox.Paths.ToSandbox(workspace);
            ExecResult write = await Sh($"echo written > '{inside}/.probe' && cat '{inside}/.probe'");
            bool seen = File.Exists(Path.Combine(workspace, ".probe")) && File.ReadAllText(Path.Combine(workspace, ".probe")).Trim() == "written";
            Add("workspace is read-write", write.ExitCode == 0 && seen, $"{inside} → {workspace}");

            ExecResult system = await Sh("touch /usr/.harness-probe 2>/dev/null && echo writable || echo read-only");
            Add("system directories are read-only", system.Output.Trim() == "read-only",
                system.Output.Trim() == "read-only" ? "/usr cannot be written" : isolated ? "/usr is writable" : "host execution: nothing is isolated", warnOnly: !isolated);

            ExecResult home = await Sh($"test -e '{harnessHome}' && echo visible || echo hidden");
            Add("harness home is hidden", home.Output.Trim() == "hidden",
                home.Output.Trim() == "hidden" ? "secrets and sessions are out of reach" : $"{harnessHome} is visible inside the sandbox", warnOnly: !isolated);

            foreach (MountSpec mount in spec.Mounts)
            {
                if (!Directory.Exists(mount.HostPath) && !File.Exists(mount.HostPath))
                {
                    Add($"mount {mount.SandboxPath}", false, $"{mount.HostPath} does not exist on the host, so it is not mounted", warnOnly: true);
                    continue;
                }
                if (mount.Mode == MountMode.ReadOnly)
                {
                    ExecResult ro = await Sh($"test -e '{mount.SandboxPath}' || echo missing; touch '{mount.SandboxPath}/.harness-probe' 2>/dev/null && rm -f '{mount.SandboxPath}/.harness-probe' && echo writable || echo read-only");
                    string state = ro.Output.Trim().Split('\n')[^1];
                    Add($"mount {mount.SandboxPath} (ro)", !ro.Output.Contains("missing") && state == "read-only", ro.Output.Contains("missing") ? "not visible" : state, warnOnly: !isolated);
                }
                else
                {
                    ExecResult rw = await Sh($"test -d '{mount.SandboxPath}' && echo present || echo missing");
                    Add($"mount {mount.SandboxPath} (rw)", rw.Output.Trim() == "present", rw.Output.Trim());
                }
            }

            results.AddRange(await NetworkAsync(spec, Sh, egress));
            results.AddRange(await LimitsAsync(spec, Sh));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            results.Add(new ProbeResult("starts", "fail", ex.Message));
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
        return results;
    }

    /// <summary>Bash that tries a TCP connection and prints <c>open</c> or <c>closed</c>.</summary>
    private static string Tcp(string host, int port) =>
        $"(timeout 5 bash -c 'exec 3<>/dev/tcp/{host}/{port}') 2>/dev/null && echo open || echo closed";

    /// <summary>Bash that asks the in-sandbox proxy for a tunnel and prints the status line.</summary>
    private static string Connect(string host, int port) =>
        $"timeout 20 bash -c 'exec 3<>/dev/tcp/127.0.0.1/{EgressForwarder.Port} && printf \"CONNECT {host}:{port} HTTP/1.1\\r\\nHost: {host}:{port}\\r\\n\\r\\n\" >&3 && head -n 1 <&3' 2>&1 | tr -d '\\r'";

    private static async Task<IEnumerable<ProbeResult>> NetworkAsync(SandboxSpec spec, Func<string, Task<ExecResult>> sh, ConcurrentQueue<EgressAttempt> egress)
    {
        List<ProbeResult> results = [];
        // A public anycast resolver: if this opens, the sandbox has a direct route out.
        string direct = (await sh(Tcp("1.1.1.1", 53))).Output.Trim();
        switch (spec.Network)
        {
            case NetworkMode.None:
                results.Add(new ProbeResult("network: none", direct == "closed" ? "pass" : "fail", direct == "closed" ? "no route out" : "a direct connection to 1.1.1.1:53 succeeded"));
                break;
            case NetworkMode.Full:
                results.Add(new ProbeResult("network: full", direct == "open" ? "pass" : "warn", direct == "open" ? "direct connections work" : "1.1.1.1:53 is unreachable (the host may need a proxy)"));
                break;
            case NetworkMode.Allowlist:
                results.Add(new ProbeResult("allowlist: no direct route", direct == "closed" ? "pass" : "fail", direct == "closed" ? "only the egress proxy is reachable" : "a direct connection to 1.1.1.1:53 succeeded"));
                if (spec.Type != "bubblewrap")
                {
                    results.Add(new ProbeResult("allowlist: egress proxy", "warn", $"the {spec.Type} provider has no egress proxy yet; allowlist means no network"));
                    break;
                }
                foreach (string host in spec.AllowHosts.Where(h => !h.StartsWith("*.", StringComparison.Ordinal)).Take(3))
                {
                    string name = host, portText = "443";
                    if (host.LastIndexOf(':') is int colon and > 0 && host.Count(c => c == ':') == 1) (name, portText) = (host[..colon], host[(colon + 1)..]);
                    int port = int.Parse(portText, CultureInfo.InvariantCulture);
                    string status = (await sh(Connect(name, port))).Output.Trim().Split('\n')[0];
                    bool ok = status.StartsWith("HTTP/1.1 200", StringComparison.Ordinal);
                    results.Add(new ProbeResult($"allowlist: {name}:{port}", ok ? "pass" : "fail", ok ? "reachable through the proxy" : status.Length > 0 ? status : "no answer from the proxy"));
                }
                const string Outside = "harness-probe.invalid";
                string refused = (await sh(Connect(Outside, 443))).Output.Trim().Split('\n')[0];
                bool denied = refused.StartsWith("HTTP/1.1 403", StringComparison.Ordinal) && egress.Any(e => e.Host == Outside && !e.Allowed);
                results.Add(new ProbeResult("allowlist: other hosts refused", denied ? "pass" : "fail", denied ? $"{Outside}:443 got 403" : refused));
                break;
        }
        return results;
    }

    private static async Task<IEnumerable<ProbeResult>> LimitsAsync(SandboxSpec spec, Func<string, Task<ExecResult>> sh)
    {
        List<ProbeResult> results = [];
        SandboxLimits l = spec.Limits;
        if (l.WallClockMinutes is int minutes) results.Add(new ProbeResult("limit: wall clock", "pass", $"{minutes} min, enforced by the run's time limit"));
        if (l.Cpus is null && l.MemoryMb is null && l.Pids is null) return results;
        if (spec.Type != "container")
        {
            results.Add(new ProbeResult("limits: cpus, memory, pids", "warn", $"the {spec.Type} provider does not enforce them; use a container profile"));
            return results;
        }
        string output = (await sh("cat /sys/fs/cgroup/memory.max /sys/fs/cgroup/pids.max /sys/fs/cgroup/cpu.max 2>/dev/null")).Output;
        string[] values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string Value(int i) => i < values.Length ? values[i] : "";
        if (l.MemoryMb is int mb)
            results.Add(Check("limit: memory", Value(0) == ((long)mb * 1024 * 1024).ToString(CultureInfo.InvariantCulture), $"memory.max = {Value(0)}"));
        if (l.Pids is int pids)
            results.Add(Check("limit: pids", Value(1) == pids.ToString(CultureInfo.InvariantCulture), $"pids.max = {Value(1)}"));
        if (l.Cpus is double cpus)
        {
            string[] quota = Value(2).Split(' ');
            bool ok = quota.Length == 2 && double.TryParse(quota[0], CultureInfo.InvariantCulture, out double q) && double.TryParse(quota[1], CultureInfo.InvariantCulture, out double period)
                && Math.Abs(q / period - cpus) < 0.01;
            results.Add(Check("limit: cpus", ok, $"cpu.max = {Value(2)}"));
        }
        return results;
    }

    private static ProbeResult Check(string name, bool ok, string detail) => new(name, ok ? "pass" : "fail", detail);
}
