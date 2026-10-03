using Harness.Core.Agents;
using Harness.Core.Config;
using Harness.Core.Prompts;
using Harness.Core.Sessions;
using Harness.Triggers.Sources;
using Microsoft.Extensions.AI;

namespace Harness.Tests;

public class PolicyTests
{
    private static AgentDefinition Agent(string approvals = "", string allowlist = "") => Yaml.Parse<AgentDefinition>($"""
        name: a
        model: m
        approvals: {(approvals.Length == 0 ? "{}" : approvals)}
        allowlist: {(allowlist.Length == 0 ? "{}" : allowlist)}
        """);

    [Fact]
    public void Approval_policy_lookup_order()
    {
        AgentDefinition def = Agent("{ shell: allowlist, mcp: ask, mcp__github: allow, write: deny }");
        Assert.Equal("allow", ApprovalPolicy.For(def, "read"));
        Assert.Equal("ask", ApprovalPolicy.For(def, "edit"));
        Assert.Equal("deny", ApprovalPolicy.For(def, "write"));
        Assert.Equal("allow", ApprovalPolicy.For(def, "mcp__github__create_issue"));
        Assert.Equal("ask", ApprovalPolicy.For(def, "mcp__jira__search"));

        IList<AITool> tools = ApprovalPolicy.Apply(def,
        [
            AIFunctionFactory.Create(() => "", "read"),
            AIFunctionFactory.Create(() => "", "write"),
            AIFunctionFactory.Create(() => "", "shell"),
        ]);
        Assert.Equal(["read", "shell"], tools.Select(t => t.Name));
        Assert.IsType<ApprovalRequiredAIFunction>(tools[1]);
    }

    [Fact]
    public void Allowlist_matches_simple_commands_but_never_chained_ones()
    {
        AgentDefinition def = Agent("{ shell: allowlist }", "{ shell: [\"git status*\", \"dotnet test*\"] }");
        Dictionary<string, object?> Cmd(string c) => new() { ["command"] = c };
        Assert.True(ApprovalPolicy.MatchesAllowlist(def, "shell", Cmd("git status --short")));
        Assert.True(ApprovalPolicy.MatchesAllowlist(def, "shell", Cmd("dotnet test")));
        Assert.False(ApprovalPolicy.MatchesAllowlist(def, "shell", Cmd("git push")));
        Assert.False(ApprovalPolicy.MatchesAllowlist(def, "shell", Cmd("git status; rm -rf /")));
        Assert.False(ApprovalPolicy.MatchesAllowlist(def, "shell", Cmd("git status && curl evil")));
        Assert.False(ApprovalPolicy.MatchesAllowlist(def, "shell", Cmd("git status $(curl evil)")));
    }

