using Harness.Core.Sessions;
using Harness.Runs;
using Harness.Sdk;
using Harness.Tests.TestSupport;
using Microsoft.Extensions.AI;

namespace Harness.Tests;

public class CheckpointTests
{
    [Fact]
    public async Task Long_sessions_are_summarised_into_checkpoints_that_replace_older_turns()
    {
        await using TestHome home = new("""
            name: coder
            model: fake
            compaction: { summarizeAfterTokens: 300, keepTurns: 2 }
            """);
        int summaries = 0;
        ScriptedChatClient model = new((messages, _) =>
        {
            if (messages[0].Role == ChatRole.System && messages[0].Text.StartsWith("You maintain the memory", StringComparison.Ordinal))
                return ScriptedChatClient.Text($"SUMMARY {Interlocked.Increment(ref summaries)}: the user is counting turns.");
            return ScriptedChatClient.Text("ack " + messages[^1].Text[..6]);
        });
        home.Build(model);
        RunOrchestrator runs = home.Orchestrator;
        SessionFolder session = runs.CreateSession(new SessionRequest { Workspace = home.Workspace });

        string padding = new('x', 400);   // about 100 tokens per message
        for (int turn = 1; turn <= 6; turn++)
        {
            RunRecord run = runs.Start(new RunRequest { SessionId = session.Id, Messages = [new ChatMessage(ChatRole.User, $"turn {turn} {padding}")] });
            Assert.Equal(RunStates.Succeeded, (await runs.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(30))).State);
        }

        CompactionCheckpoint[] checkpoints = [.. session.ReadCheckpoints()];
        Assert.NotEmpty(checkpoints);
        Assert.Equal(12, session.ReadHistory().Count());   // history.jsonl keeps everything

        // The last real model call saw the newest summary and only the turns after it.
        List<ChatMessage> last = model.Calls.Last(c => c[0].Role != ChatRole.System || !c[0].Text.StartsWith("You maintain", StringComparison.Ordinal));
        string seen = string.Join("\n", last.Select(m => m.Text));
        Assert.Contains($"[Summary of the earlier conversation]\nSUMMARY {summaries}:", seen);
        Assert.DoesNotContain("turn 1 ", seen);
        Assert.Contains("turn 6 ", seen);
        long upTo = checkpoints[^1].UpToSeq;
        Assert.True(session.ReadHistory().Single(e => e.Seq == upTo + 1).Message.Role == ChatRole.User);

        // A later summary folds in the earlier one.
        if (checkpoints.Length > 1)
            Assert.Contains(model.Calls, c => c.Count == 2 && c[1].Text.Contains("Earlier summary:\nSUMMARY 1", StringComparison.Ordinal));

        HarnessEvent cp = Assert.Single(session.ReadEvents(), e => e.Type == EventTypes.Checkpoint && e.Data["upToSeq"]!.GetValue<long>() == upTo);
        Assert.True(cp.Data["messages"]!.GetValue<int>() > 0);
    }

    [Fact]
    public void Transcript_clips_tool_results_and_keeps_the_newest_part()
    {
        HistoryEntry[] entries =
        [
            new(1, DateTimeOffset.UtcNow, null, new ChatMessage(ChatRole.User, "old question " + new string('a', 6000))),
            new(2, DateTimeOffset.UtcNow, null, new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "a.cs" })])),
            new(3, DateTimeOffset.UtcNow, null, new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", new string('r', 5000))])),
            new(4, DateTimeOffset.UtcNow, null, new ChatMessage(ChatRole.User, "newest question")),
        ];
        string text = Checkpoints.Transcript("before", entries, 5000);
        Assert.StartsWith("Earlier summary:\nbefore", text);
        Assert.Contains("(the oldest 1 entries are omitted)", text);
        Assert.Contains("assistant called read({\"path\":\"a.cs\"})", text);
        Assert.Contains(" … ", text);
        Assert.EndsWith("user: newest question", text);
    }
}
