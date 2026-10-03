using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Core.Sessions;

/// <summary>Contents of <c>session.json</c>.</summary>
public sealed class SessionInfo
{
    public string Id { get; set; } = "";
    public string Agent { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Host path of the workspace root.</summary>
    public string Workspace { get; set; } = "";
    /// <summary>Named workspace, when the session was created from one.</summary>
    public string? WorkspaceName { get; set; }
    /// <summary>Working directory inside the workspace; folder prompts are collected from the root down to here.</summary>
    public string? WorkingDirectory { get; set; }
    public string? Title { get; set; }
    /// <summary>Stable key for sessions continued across triggered runs, for example <c>whatsapp:+31612345678</c>.</summary>
    public string? Key { get; set; }
    public string? ParentId { get; set; }
    public long? ForkSeq { get; set; }
    public string Status { get; set; } = "idle";
    public string? TriggerId { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long MessageCount { get; set; }
    /// <summary>Serialized Agent Framework session state.</summary>
    public JsonElement? AgentState { get; set; }
}

/// <summary>One line of <c>history.jsonl</c>.</summary>
public sealed record HistoryEntry(long Seq, DateTimeOffset Ts, string? RunId, Microsoft.Extensions.AI.ChatMessage Message);

/// <summary>One line of <c>checkpoints.jsonl</c>: messages up to <see cref="UpToSeq"/> are summarised as <see cref="Summary"/>.</summary>
public sealed record CompactionCheckpoint(long UpToSeq, DateTimeOffset Ts, string Summary);

/// <summary>Index row for a run.</summary>
public sealed class RunRecord
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Agent { get; set; } = "";
    public string Model { get; set; } = "";
    public string? TriggerId { get; set; }
    public string State { get; set; } = "queued";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public string? LastTool { get; set; }
    public string? Error { get; set; }
    public string? ResultText { get; set; }
}

internal static class SessionJson
{
    public static readonly JsonSerializerOptions Options = new(Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}
