using Harness.Core.Agents;
using Harness.Core.Sessions;
using Harness.Runs;
using Harness.Sdk;
using Harness.Tests.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>A run waiting for approval survives a daemon restart and resumes when the approval is answered.</summary>
public class DurableApprovalTests
{
    private static ScriptedChatClient ShellThenDone(string command) => new((messages, _) =>
    {
        IReadOnlyList<FunctionResultContent> results = ScriptedChatClient.LastResults(messages);
        return results.Count == 0
            ? ScriptedChatClient.Call("shell", new() { ["command"] = command })
            : ScriptedChatClient.Text("after restart: " + results[0].Result);
    });

    /// <summary>Starts a run, waits until it asks for approval, then stops the "daemon".</summary>
    private static async Task<(string RunId, string SessionId)> ParkAsync(TestHome home, RunRequest? template = null)
    {
        home.Build(ShellThenDone("echo resumed-ok"));
        RunOrchestrator runs = home.Orchestrator;
        SessionFolder session = runs.CreateSession(new SessionRequest { Workspace = home.Workspace });
        RunRecord run = runs.Start((template ?? new RunRequest { SessionId = "", Messages = [] }) with
        {
            SessionId = session.Id,
            Messages = [new ChatMessage(ChatRole.User, "go")],
        });
        await RunOrchestratorTests.WaitForAsync(() => runs.Approvals.Pending(run.Id).FirstOrDefault());
        await home.Services!.DisposeAsync();   // daemon stops
        return (run.Id, session.Id);
    }

    private static RunOrchestrator Restart(TestHome home, IChatClient model)
    {
        ServiceProvider services = home.Build(model);
        services.GetRequiredService<HarnessDb>().FailInterruptedRuns();
        RunOrchestrator runs = home.Orchestrator;
        runs.RehydrateParkedRuns();
        return runs;
    }

    [Fact]
    public async Task Pending_approval_survives_a_restart_and_the_run_resumes()
    {
        await using TestHome home = new();
        (string runId, string sessionId) = await ParkAsync(home);

        ScriptedChatClient model = ShellThenDone("echo resumed-ok");
        RunOrchestrator runs = Restart(home, model);
        Assert.Equal(RunStates.AwaitingApproval, runs.Get(runId)!.State);
        PendingApproval pending = Assert.Single(runs.Approvals.Pending(runId));
        Assert.Equal("shell", pending.ToolName);
        Assert.Equal("echo resumed-ok", pending.Summary);

        Assert.True(runs.ResolveApproval(runId, pending.RequestId, new ApprovalAnswer(true, null, "test", "test")));
        RunResult result = await runs.WaitAsync(runId).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.State == RunStates.Succeeded, result.Error);
        Assert.Contains("resumed-ok", result.Text);
        Assert.Equal(200, result.InputTokens);   // 100 before the restart (fake model's usage per call) + 100 after
        Assert.Equal(200, runs.Sessions.Open(sessionId).Info.InputTokens);

        // The resumed call ran once, and the history is valid for the next turn.
        SessionFolder session = runs.Sessions.Open(sessionId);
        Assert.Single(session.ReadHistory().SelectMany(e => e.Message.Contents).OfType<FunctionResultContent>());
        Assert.Contains(session.ReadEvents(runId: runId), e => e.Type == EventTypes.ApprovalResolved);
        Assert.Equal(EventTypes.RunFinished, session.ReadEvents(runId: runId).Last().Type);
        Assert.Empty(home.Services!.GetRequiredService<ApprovalStore>().Parked());
    }

    [Fact]
    public async Task Parked_run_can_be_cancelled_after_a_restart()
    {
        await using TestHome home = new();
        (string runId, _) = await ParkAsync(home);
        RunOrchestrator runs = Restart(home, ShellThenDone("x"));
        Assert.True(runs.Cancel(runId));
        Assert.Equal(RunStates.Cancelled, (await runs.WaitAsync(runId)).State);
        Assert.Empty(runs.Approvals.Pending());
    }

    [Fact]
    public async Task Denied_after_restart_is_reported_to_the_model()
    {
        await using TestHome home = new();
        (string runId, _) = await ParkAsync(home);
        ScriptedChatClient model = ShellThenDone("x");
        RunOrchestrator runs = Restart(home, model);
        PendingApproval pending = Assert.Single(runs.Approvals.Pending(runId));
        runs.ResolveApproval(runId, pending.RequestId, new ApprovalAnswer(false, "no thanks", "test", "test"));
        RunResult result = await runs.WaitAsync(runId).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RunStates.Succeeded, result.State);
        Assert.Contains("no thanks", result.Text);
    }

    [Fact]
    public async Task Approval_timeout_keeps_counting_across_a_restart()
    {
        await using TestHome home = new();
        RunRequest triggered = new()
        {
            SessionId = "", Messages = [], Interactive = false, AllowUnsandboxed = true,
            ApprovalTimeout = TimeSpan.FromSeconds(1), OnApprovalTimeoutApprove = false,
        };
        (string runId, _) = await ParkAsync(home, triggered);
        await Task.Delay(1200);
        RunOrchestrator runs = Restart(home, ShellThenDone("x"));
        RunResult result = await runs.WaitAsync(runId).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RunStates.Rejected, result.State);   // an unattended denial ends the run
    }

    [Fact]
    public async Task Runs_that_were_not_waiting_for_approval_are_failed_on_restart()
    {
        await using TestHome home = new();
        home.Build(new ScriptedChatClient((_, _) => ScriptedChatClient.Text("x")));
        RunOrchestrator runs = home.Orchestrator;
        SessionFolder session = runs.CreateSession(new SessionRequest { Workspace = home.Workspace });
        HarnessDb db = home.Services!.GetRequiredService<HarnessDb>();
        db.UpsertRun(new RunRecord { Id = "r_stuck", SessionId = session.Id, Agent = "coder", Model = "fake", State = RunStates.Running, CreatedAt = DateTimeOffset.UtcNow });
        await home.Services!.DisposeAsync();

        Restart(home, new ScriptedChatClient((_, _) => ScriptedChatClient.Text("x")));
        Assert.Equal(RunStates.Failed, home.Orchestrator.Get("r_stuck")!.State);
    }
}