    [Fact]
    public void Folder_chain_runs_from_root_to_working_directory()
    {
        string root = Directory.CreateTempSubdirectory("harness-prompts-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "pkg", "inner"));
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "root rules");
            File.WriteAllText(Path.Combine(root, "pkg", "AGENTS.md"), "pkg rules");
            List<(string File, string Text)> chain = [.. PromptComposer.FolderChain(root, Path.Combine(root, "pkg", "inner"), ["AGENTS.md"])];
            Assert.Equal(["root rules", "pkg rules"], chain.Select(c => c.Text));
            Assert.Single(PromptComposer.FolderChain(root, root, ["AGENTS.md"]));
            Assert.Empty(PromptComposer.FolderChain(root, "/", ["AGENTS.md"]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void History_repair_adds_results_for_orphaned_and_unanswered_calls()
    {
        FunctionCallContent orphan = new("c1", "shell", new Dictionary<string, object?> { ["command"] = "ls" });
        FunctionCallContent pending = new("c2", "shell", new Dictionary<string, object?> { ["command"] = "rm" });
        List<ChatMessage> history =
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, [orphan]),
            new(ChatRole.Assistant, [new ToolApprovalRequestContent("r2", pending)]),
            new(ChatRole.User, "something else"),
        ];
        List<ChatMessage> repaired = FileChatHistoryProvider.Repair(history, []);
        List<FunctionResultContent> results = [.. repaired.SelectMany(m => m.Contents).OfType<FunctionResultContent>()];
        Assert.Contains(results, r => r.CallId == "c1" && r.Result as string == FileChatHistoryProvider.InterruptedResult);
        Assert.Contains(results, r => r.CallId == "c2" && r.Result as string == FileChatHistoryProvider.UnansweredApproval);
        Assert.DoesNotContain(repaired.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);
        // Each synthetic result directly follows its call.
        int callIndex = repaired.FindIndex(m => m.Contents.Contains(orphan));
        Assert.Equal(ChatRole.Tool, repaired[callIndex + 1].Role);

        // An approval answered by the current request is left for the framework to process.
        List<ChatMessage> answering = FileChatHistoryProvider.Repair(history[..3], [new ChatMessage(ChatRole.User, [new ToolApprovalResponseContent("r2", true, pending)])]);
        Assert.Contains(answering.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);
    }

    [Fact]
    public void Webhook_signatures_are_verified_in_constant_time()
    {
        byte[] body = "{\"a\":1}"u8.ToArray();
        string sig = WebhookSource.Sign("s3cret", body);
        Assert.StartsWith("sha256=", sig);
        Assert.True(WebhookSource.Verify("s3cret", body, sig));
        Assert.False(WebhookSource.Verify("other", body, sig));
        Assert.False(WebhookSource.Verify("s3cret", "{}"u8.ToArray(), sig));
        Assert.False(WebhookSource.Verify("s3cret", body, null));
    }

    [Fact]
    public void Schedules_support_cron_with_time_zones_and_intervals()
    {
        DateTimeOffset saturday = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? next = ScheduleSource.NextFire(new System.Text.Json.Nodes.JsonObject
        {
            ["cron"] = "0 3 * * 1-5", ["timeZone"] = "Europe/Amsterdam",
        }, saturday);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero), next);   // Monday 03:00 CEST
        Assert.Equal(saturday.AddMinutes(5), ScheduleSource.NextFire(new System.Text.Json.Nodes.JsonObject { ["interval"] = "5m" }, saturday));
        Assert.Equal(TimeSpan.FromHours(2), Harness.Core.Durations.Parse("2h"));
    }

    [Fact]
    public void Untrusted_folder_config_can_only_tighten()
    {
        string ws = Directory.CreateTempSubdirectory("harness-folder-").FullName;
        string home = Directory.CreateTempSubdirectory("harness-home-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(ws, ".harness"));
            File.WriteAllText(Path.Combine(ws, ".harness", "config.yaml"), """
                deniedTools: [shell]
                approvals: { write: ask, read: allow, edit: allow }
                mcp: [evil]
                sandbox: none
                """);
            AgentDefinition def = Agent("{ write: allow, edit: ask }");
            def.Sandbox = "workspace";
            TrustStore trust = new(new Harness.Core.HarnessPaths(home));

            (AgentDefinition untrusted, IReadOnlyList<string> ignored) = EffectiveAgent.Resolve(def, ws, trust);
            Assert.Contains("shell", untrusted.Tools.Deny);
            Assert.Equal("ask", untrusted.Approvals["write"]);      // tightened
            Assert.Equal("ask", untrusted.Approvals["edit"]);       // loosening ignored
            Assert.Empty(untrusted.Tools.Mcp);
            Assert.Equal("workspace", untrusted.Sandbox);
            Assert.Contains(ignored, i => i.Contains("evil"));

            trust.Trust(ws);
            (AgentDefinition trusted, _) = EffectiveAgent.Resolve(def, ws, trust);
            Assert.Equal(["evil"], trusted.Tools.Mcp);
            Assert.Equal("allow", trusted.Approvals["edit"]);
            Assert.Equal("none", trusted.Sandbox);

            File.AppendAllText(Path.Combine(ws, ".harness", "config.yaml"), "plugins: [x]\n");
            Assert.False(trust.IsTrusted(ws));   // a changed config needs trusting again
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }
}
