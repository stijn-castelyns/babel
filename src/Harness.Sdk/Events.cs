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
    /// <summary>A workspace step of a run template started, finished or failed.</summary>
    public const string WorkspaceStep = "WORKSPACE_STEP";
    /// <summary>The agent submitted output through <c>submit_output</c>, or the harness checked it; carries the validation result.</summary>
    public const string OutputValidated = "OUTPUT_VALIDATED";
    /// <summary>An output sink delivered (or failed to deliver) the run's result.</summary>
    public const string OutputDelivered = "OUTPUT_DELIVERED";
    /// <summary>Older history was summarised into a checkpoint in <c>checkpoints.jsonl</c>.</summary>
    public const string Checkpoint = "CHECKPOINT";
    /// <summary>The egress proxy of a <c>network: allowlist</c> sandbox refused a connection.</summary>
    public const string EgressDenied = "EGRESS_DENIED";

    /// <summary>Live-only events are streamed but never written to <c>events.jsonl</c>.</summary>
    public static bool IsPersisted(string type) => type != TextMessageContent;
}
