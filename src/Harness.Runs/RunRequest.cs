using Harness.Sdk;
using Microsoft.Extensions.AI;

namespace Harness.Runs;

/// <summary>One execution against a session: an interactive user turn or a triggered job.</summary>
public sealed record RunRequest
{
    public required string SessionId { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    /// <summary>True when a person can answer approvals. Unattended runs with no approver treat <c>ask</c> as <c>deny</c>.</summary>
    public bool Interactive { get; init; } = true;
    public string? TriggerId { get; init; }
    /// <summary>Prompt layer 6.</summary>
    public string? RunInstructions { get; init; }
    public ReplyAddress? ReplyTo { get; init; }
    /// <summary>Triggered runs refuse to start without a sandbox unless this is set.</summary>
    public bool AllowUnsandboxed { get; init; }
    /// <summary>How long an approval may wait before <see cref="OnApprovalTimeoutApprove"/> applies. Null waits indefinitely.</summary>
    public TimeSpan? ApprovalTimeout { get; init; }
    public bool OnApprovalTimeoutApprove { get; init; }
    /// <summary>Extra tools for this run only, such as <c>submit_output</c>.</summary>
    public IReadOnlyList<AITool> ExtraTools { get; init; } = [];
}

/// <summary>Creating a session.</summary>
public sealed record SessionRequest
{
    public string? Agent { get; init; }
    /// <summary>Host path of the workspace; ignored when <see cref="WorkspaceName"/> is set.</summary>
    public string? Workspace { get; init; }
    public string? WorkspaceName { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Title { get; init; }
    public string? Key { get; init; }
    public string? Model { get; init; }
    public string? TriggerId { get; init; }
}
