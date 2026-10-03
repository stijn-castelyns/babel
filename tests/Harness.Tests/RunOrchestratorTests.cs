using Harness.Core.Sessions;
using Harness.Runs;
using Harness.Sdk;
using Harness.Tests.TestSupport;
using Microsoft.Extensions.AI;

namespace Harness.Tests;

public class RunOrchestratorTests
{
    [Fact]
    public async Task Tool_calls_approvals_and_history_round_trip()
    {
        await using TestHome home = new();
        File.WriteAllText(Path.Combine(home.Workspace, "notes.txt"), "alpha\nbeta\n");

        ScriptedChatClient model = new((messages, _) =>
        {
            IReadOnlyList<FunctionResultContent> results = ScriptedChatClient.LastResults(messages);
            if (results.Count == 0 && messages[^1].Text == "first")
                return ScriptedChatClient.Call("read", new() { ["path"] = "notes.txt" });
            if (results.Count == 1 && results[0].Result?.ToString()?.Contains("alpha") == true)
                return ScriptedChatClient.Call("shell", new() { ["command"] = "echo from-shell" });
            if (results.Count == 1 && results[0].Result?.ToString()?.Contains("from-shell") == true)
                return ScriptedChatClient.Text("done");
            return ScriptedChatClient.Text("second reply");
        });
        home.Build(model);
        RunOrchestrator runs = home.Orchestrator;

        SessionFolder session = runs.CreateSession(new SessionRequest { Workspace = home.Workspace });
        List<HarnessEvent> events = [];
        using EventHub.Subscription sub = runs.Events.Subscribe(e => e.SessionId == session.Id);

        Harness.Core.Sessions.RunRecord run = runs.Start(new RunRequest { SessionId = session.Id, Messages = [new ChatMessage(ChatRole.User, "first")] });

        PendingApproval pending = await WaitForAsync(() => runs.Approvals.Pending(run.Id).FirstOrDefault());
        Assert.Equal("shell", pending.ToolName);
        Assert.True(runs.ResolveApproval(run.Id, pending.RequestId, new ApprovalAnswer(true, null, "test", "test")));

        RunResult result = await runs.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RunStates.Succeeded, result.State);
        Assert.Equal("done", result.Text);
        Assert.True(result.InputTokens > 0);

        while (sub.Reader.TryRead(out EventHub.Envelope? e)) events.Add(e.Event);
        string[] types = [.. events.Select(e => e.Type)];
        Assert.Contains(EventTypes.ApprovalRequested, types);
        Assert.Contains(EventTypes.ApprovalResolved, types);
        Assert.Contains(events, e => e.Type == EventTypes.ToolCallStart && e.Data["toolName"]?.GetValue<string>() == "read");
        Assert.Contains(events, e => e.Type == EventTypes.ToolCallResult && e.Data["content"]!.GetValue<string>().Contains("from-shell"));
        Assert.Equal(EventTypes.RunFinished, types[^1]);

        // Second turn: the history the model sees must be valid for Chat Completions (no approval content, no orphan calls).
        Harness.Core.Sessions.RunRecord second = runs.Start(new RunRequest { SessionId = session.Id, Messages = [new ChatMessage(ChatRole.User, "again")] });
        RunResult secondResult = await runs.WaitAsync(second.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("second reply", secondResult.Text);

        List<ChatMessage> seen = model.Calls[^1];
        Assert.DoesNotContain(seen.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent or ToolApprovalResponseContent);
        HashSet<string> resultIds = [.. seen.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId)];
        Assert.All(seen.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => Assert.Contains(c.CallId, resultIds));
        Assert.Equal(2, seen.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count());
        Assert.Contains(seen, m => m.Role == ChatRole.User && m.Text == "first");
        Assert.Contains(seen, m => m.Role == ChatRole.Assistant && m.Text == "done");

