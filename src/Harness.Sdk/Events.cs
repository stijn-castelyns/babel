using System.Text.Json.Nodes;

namespace Harness.Sdk;

/// <summary>The one envelope every run event uses. Type names follow AG-UI where one exists.</summary>
public sealed record HarnessEvent(long Seq, DateTimeOffset Ts, string? RunId, string? SessionId, string Type, JsonObject Data);

public static class EventTypes
{
    public const string RunStarted = "RUN_STARTED";
    public const string RunState = "RUN_STATE";
    public const string RunFinished = "RUN_FINISHED";
    public const string RunError = "RUN_ERROR";
    public const string TextMessageStart = "TEXT_MESSAGE_START";
    public const string TextMessageContent = "TEXT_MESSAGE_CONTENT";
    public const string TextMessageEnd = "TEXT_MESSAGE_END";
    public const string ToolCallStart = "TOOL_CALL_START";
    public const string ToolCallEnd = "TOOL_CALL_END";
    public const string ToolCallResult = "TOOL_CALL_RESULT";
    public const string ApprovalRequested = "APPROVAL_REQUESTED";
    public const string ApprovalResolved = "APPROVAL_RESOLVED";
    public const string Usage = "USAGE";

    /// <summary>Live-only events are streamed but never written to <c>events.jsonl</c>.</summary>
    public static bool IsPersisted(string type) => type != TextMessageContent;
}
