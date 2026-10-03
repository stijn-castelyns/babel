namespace Harness.Core.Config;

/// <summary><c>config.yaml</c>.</summary>
public sealed class HarnessConfig
{
    public Dictionary<string, ModelProfile> Models { get; set; } = [];
    public DefaultsConfig Defaults { get; set; } = new();
    public ListenersConfig Listeners { get; set; } = new();
    public PromptConfig Prompts { get; set; } = new();
    public List<CommandHookConfig> Hooks { get; set; } = [];
    public ToolsConfig Tools { get; set; } = new();
    /// <summary>Per-plugin settings, keyed by plugin id.</summary>
    public Dictionary<string, object?> Plugins { get; set; } = [];
    public RunLimitsConfig Runs { get; set; } = new();
}

public sealed class ModelProfile
{
    /// <summary><c>ollama</c>, <c>azure-openai</c> or <c>openai</c> (any OpenAI-compatible chat-completions endpoint).</summary>
    public string Provider { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string? Model { get; set; }
    public string? Deployment { get; set; }
    public ModelAuth? Auth { get; set; }
    /// <summary>Context window in tokens, used for compaction and <c>models doctor</c>.</summary>
    public int? ContextWindow { get; set; }
    /// <summary>How many runs may use this profile at once. A local model on one GPU usually wants 1.</summary>
    public int? MaxConcurrency { get; set; }
    public float? Temperature { get; set; }
    public int? MaxOutputTokens { get; set; }
}

public sealed class ModelAuth
{
    /// <summary><c>entra</c>, <c>apiKey</c> or <c>none</c>.</summary>
    public string Type { get; set; } = "none";
    /// <summary>A <c>secret:</c> or <c>env:</c> reference.</summary>
    public string? Secret { get; set; }
}

public sealed class DefaultsConfig
{
    public string Agent { get; set; } = "coder";
    public string? Model { get; set; }
}

public sealed class ListenersConfig
{
    /// <summary>Local socket path; empty uses <c>~/.harness/harness.sock</c>.</summary>
    public string? Socket { get; set; }
    /// <summary>API listener, for example <c>https://127.0.0.1:7443</c>. Unset disables it.</summary>
    public string? Api { get; set; }
    /// <summary>Webhook listener, for example <c>http://127.0.0.1:7444</c>. Unset disables it.</summary>
    public string? Webhooks { get; set; }
    /// <summary>Bearer token accepted on the API listener until passkey auth lands (a <c>secret:</c> or <c>env:</c> reference).</summary>
    public string? ApiToken { get; set; }
    /// <summary>PFX certificate for an <c>https://</c> API listener.</summary>
    public string? ApiCertificate { get; set; }
    /// <summary>Password of <see cref="ApiCertificate"/> (a <c>secret:</c> or <c>env:</c> reference).</summary>
    public string? ApiCertificatePassword { get; set; }
}

public sealed class PromptConfig
{
    /// <summary>File names read as folder prompt layers, in priority order.</summary>
    public List<string> FolderFiles { get; set; } = ["AGENTS.md"];
}

public sealed class ToolsConfig
{
    /// <summary>Tool results above this many characters are spilled to <c>.harness/spill</c>.</summary>
    public int SpillThresholdChars { get; set; } = 24_000;
}

public sealed class RunLimitsConfig
{
    public int GlobalConcurrency { get; set; } = 4;
}

/// <summary>A command hook: a shell command that receives the hook context as JSON on stdin.</summary>
public sealed class CommandHookConfig
{
    /// <summary>Hook event, for example <c>toolCalling</c>, <c>toolCalled</c>, <c>runCompleted</c>.</summary>
    public string Event { get; set; } = "";
    /// <summary>Optional tool-name regex for tool events.</summary>
    public string? Matcher { get; set; }
    public string Command { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>An agent definition in <c>agents/&lt;name&gt;.yaml</c>.</summary>
public sealed class AgentDefinition
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Model { get; set; } = "";
    /// <summary>Path to the instructions file, relative to the harness home.</summary>
    public string? Instructions { get; set; }
    public AgentTools Tools { get; set; } = new();
    /// <summary>Skill directories, relative to the harness home; empty means none.</summary>
    public List<string> Skills { get; set; } = [];
    public List<string> Plugins { get; set; } = [];
    /// <summary>Policy per tool or tool group: <c>ask</c>, <c>allow</c>, <c>deny</c> or <c>allowlist</c>.</summary>
    public Dictionary<string, string> Approvals { get; set; } = [];
    /// <summary>Patterns that are auto-approved for tools whose policy is <c>allowlist</c>, keyed by tool.</summary>
    public Dictionary<string, List<string>> Allowlist { get; set; } = [];
    /// <summary>Sandbox profile name from <c>sandboxes.yaml</c>; <c>none</c> runs on the host.</summary>
    public string Sandbox { get; set; } = "none";
    public CompactionConfig Compaction { get; set; } = new();
    public AgentLimits Limits { get; set; } = new();

    /// <summary>Where this definition was loaded from.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public string? SourcePath { get; set; }
}

public sealed class AgentTools
{
    public List<string> Builtin { get; set; } = ["read", "list", "glob", "grep", "edit", "write", "shell"];
    public List<string> Mcp { get; set; } = [];
    public List<string> Deny { get; set; } = [];
}

public sealed class CompactionConfig
{
    /// <summary>Older tool results become one-line stubs once the conversation has more than this many messages.</summary>
    public int ToolResultsAfter { get; set; } = 40;
    /// <summary>Older turns are dropped from the model's view beyond this many turns.</summary>
    public int SlidingWindowTurns { get; set; } = 30;
}

public sealed class AgentLimits
{
    public int MaxToolIterations { get; set; } = 60;
    public int MaxRunMinutes { get; set; } = 30;
    public long? MaxTokens { get; set; }
    /// <summary>What set <see cref="MaxTokens"/> when it is not the agent's own limit (a trigger's daily budget), for the error message.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public string? MaxTokensReason { get; set; }
}

/// <summary>An entry in <c>sandboxes.yaml</c>.</summary>
public sealed class SandboxProfile
{
    public string Type { get; set; } = "none";
    public string Network { get; set; } = "full";
    public List<string> AllowHosts { get; set; } = [];
    public List<MountConfig> Mounts { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
    public LimitsConfig Limits { get; set; } = new();
    public string? Image { get; set; }
    public Dictionary<string, string> Options { get; set; } = [];
}

public sealed class MountConfig
{
    public string Host { get; set; } = "";
    public string Path { get; set; } = "";
    public string Mode { get; set; } = "ro";
}

public sealed class LimitsConfig
{
    public double? Cpus { get; set; }
    public int? MemoryMb { get; set; }
    public int? Pids { get; set; }
    public int? WallClockMinutes { get; set; }
}

/// <summary><c>.harness/config.yaml</c> inside a workspace folder.</summary>
public sealed class FolderConfig
{
    public string? Model { get; set; }
    public string? Sandbox { get; set; }
    public List<string> DeniedTools { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public List<string> Mcp { get; set; } = [];
    public List<string> Plugins { get; set; } = [];
    public Dictionary<string, string> Approvals { get; set; } = [];
}
