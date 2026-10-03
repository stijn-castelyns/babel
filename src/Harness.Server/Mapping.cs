using System.Text;
using System.Text.Json;
using Harness.Client;
using Harness.Core.Agents;
using Harness.Core.Config;
using Harness.Core.Sessions;
using Harness.Runs;
using Microsoft.Extensions.AI;

namespace Harness.Server;

internal static class Mapping
{
    public static SessionDto ToDto(this SessionInfo s) => new(s.Id, s.Title, s.Agent, s.Model, s.Workspace, s.WorkspaceName, s.Key, s.ParentId,
        s.Status, s.TriggerId, s.CreatedAt, s.UpdatedAt, s.InputTokens, s.OutputTokens, s.MessageCount);

    public static SessionDto ToDto(this HarnessDb.SessionRow s) => new(s.Id, s.Title, s.Agent, s.Model, s.Workspace, s.WorkspaceName, s.Key, s.ParentId,
        s.Status, s.TriggerId, s.CreatedAt, s.UpdatedAt, s.InputTokens, s.OutputTokens, s.MessageCount);

    public static RunDto ToDto(this RunRecord r) => new(r.Id, r.SessionId, r.Agent, r.Model, r.TriggerId, r.State, r.CreatedAt, r.StartedAt,
        r.FinishedAt, r.InputTokens, r.OutputTokens, r.LastTool, r.Error, r.ResultText);

    public static ApprovalDto ToDto(this PendingApproval p) => new(p.RequestId, p.RunId, p.SessionId, p.ToolName, p.ToolCallId, p.Arguments, p.Summary, p.RequestedAt);

    public static AgentDto ToDto(this AgentDefinition a) => new(a.Name, a.Description, a.Model, a.Sandbox, [.. a.Tools.Builtin, .. a.Tools.Mcp.Select(m => "mcp:" + m)]);

    public static MessageDto ToDto(this HistoryEntry e) => new(e.Seq, e.Ts, e.RunId, e.Message.Role.Value, e.Message.Text,
        [.. e.Message.Contents.Select(ToDto).Where(c => c is not null).Select(c => c!)]);

    private static ContentDto? ToDto(AIContent content) => content switch
    {
        TextContent t => new ContentDto("text", t.Text),
        TextReasoningContent r => new ContentDto("reasoning", r.Text),
        FunctionCallContent c => new ContentDto("tool_call", ToolName: c.Name, CallId: c.CallId, Arguments: JsonSerializer.SerializeToNode(c.Arguments, AIJsonUtilities.DefaultOptions)),
        FunctionResultContent r => new ContentDto("tool_result", CallId: r.CallId, Result: ToolCallMiddleware.Stringify(r.Result)),
        ToolApprovalRequestContent a => new ContentDto("approval_request", CallId: a.RequestId, ToolName: (a.ToolCall as FunctionCallContent)?.Name),
        ToolApprovalResponseContent a => new ContentDto("approval_response", CallId: a.RequestId, Text: a.Approved ? "approved" : "denied"),
        UsageContent => null,
        _ => new ContentDto(content.GetType().Name),
    };

    /// <summary>A readable Markdown transcript.</summary>
    public static string ToMarkdown(SessionInfo info, IEnumerable<HistoryEntry> history)
    {
        StringBuilder sb = new();
        sb.AppendLine($"# {info.Title ?? info.Id}").AppendLine();
        sb.AppendLine($"- Session: `{info.Id}`  ").AppendLine($"- Agent: {info.Agent} · Model: {info.Model}  ").AppendLine($"- Workspace: `{info.Workspace}`").AppendLine();
        foreach (HistoryEntry e in history)
        {
            foreach (AIContent c in e.Message.Contents)
            {
                switch (c)
                {
                    case TextContent t when t.Text.Length > 0:
                        sb.AppendLine($"## {(e.Message.Role == ChatRole.User ? "You" : e.Message.Role == ChatRole.Assistant ? info.Agent : e.Message.Role.Value)} · #{e.Seq}").AppendLine().AppendLine(t.Text).AppendLine();
                        break;
                    case FunctionCallContent call:
                        sb.AppendLine($"**▸ {call.Name}** `{JsonSerializer.Serialize(call.Arguments, AIJsonUtilities.DefaultOptions)}`").AppendLine();
                        break;
                    case FunctionResultContent result:
                        string text = ToolCallMiddleware.Stringify(result.Result);
                        if (text.Length > 4000) text = text[..4000] + "\n…";
                        sb.AppendLine("```").AppendLine(text).AppendLine("```").AppendLine();
                        break;
                }
            }
        }
        return sb.ToString();
    }
}
