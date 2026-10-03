using Harness.Core.Sessions;
using Harness.Runs;
using Harness.Sdk;
using Harness.Tests.TestSupport;
using Microsoft.Extensions.AI;

namespace Harness.Tests;

/// <summary>Phase 4 trigger behaviour beyond the basics: run chaining, coalescing, concurrency, rate limits and budgets.</summary>
public class TriggerPolicyTests
{
    private static async Task<RunResult> FireAndWaitAsync(TestHome home, string trigger, string text = "go")
    {
        RunRecord run = await home.Triggers.FireAsync(trigger, text, null, CancellationToken.None);
        return await home.Orchestrator.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static IReadOnlyList<RunRecord> Runs(TestHome home, string trigger) =>
        [.. home.Orchestrator.Sessions.Index.ListRuns(limit: 1000).Where(r => r.TriggerId == trigger)];

    [Fact]
    public async Task Run_completed_source_chains_runs_by_state_and_stops_loops()
    {
        await using TestHome home = new();
        home.WriteTemplate("t", ("template.yaml", "sandbox: none\noutput: { kind: json, retries: 0 }\n"));
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "build.yaml"), "template: t\nallowUnsandboxed: true\n");
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "on-success.yaml"), $$"""
            source: { type: run-completed, trigger: build }
            workspace: {{home.Workspace}}
            allowUnsandboxed: true
            prompt: "Build {event.data.run.state}: {event.data.run.output.summary}"
            """);
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "on-failure.yaml"), $$"""
            source: { type: run-completed, triggers: [build], states: [invalid_output, failed] }
            workspace: {{home.Workspace}}
            allowUnsandboxed: true
            prompt: "Investigate: {event.text}"
            """);
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "loop.yaml"), $$"""
            source: { type: run-completed, triggers: [loop], states: any }
            workspace: {{home.Workspace}}
            allowUnsandboxed: true
            """);
        List<string> prompts = [];
        bool submit = true;
        home.Build(new ScriptedChatClient((messages, _) =>
        {
            string last = messages.Last(m => m.Role == ChatRole.User).Text;
            if (messages.Any(m => m.Role == ChatRole.Tool)) return ScriptedChatClient.Text("done");
            lock (prompts) prompts.Add(last);
            return submit && last == "go" ? ScriptedChatClient.Call("submit_output", new() { ["summary"] = "all green" }) : ScriptedChatClient.Text("no output");
        }));
        await home.Triggers.StartAsync(CancellationToken.None);

        RunResult build = await FireAndWaitAsync(home, "build");
        Assert.True(build.State == RunStates.Succeeded, build.Error);
        RunRecord follow = await RunOrchestratorTests.WaitForAsync(() => Runs(home, "on-success").FirstOrDefault(r => RunStates.IsFinal(r.State)));
        Assert.Equal(RunStates.Succeeded, follow.State);
        Assert.Contains("Build succeeded: all green", prompts);
        Assert.Empty(Runs(home, "on-failure"));

        submit = false;
        RunResult broken = await FireAndWaitAsync(home, "build");
        Assert.Equal(RunStates.InvalidOutput, broken.State);
        await RunOrchestratorTests.WaitForAsync(() => Runs(home, "on-failure").FirstOrDefault(r => RunStates.IsFinal(r.State)));
        Assert.Contains("Investigate: No valid output after 1 attempt(s): You stopped without calling submit_output.", prompts);
        Assert.Single(Runs(home, "on-success"));

        // A trigger that fires on its own runs stops after five chained runs.
        await FireAndWaitAsync(home, "loop");
        await RunOrchestratorTests.WaitForAsync(() => Runs(home, "loop").Count == 6 && Runs(home, "loop").All(r => RunStates.IsFinal(r.State)) ? "ok" : null);
        await Task.Delay(500);
        Assert.Equal(6, Runs(home, "loop").Count);
    }
}
