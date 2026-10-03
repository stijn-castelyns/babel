using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Config;
using Microsoft.Extensions.AI;

namespace Harness.Core.Agents;

/// <summary>Maps an agent's <c>approvals:</c> block onto tools.</summary>
public static class ApprovalPolicy
{
    public const string Ask = "ask", Allow = "allow", Deny = "deny", AllowlistPolicy = "allowlist";

    private static readonly HashSet<string> ReadOnlyTools = ["read", "list", "glob", "grep", "load_skill", "read_skill_resource", "submit_output"];

    /// <summary>
    /// The policy for one tool. Lookup order: exact tool name, <c>mcp__&lt;server&gt;</c>, <c>mcp</c> for MCP tools,
    /// <c>skills</c> for skill scripts, then the default (read-only tools allowed, everything else asks).
    /// </summary>
    public static string For(AgentDefinition def, string toolName)
    {
        if (def.Approvals.TryGetValue(toolName, out string? exact)) return exact;
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            string server = string.Join("__", toolName.Split("__").Take(2));
            if (def.Approvals.TryGetValue(server, out string? perServer)) return perServer;
            if (def.Approvals.TryGetValue("mcp", out string? mcp)) return mcp;
        }
        if (toolName == "run_skill_script" && def.Approvals.TryGetValue("skills", out string? skills)) return skills;
        if (def.Approvals.TryGetValue("*", out string? star)) return star;
        return ReadOnlyTools.Contains(toolName) ? Allow : Ask;
    }

    /// <summary>Drops denied tools and marks <c>ask</c>/<c>allowlist</c> tools as approval-required.</summary>
    public static IList<AITool> Apply(AgentDefinition def, IEnumerable<AITool> tools)
    {
        List<AITool> result = [];
        foreach (AITool tool in tools)
        {
            if (def.Tools.Deny.Contains(tool.Name)) continue;
            string policy = For(def, tool.Name);
            if (policy == Deny) continue;
            if (policy is Ask or AllowlistPolicy && tool is AIFunction fn && tool is not ApprovalRequiredAIFunction)
                result.Add(new ApprovalRequiredAIFunction(fn));
            else
                result.Add(tool);
        }
        return result;
    }

    /// <summary>True when an <c>allowlist</c> tool call matches one of the agent's patterns (<c>*</c> wildcards over the main argument).</summary>
    public static bool MatchesAllowlist(AgentDefinition def, string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        if (For(def, toolName) != AllowlistPolicy || !def.Allowlist.TryGetValue(toolName, out List<string>? patterns)) return false;
        string subject = MainArgument(toolName, arguments) ?? "";
        // Commands chained with shell operators never match: "git status; rm -rf /" is not "git status*".
        if (toolName == "shell" && Regex.IsMatch(subject, @"[;&|`$<>]|\n")) return false;
        return patterns.Any(p => Regex.IsMatch(subject, "^" + Regex.Escape(p).Replace("\\*", ".*") + "$", RegexOptions.Singleline));
    }

    /// <summary>The argument that best describes a call: the command for shell, the path for file tools.</summary>
    public static string? MainArgument(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        foreach (string key in new[] { "command", "path", "pattern", "handle", "query", "name" })
            if (arguments.TryGetValue(key, out object? value) && value is not null)
                return value is JsonElement e ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()) : value.ToString();
        return null;
    }

    /// <summary>A stable key for "approve this exact invocation for the rest of the session".</summary>
    public static string InvocationKey(string toolName, IReadOnlyDictionary<string, object?> arguments) =>
        toolName + "\0" + JsonSerializer.Serialize(arguments.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value), AIJsonUtilities.DefaultOptions);
}
