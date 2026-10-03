using System.Text.Json.Nodes;
using Harness.Core.Sessions;
using Harness.Extensions.Plugins;
using Harness.Runs;
using Harness.Sdk;
using Harness.Tests.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>A model that records every prompt and answers "ok".</summary>
    private static ScriptedChatClient Recording(List<string> prompts, Func<string, ChatResponse>? reply = null) => new((messages, _) =>
    {
        string last = messages.Last(m => m.Role == ChatRole.User).Text;
        if (messages.Any(m => m.Role == ChatRole.Tool)) return ScriptedChatClient.Text("done");
        lock (prompts) prompts.Add(last);
        return reply?.Invoke(last) ?? ScriptedChatClient.Text("ok");
    });

    private static async Task<TestHome> ChatHomeAsync(string triggerYaml, ScriptedChatClient model)
    {
        TestHome home = new();
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "chat.yaml"), $"""
            source: {"{"} type: test-messages {"}"}
            workspace: {home.Workspace}
            allowUnsandboxed: true
            {triggerYaml}
            """);
        home.Build(model);
        home.Services!.GetRequiredService<PluginRegistry>().Add(new MessagesPlugin());
        await home.Triggers.StartAsync(CancellationToken.None);
        return home;
    }

    [Fact]
    public async Task Coalesce_merges_quick_messages_per_conversation_and_survives_a_reload()
    {
        List<string> prompts = [];
        await using TestHome home = await ChatHomeAsync("coalesce: 0.4s\nsession: \"chat:{event.sender}\"", Recording(prompts));

        await MessagesSource.Current!.SendAsync("+1", "first");
        await MessagesSource.Current!.SendAsync("+2", "other person");
        await MessagesSource.Current!.SendAsync("+1", "second");
        await Task.Delay(150);
        await MessagesSource.Current!.SendAsync("+1", "third");
        Assert.Empty(Runs(home, "chat"));   // nothing runs while messages keep arriving

        await RunOrchestratorTests.WaitForAsync(() => Runs(home, "chat").Count(r => RunStates.IsFinal(r.State)) == 2 ? "ok" : null);
        Assert.Contains("first\nsecond\nthird", prompts);
        Assert.Contains("other person", prompts);

        // Messages still waiting in a batch when the engine stops stay queued, and the next start coalesces them again.
        await MessagesSource.Current!.SendAsync("+1", "before reload");
        await MessagesSource.Current!.SendAsync("+1", "also before");
        await home.Triggers.ReloadAsync(CancellationToken.None);
        await RunOrchestratorTests.WaitForAsync(() => Runs(home, "chat").Count(r => RunStates.IsFinal(r.State)) == 3 ? "ok" : null);
        Assert.Contains("before reload\nalso before", prompts);
        await Task.Delay(800);
        Assert.Equal(3, Runs(home, "chat").Count);
    }

    [Fact]
    public async Task Concurrency_queues_runs_beyond_the_global_limit()
    {
        using ManualResetEventSlim release = new();
        int running = 0, peak = 0;
        List<string> prompts = [];
        await using TestHome home = await ChatHomeAsync("concurrency: { global: 1 }", Recording(prompts, _ =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            release.Wait(TimeSpan.FromSeconds(20));
            Interlocked.Decrement(ref running);
            return ScriptedChatClient.Text("ok");
        }));

        RunRecord first = await home.Triggers.FireAsync("chat", "one", null, CancellationToken.None);
        RunRecord second = await home.Triggers.FireAsync("chat", "two", null, CancellationToken.None);
        await RunOrchestratorTests.WaitForAsync(() => home.Orchestrator.Sessions.Open(second.SessionId).ReadEvents()
            .FirstOrDefault(e => e.Type == EventTypes.RunState && e.Data["notice"]?.ToString()?.StartsWith("Waiting for a concurrency slot of trigger 'chat'", StringComparison.Ordinal) == true));
        Assert.Equal(RunStates.Queued, home.Orchestrator.Get(second.Id)!.State);

        release.Set();
        Assert.Equal(RunStates.Succeeded, (await home.Orchestrator.WaitAsync(first.Id).WaitAsync(TimeSpan.FromSeconds(30))).State);
        Assert.Equal(RunStates.Succeeded, (await home.Orchestrator.WaitAsync(second.Id).WaitAsync(TimeSpan.FromSeconds(30))).State);
        Assert.Equal(1, peak);

        // A queued run can be cancelled before it ever starts.
        release.Reset();
        RunRecord third = await home.Triggers.FireAsync("chat", "three", null, CancellationToken.None);
        RunRecord fourth = await home.Triggers.FireAsync("chat", "four", null, CancellationToken.None);
        await RunOrchestratorTests.WaitForAsync(() => prompts.Contains("three") ? "ok" : null);
        Assert.True(home.Orchestrator.Cancel(fourth.Id));
        Assert.Equal(RunStates.Cancelled, (await home.Orchestrator.WaitAsync(fourth.Id).WaitAsync(TimeSpan.FromSeconds(30))).State);
        release.Set();
        Assert.Equal(RunStates.Succeeded, (await home.Orchestrator.WaitAsync(third.Id).WaitAsync(TimeSpan.FromSeconds(30))).State);
        Assert.DoesNotContain("four", prompts);
    }

    [Fact]
    public async Task Concurrency_drop_refuses_a_busy_session_but_serves_others()
    {
        using ManualResetEventSlim release = new();
        List<string> prompts = [];
        await using TestHome home = await ChatHomeAsync("concurrency: { perSession: 1, onBusy: drop }\nsession: \"chat:{event.sender}\"",
            Recording(prompts, _ => { release.Wait(TimeSpan.FromSeconds(20)); return ScriptedChatClient.Text("ok"); }));

        await MessagesSource.Current!.SendAsync("+1", "long task");
        await RunOrchestratorTests.WaitForAsync(() => prompts.Contains("long task") ? "ok" : null);
        await MessagesSource.Current!.SendAsync("+1", "are you there?");
        await MessagesSource.Current!.SendAsync("+2", "hello");
        await RunOrchestratorTests.WaitForAsync(() => prompts.Contains("hello") ? "ok" : null);
        release.Set();
        await RunOrchestratorTests.WaitForAsync(() => Runs(home, "chat").Count(r => RunStates.IsFinal(r.State)) == 2 ? "ok" : null);
        Assert.DoesNotContain("are you there?", prompts);

        // Once the first run is done, the session takes messages again.
        await MessagesSource.Current!.SendAsync("+1", "next");
        await RunOrchestratorTests.WaitForAsync(() => Runs(home, "chat").Count(r => RunStates.IsFinal(r.State)) == 3 ? "ok" : null);
        Assert.Contains("next", prompts);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private sealed class MessagesPlugin : IHarnessPlugin
    {
        public string Id => "test.messages";
        public void Configure(IPluginBuilder plugin) => plugin.AddTriggerSource<MessagesSource>("test-messages");
    }

    /// <summary>A chat-like source: tests push messages into it.</summary>
    private sealed class MessagesSource : ITriggerSource
    {
        public static MessagesSource? Current;
        private TriggerSourceContext? _context;

        public string Type => "test-messages";

        public Task StartAsync(TriggerSourceContext context, CancellationToken cancellationToken)
        {
            _context = context;
            Current = this;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task SendAsync(string sender, string text) =>
            await _context!.EmitAsync(new TriggerEvent(Guid.NewGuid().ToString("N"), _context.TriggerId, DateTimeOffset.UtcNow, sender, text, [],
                new JsonObject(), null), CancellationToken.None);
    }
}
