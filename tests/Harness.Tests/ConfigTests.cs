using Harness.Core.Config;

namespace Harness.Tests;

public class ConfigTests
{
    [Fact]
    public void Empty_sections_get_defaults()
    {
        HarnessConfig config = Yaml.Parse<HarnessConfig>("""
            models:
              local: { provider: ollama, endpoint: http://localhost:11434, model: m }
            listeners:
              # api: http://127.0.0.1:7443
            hooks:
            """);
        Assert.NotNull(config.Listeners);
        Assert.NotNull(config.Hooks);
        Assert.Equal("coder", config.Defaults.Agent);
        Assert.Equal("ollama", config.Models["local"].Provider);
    }

    [Fact]
    public void Unknown_keys_are_errors()
    {
        Assert.ThrowsAny<Exception>(() => Yaml.Parse<AgentDefinition>("name: x\nmodle: typo\n"));
    }

    [Fact]
    public void Agent_definition_parses_the_documented_shape()
    {
        AgentDefinition def = Yaml.Parse<AgentDefinition>("""
            name: coder
            model: azure
            instructions: prompts/coder.md
            tools:
              builtin: [read, list, glob, grep, edit, write, shell]
              mcp: [github]
            skills: [skills/]
            plugins: [Contoso.GitHooks]
            approvals: { shell: ask, write: allow, mcp: ask }
            sandbox: workspace
            compaction: { toolResultsAfter: 20, slidingWindowTurns: 30 }
            limits: { maxToolIterations: 60, maxRunMinutes: 30 }
            """);
        Assert.Equal(["github"], def.Tools.Mcp);
        Assert.Equal("ask", def.Approvals["mcp"]);
        Assert.Equal(20, def.Compaction.ToolResultsAfter);
    }
}
