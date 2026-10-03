namespace Harness.Core.Config;

/// <summary>
/// Applies a workspace's <c>.harness/config.yaml</c> on top of an agent definition. Scalars override and lists append.
/// Anything that adds executable surface (MCP servers, plugins, skills) or loosens approvals applies only when the folder is trusted.
/// </summary>
public static class EffectiveAgent
{
    public static (AgentDefinition Agent, IReadOnlyList<string> Ignored) Resolve(AgentDefinition def, string workspaceRoot, TrustStore trust)
    {
        string file = Path.Combine(workspaceRoot, ".harness", "config.yaml");
        AgentDefinition merged = Clone(def);
        if (!File.Exists(file)) return (merged, []);

        FolderConfig folder = Yaml.Load<FolderConfig>(file);
        bool trusted = trust.IsTrusted(workspaceRoot);
        List<string> ignored = [];

        if (folder.Model is { Length: > 0 }) merged.Model = folder.Model;
        merged.Tools.Deny.AddRange(folder.DeniedTools);

        // A sandbox change is only safe without trust when it does not move execution onto the host.
        if (folder.Sandbox is { Length: > 0 })
        {
            if (trusted || folder.Sandbox != "none") merged.Sandbox = folder.Sandbox;
            else ignored.Add("sandbox: none");
        }

        foreach ((string tool, string policy) in folder.Approvals)
        {
            bool tightens = Rank(policy) <= Rank(merged.Approvals.GetValueOrDefault(tool, "allow"));
            if (trusted || tightens) merged.Approvals[tool] = policy;
            else ignored.Add($"approvals.{tool}: {policy}");
        }

        if (trusted)
        {
            merged.Tools.Mcp.AddRange(folder.Mcp);
            merged.Plugins.AddRange(folder.Plugins);
            merged.Skills.AddRange(folder.Skills.Select(s => Path.GetFullPath(Path.Combine(workspaceRoot, s))));
        }
        else
        {
            ignored.AddRange(folder.Mcp.Select(m => $"mcp: {m}"));
            ignored.AddRange(folder.Plugins.Select(p => $"plugins: {p}"));
            ignored.AddRange(folder.Skills.Select(s => $"skills: {s}"));
        }
        return (merged, ignored);
    }

    /// <summary>Lower is stricter.</summary>
    private static int Rank(string policy) => policy switch { "deny" => 0, "ask" => 1, "allowlist" => 2, _ => 3 };

    private static AgentDefinition Clone(AgentDefinition d) => new()
    {
        Name = d.Name,
        Description = d.Description,
        Model = d.Model,
        Instructions = d.Instructions,
        Tools = new AgentTools { Builtin = [.. d.Tools.Builtin], Mcp = [.. d.Tools.Mcp], Deny = [.. d.Tools.Deny] },
        Skills = [.. d.Skills],
        Plugins = [.. d.Plugins],
        Approvals = new(d.Approvals),
        Allowlist = d.Allowlist.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        Sandbox = d.Sandbox,
        Compaction = new CompactionConfig { ToolResultsAfter = d.Compaction.ToolResultsAfter, SlidingWindowTurns = d.Compaction.SlidingWindowTurns },
        Limits = new AgentLimits { MaxToolIterations = d.Limits.MaxToolIterations, MaxRunMinutes = d.Limits.MaxRunMinutes, MaxTokens = d.Limits.MaxTokens, MaxTokensReason = d.Limits.MaxTokensReason },
        SourcePath = d.SourcePath,
    };
}