        // History on disk holds every message, and the index can find the session by message text.
        Assert.True(session.ReadHistory().Count() >= 6);
        Assert.Contains(home.Orchestrator.Sessions.Index.ListSessions("done"), s => s.Id == session.Id);
    }

    [Fact]
    public async Task Denied_approval_is_reported_to_the_model_in_interactive_runs()
    {
        await using TestHome home = new();
        ScriptedChatClient model = new((messages, _) =>
        {
            IReadOnlyList<FunctionResultContent> results = ScriptedChatClient.LastResults(messages);
            return results.Count == 0
                ? ScriptedChatClient.Call("shell", new() { ["command"] = "rm -rf /" })
                : ScriptedChatClient.Text("ok, not running it: " + results[0].Result);
        });
        home.Build(model);
        RunOrchestrator runs = home.Orchestrator;
        SessionFolder session = runs.CreateSession(new SessionRequest { Workspace = home.Workspace });
        Harness.Core.Sessions.RunRecord run = runs.Start(new RunRequest { SessionId = session.Id, Messages = [new ChatMessage(ChatRole.User, "go")] });
        PendingApproval pending = await WaitForAsync(() => runs.Approvals.Pending(run.Id).FirstOrDefault());
        runs.ResolveApproval(run.Id, pending.RequestId, new ApprovalAnswer(false, "too dangerous", "test", "test"));
        RunResult result = await runs.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RunStates.Succeeded, result.State);
        Assert.StartsWith("ok, not running it", result.Text);
    }

    [Fact]
    public async Task Unattended_run_denies_ask_tools_and_ends_rejected()
    {
        await using TestHome home = new();
        ScriptedChatClient model = new((messages, _) =>
            ScriptedChatClient.LastResults(messages).Count == 0 ? ScriptedChatClient.Call("shell", new() { ["command"] = "ls" }) : ScriptedChatClient.Text("x"));
        home.Build(model);
        RunOrchestrator runs = home.Orchestrator;
        SessionFolder session = runs.CreateSession(new SessionRequest { Workspace = home.Workspace });
        Harness.Core.Sessions.RunRecord run = runs.Start(new RunRequest
        {
            SessionId = session.Id, Messages = [new ChatMessage(ChatRole.User, "go")], Interactive = false, AllowUnsandboxed = true,
        });
        RunResult result = await runs.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RunStates.Rejected, result.State);
    }

    [Fact]
    public async Task Triggered_run_without_sandbox_is_refused()
    {
        await using TestHome home = new();
        home.Build(new ScriptedChatClient((_, _) => ScriptedChatClient.Text("hi")));
        RunOrchestrator runs = home.Orchestrator;
        SessionFolder session = runs.CreateSession(new SessionRequest { Workspace = home.Workspace });
        Harness.Core.Sessions.RunRecord run = runs.Start(new RunRequest { SessionId = session.Id, Messages = [new ChatMessage(ChatRole.User, "go")], Interactive = false });
        RunResult result = await runs.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RunStates.Failed, result.State);
        Assert.Contains("sandbox", result.Error);
    }

    [Fact]
    public async Task Cancel_stops_a_run_waiting_for_approval()
    {
        await using TestHome home = new();
        home.Build(new ScriptedChatClient((m, _) => ScriptedChatClient.Call("shell", new() { ["command"] = "sleep 1" })));
        RunOrchestrator runs = home.Orchestrator;
        SessionFolder session = runs.CreateSession(new SessionRequest { Workspace = home.Workspace });
        Harness.Core.Sessions.RunRecord run = runs.Start(new RunRequest { SessionId = session.Id, Messages = [new ChatMessage(ChatRole.User, "go")] });
        await WaitForAsync(() => runs.Approvals.Pending(run.Id).FirstOrDefault());
        Assert.True(runs.Cancel(run.Id));
        RunResult result = await runs.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RunStates.Cancelled, result.State);
        Assert.Empty(runs.Approvals.Pending(run.Id));
    }

    internal static async Task<T> WaitForAsync<T>(Func<T?> probe, int timeoutMs = 20_000) where T : class
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (probe() is { } value) return value;
            await Task.Delay(25);
        }
        throw new TimeoutException("Condition not met in time.");
    }
}
