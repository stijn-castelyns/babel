using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Harness.Core.Config;
using Harness.Core.Sessions;
using Harness.Sdk;

namespace Harness.Core.Agents;

/// <summary>Receives the events a run produces.</summary>
public interface IRunEvents
{
    void Emit(string type, JsonObject data);
}

/// <summary>Everything the agent factory and its middleware know about one run.</summary>
public sealed class RunContext : IToolContext
{
    private long _inputTokens;
    private long _outputTokens;
    private int _toolCalls;

    public required string RunId { get; init; }
    public required SessionFolder Session { get; init; }
    public required AgentDefinition Agent { get; init; }
    public required ModelProfile Model { get; init; }
    public required string WorkspaceRoot { get; init; }
    public required string WorkingDirectory { get; init; }
    public required ISandbox Sandbox { get; init; }
    public required IServiceProvider Services { get; init; }
    public required IRunEvents Events { get; init; }
    public required SessionRuntimeState State { get; init; }
    /// <summary>True when a person started the run and can answer approvals.</summary>
    public bool Interactive { get; init; } = true;
    /// <summary>Prompt layer 6: trigger template instructions and output contract.</summary>
    public string? RunInstructions { get; init; }
    public string? TriggerId { get; init; }
    /// <summary>Secret values to mask in tool results and events.</summary>
    public IReadOnlyCollection<string> SecretValues { get; init; } = [];
    public int SpillThresholdChars { get; init; } = 24_000;
    public WebToolsConfig Web { get; init; } = new();
    public IReadOnlyList<string> FolderInstructionFiles { get; init; } = ["AGENTS.md"];

    public string SessionId => Session.Id;
    public string AgentName => Agent.Name;
    public long InputTokens => Interlocked.Read(ref _inputTokens);
    public long OutputTokens => Interlocked.Read(ref _outputTokens);
    public int ToolCalls => Volatile.Read(ref _toolCalls);
    public string? LastTool { get; set; }

    public void AddUsage(long input, long output)
    {
        Interlocked.Add(ref _inputTokens, input);
        Interlocked.Add(ref _outputTokens, output);
    }

    public int IncrementToolCalls() => Interlocked.Increment(ref _toolCalls);

    public string Mask(string text)
    {
        foreach (string secret in SecretValues)
            if (secret.Length >= 6) text = text.Replace(secret, "[secret]", StringComparison.Ordinal);
        return text;
    }
}

/// <summary>In-memory state that outlives a single run but belongs to one session.</summary>
public sealed class SessionRuntimeState
{
    /// <summary>Content hashes of files the agent has read, keyed by canonical host path. Enforces read-before-write.</summary>
    public ConcurrentDictionary<string, FileReadState> Reads { get; } = new(StringComparer.Ordinal);
    /// <summary>Folders whose lazy instructions (prompt layer 5) were already shown.</summary>
    public ConcurrentDictionary<string, bool> LoadedFolderInstructions { get; } = new(StringComparer.Ordinal);
    /// <summary>Exact tool invocations the user approved for the rest of the session.</summary>
    public ConcurrentDictionary<string, bool> AlwaysApproved { get; } = new(StringComparer.Ordinal);
    /// <summary>Background shell jobs, keyed by handle.</summary>
    public ConcurrentDictionary<string, object> BackgroundJobs { get; } = new(StringComparer.Ordinal);
    private int _turn;
    public int Turn => Volatile.Read(ref _turn);
    public int NextTurn() => Interlocked.Increment(ref _turn);
}

public sealed record FileReadState(string Hash, int Turn, int Offset, int Limit);

public sealed class SessionRuntimeRegistry
{
    private readonly ConcurrentDictionary<string, SessionRuntimeState> _states = new();
    public SessionRuntimeState Get(string sessionId) => _states.GetOrAdd(sessionId, _ => new SessionRuntimeState());
    public void Forget(string sessionId) => _states.TryRemove(sessionId, out _);
}

public sealed class RunLimitExceededException(string message) : Exception(message);
