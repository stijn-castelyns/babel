using System.Text.Json.Nodes;

namespace Harness.Client;

// Wire contracts of the daemon API, shared by the server, the CLI and tests.

public sealed record SessionDto(
    string Id, string? Title, string Agent, string Model, string Workspace, string? WorkspaceName, string? Key,
    string? ParentId, string Status, string? TriggerId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    long InputTokens, long OutputTokens, long MessageCount);

public sealed record CreateSessionRequest(
    string? Agent = null, string? Workspace = null, string? WorkspaceName = null, string? WorkingDirectory = null,
    string? Title = null, string? Model = null);

public sealed record SendMessageRequest(string Text);

public sealed record SendMessageResponse(string RunId, string SessionId);

public sealed record ContentDto(string Type, string? Text = null, string? ToolName = null, string? CallId = null, JsonNode? Arguments = null, string? Result = null);

public sealed record MessageDto(long Seq, DateTimeOffset Ts, string? RunId, string Role, string Text, IReadOnlyList<ContentDto> Contents);

public sealed record MessagesPage(IReadOnlyList<MessageDto> Messages, bool HasMore);

public sealed record ForkRequest(long? AtSeq = null, string? Title = null);

public sealed record RunDto(
    string Id, string SessionId, string Agent, string Model, string? TriggerId, string State, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, long InputTokens, long OutputTokens, string? LastTool,
    string? Error, string? ResultText, JsonNode? Output = null, IReadOnlyList<string>? Files = null);

public sealed record ApprovalDecisionRequest(bool Approved, string? Reason = null, bool Always = false, string? DecidedBy = null);

public sealed record ApprovalDto(string RequestId, string RunId, string SessionId, string ToolName, string ToolCallId, JsonObject Arguments, string? Summary, DateTimeOffset RequestedAt);

public sealed record AgentDto(string Name, string? Description, string Model, string Sandbox, IReadOnlyList<string> Tools);

public sealed record WorkspaceDto(string Name, string Path);

public sealed record TemplateDto(string Name, string? Description, string? Agent, string? Sandbox, string OutputKind, IReadOnlyList<string> Steps, string Keep);

public sealed record TriggerDto(string Id, string SourceType, bool Enabled, DateTimeOffset? NextFireAt, string? LastRunId, string? LastState,
    long? DailyTokens = null, long? TokensToday = null);

public sealed record FireTriggerRequest(string? Text = null, JsonObject? Inputs = null);

public sealed record StatusDto(string Version, string Home, int ActiveRuns, int PendingApprovals, IReadOnlyList<string> PluginErrors);

/// <summary>Every run event uses this envelope. <see cref="Seq"/> is the session event sequence (or the hub sequence on the firehose).</summary>
public sealed record EventDto(long Seq, DateTimeOffset Ts, string? RunId, string? SessionId, string Type, JsonObject Data);

public sealed record ErrorDto(string Error);

public sealed record PruneRequest(bool DryRun = false);

public sealed record PruneReportDto(IReadOnlyList<string> Sessions, IReadOnlyList<string> RunFolders, int Events, bool DryRun);

public sealed record SandboxProbeDto(string Check, string Status, string Detail);
