using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Harness.Core.Sessions;

/// <summary>
/// Plugs a session folder into Agent Framework as its <see cref="ChatHistoryProvider"/>. Loads the latest checkpoint
/// summary plus the messages after it, repairs history Chat Completions would reject, and appends each turn's messages.
/// </summary>
public sealed class FileChatHistoryProvider(SessionFolder session, string runId) : ChatHistoryProvider(null, null, null)
{
    public const string InterruptedResult = "Interrupted: this tool call did not complete (the run stopped before it returned).";
    public const string UnansweredApproval = "Not run: the approval request was not answered before the conversation continued.";

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        List<HistoryEntry> entries = [.. session.ReadHistory()];
        CompactionCheckpoint? checkpoint = session.ReadCheckpoints().LastOrDefault();
        List<ChatMessage> messages = [];
        if (checkpoint is not null)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"[Summary of the earlier conversation]\n{checkpoint.Summary}"));
            entries = [.. entries.Where(e => e.Seq > checkpoint.UpToSeq)];
        }
        messages.AddRange(entries.Select(e => e.Message));
        return ValueTask.FromResult<IEnumerable<ChatMessage>>(Repair(messages, context.RequestMessages));
    }

    protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        IEnumerable<ChatMessage> request = context.RequestMessages
            .Where(m => m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);
        IEnumerable<ChatMessage> response = context.ResponseMessages ?? [];
        session.AppendHistory(request.Concat(response), runId);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Makes stored history safe to send again:
    /// approval requests and responses already processed in earlier runs are removed (their call and result are in history);
    /// an approval request still unanswered becomes a call with a "not run" result, unless this request answers it;
    /// a tool call with no result gets a synthetic "interrupted" result, because Chat Completions rejects orphaned calls.
    /// </summary>
    public static List<ChatMessage> Repair(IReadOnlyList<ChatMessage> history, IEnumerable<ChatMessage> request)
    {
        HashSet<string> answeredNow = [.. request.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>().Select(r => r.RequestId)];
        HashSet<string> answeredBefore = [.. history.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>().Select(r => r.RequestId)];

        List<ChatMessage> cleaned = [];
        foreach (ChatMessage message in history)
        {
            List<AIContent> contents = [];
            List<AIContent> syntheticResults = [];
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case ToolApprovalResponseContent:
                        break;   // processed in an earlier run
                    case ToolApprovalRequestContent req when answeredBefore.Contains(req.RequestId):
                        break;
                    case ToolApprovalRequestContent req when answeredNow.Contains(req.RequestId):
                        contents.Add(req);
                        break;
                    case ToolApprovalRequestContent req when req.ToolCall is FunctionCallContent call:
                        contents.Add(call);
                        syntheticResults.Add(new FunctionResultContent(call.CallId, UnansweredApproval));
                        break;
                    case ToolApprovalRequestContent:
                        break;
                    default:
                        contents.Add(content);
                        break;
                }
            }
            if (contents.Count > 0)
                cleaned.Add(new ChatMessage(message.Role, contents) { AuthorName = message.AuthorName, MessageId = message.MessageId, CreatedAt = message.CreatedAt, AdditionalProperties = message.AdditionalProperties });
            if (syntheticResults.Count > 0)
                cleaned.Add(new ChatMessage(ChatRole.Tool, syntheticResults));
        }

        HashSet<string> results = [.. cleaned.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId)];
        List<ChatMessage> repaired = [];
        for (int i = 0; i < cleaned.Count; i++)
        {
            repaired.Add(cleaned[i]);
            List<FunctionCallContent> orphans = [.. cleaned[i].Contents.OfType<FunctionCallContent>().Where(c => !c.InformationalOnly && !results.Contains(c.CallId))];
            if (orphans.Count == 0) continue;
            // Results must follow their call; keep any tool messages that already follow it, then add the missing ones.
            while (i + 1 < cleaned.Count && cleaned[i + 1].Role == ChatRole.Tool) repaired.Add(cleaned[++i]);
            repaired.Add(new ChatMessage(ChatRole.Tool, [.. orphans.Select(c => (AIContent)new FunctionResultContent(c.CallId, InterruptedResult))]));
        }
        return repaired;
    }
}
