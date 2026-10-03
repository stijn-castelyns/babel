using System.ComponentModel;
using Harness.Core.Sessions;
using Harness.Extensions.Plugins;
using Harness.Runs;
using Harness.Sdk;
using Harness.Tests.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

public class PluginTests
{
    public sealed class GitGuardPlugin : IHarnessPlugin
    {
        public string Id => "contoso.git-guard";
        public void Configure(IPluginBuilder plugin) => plugin.AddTools<GitTools>().AddHook<NoForcePush>();
    }

    public sealed class GitTools
    {
        [Description("Return the current branch name.")]
        public string CurrentBranch() => "main";
    }

    public sealed class NoForcePush : IHarnessHook
    {
        public ValueTask OnToolCallingAsync(ToolCallingContext ctx, CancellationToken ct)
        {
            if (ctx.ToolName == "shell" && ctx.Argument<string>("command")?.Contains("push --force") == true)
                ctx.Block("Force-push is blocked by policy.");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Plugin_tools_and_hooks_take_part_in_runs()
    {
        await using TestHome home = new("""
            name: coder
            model: fake
            plugins: [contoso.git-guard]
            approvals: { shell: allow, current_branch: allow }
            """);
        ScriptedChatClient model = new((m, options) =>
        {
            IReadOnlyList<FunctionResultContent> results = ScriptedChatClient.LastResults(m);
            if (results.Count == 0) return ScriptedChatClient.Call("current_branch", []);
            if (results[0].Result?.ToString() == "main") return ScriptedChatClient.Call("shell", new() { ["command"] = "git push --force" });
            return ScriptedChatClient.Text("result: " + results[0].Result);
        });
        home.Build(model);
        home.Services!.GetRequiredService<PluginRegistry>().Add(new GitGuardPlugin());

        RunOrchestrator runs = home.Orchestrator;
        SessionFolder session = runs.CreateSession(new SessionRequest { Workspace = home.Workspace });
        RunRecord run = runs.Start(new RunRequest { SessionId = session.Id, Messages = [new ChatMessage(ChatRole.User, "push it")] });
        RunResult result = await runs.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RunStates.Succeeded, result.State);
        Assert.Equal("result: Blocked: Force-push is blocked by policy.", result.Text);
        Assert.Contains(model.Options[0]!.Tools!, t => t.Name == "current_branch");
    }

    [Fact]
    public void Plugin_manifest_checks_the_sdk_range_and_tool_names()
    {
        Assert.True(new PluginManifest { Sdk = "1" }.SupportsSdk(1));
        Assert.True(new PluginManifest { Sdk = "1-2" }.SupportsSdk(2));
        Assert.False(new PluginManifest { Sdk = "2" }.SupportsSdk(1));
        Assert.Equal("get_file_status", LoadedPlugin.ToolName("GetFileStatusAsync"));
        Assert.True(PluginLoadContext.IsShared("Harness.Sdk"));
        Assert.True(PluginLoadContext.IsShared("Microsoft.Extensions.AI.Abstractions"));
        Assert.False(PluginLoadContext.IsShared("Newtonsoft.Json"));
    }
}
