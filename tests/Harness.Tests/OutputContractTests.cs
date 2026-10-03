using System.Text.Json;
using Harness.Core.Sessions;
using Harness.Runs;
using Harness.Sdk;
using Harness.Tests.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

public class OutputContractTests
{
    private const string ReportSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "summary": { "type": "string", "minLength": 3 },
            "updated": { "type": "integer", "minimum": 0 }
          },
          "required": ["summary", "updated"],
          "additionalProperties": false
        }
        """;

    private static async Task<(TestHome Home, RunResult Result, RunRecord Run)> FireAsync(string templateYaml, ScriptedChatClient model,
        string triggerExtra = "", params (string Path, string Content)[] extraFiles)
    {
        TestHome home = new();
        home.WriteTemplate("job", [("template.yaml", templateYaml), .. extraFiles]);
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "job.yaml"), "template: job\nallowUnsandboxed: true\n" + triggerExtra);
        home.Build(model);
        await home.Triggers.StartAsync(CancellationToken.None);
        RunRecord run = await home.Triggers.FireAsync("job", "go", null, CancellationToken.None);
        RunResult result = await home.Orchestrator.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(60));
        return (home, result, run);
    }

    [Fact]
    public async Task Invalid_submission_gets_the_errors_and_a_valid_one_succeeds_with_output_and_files()
    {
        List<string> toolResults = [];
        JsonElement? toolSchema = null;
        ScriptedChatClient model = new((messages, options) =>
        {
            toolSchema ??= options?.Tools?.OfType<AIFunction>().FirstOrDefault(t => t.Name == "submit_output")?.JsonSchema;
            toolResults.AddRange(ScriptedChatClient.LastResults(messages).Select(r => r.Result?.ToString() ?? ""));
            return messages.Count(m => m.Role == ChatRole.Tool) switch
            {
                0 => ScriptedChatClient.Call("submit_output", new() { ["summary"] = "ok", ["updated"] = "three" }),
                1 => ScriptedChatClient.Call("write", new() { ["path"] = "report.md", ["content"] = "# Report\n" }),
                2 => ScriptedChatClient.Call("submit_output", new() { ["summary"] = "bumped 3 packages", ["updated"] = 3 }),
                _ => ScriptedChatClient.Text("Submitted."),
            };
        });
        (TestHome home, RunResult result, RunRecord run) = await FireAsync("""
            sandbox: none
            output: { kind: json, schema: schemas/report.schema.json, files: [report.md], retries: 2 }
            """, model, "", ("schemas/report.schema.json", ReportSchema));
        await using TestHome owned = home;

        Assert.True(result.State == RunStates.Succeeded, result.Error);
        Assert.Equal("bumped 3 packages", result.Output!["summary"]!.GetValue<string>());
        Assert.Equal(3, result.Output["updated"]!.GetValue<int>());

        // The model saw the template's schema as the tool's parameters, without $schema.
        Assert.NotNull(toolSchema);
        Assert.Equal("integer", toolSchema!.Value.GetProperty("properties").GetProperty("updated").GetProperty("type").GetString());
        Assert.False(toolSchema.Value.TryGetProperty("$schema", out _));

        // The first submission was rejected with every problem listed.
        Assert.StartsWith("Output rejected", toolResults[0]);
        Assert.Contains("/summary", toolResults[0]);
        Assert.Contains("/updated", toolResults[0]);
        Assert.Contains("report.md", toolResults[0]);
        Assert.StartsWith("Output accepted", toolResults[2]);

        // Output and declared files are kept with the run, outside the (removed) workspace.
        string outputDir = Path.Combine(home.Paths.RunsDir, run.Id, "output");
        string copied = Assert.Single(result.Files);
        Assert.Equal(Path.Combine(outputDir, "files", "report.md"), copied);
        Assert.Equal("# Report\n", File.ReadAllText(copied));
        Assert.Equal(3, JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDir, "output.json"))).RootElement.GetProperty("updated").GetInt32());
        Assert.False(Directory.Exists(Path.Combine(home.Paths.RunsDir, run.Id, "workspace")));

        RunRecord stored = home.Orchestrator.Sessions.Index.GetRun(run.Id)!;
        Assert.Contains("bumped 3 packages", stored.Output);
        Assert.Equal([copied], stored.Files);
        HarnessEvent[] validated = [.. home.Orchestrator.Sessions.Open(run.SessionId).ReadEvents().Where(e => e.Type == EventTypes.OutputValidated)];
        Assert.Equal([false, true, true], validated.Select(e => e.Data["valid"]!.GetValue<bool>()));
        Assert.Contains(home.Orchestrator.Sessions.Open(run.SessionId).ReadEvents(), e => e.Type == EventTypes.RunState && e.Data["state"]?.GetValue<string>() == RunStates.Validating);
    }

    [Fact]
    public async Task Stopping_without_output_is_retried_and_then_ends_as_invalid_output()
    {
        ScriptedChatClient model = new((_, _) => ScriptedChatClient.Text("I think I am done."));
        (TestHome home, RunResult result, RunRecord run) = await FireAsync("sandbox: none\noutput: { kind: text, retries: 1 }\n", model);
        await using TestHome owned = home;

        Assert.Equal(RunStates.InvalidOutput, result.State);
        Assert.Contains("without calling submit_output", result.Error);
        Assert.Equal(2, model.Calls.Count);   // the first try plus one retry
        Assert.Contains("This is the last attempt.", model.Calls[1].Last(m => m.Role == ChatRole.User).Text);
        Assert.True(Directory.Exists(Path.Combine(home.Paths.RunsDir, run.Id, "workspace")));   // keep: onFailure
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task Text_output_becomes_the_run_text_and_non_object_schemas_are_wrapped()
    {
        ScriptedChatClient textModel = new((messages, _) => messages.Any(m => m.Role == ChatRole.Tool)
            ? ScriptedChatClient.Text("done")
            : ScriptedChatClient.Call("submit_output", new() { ["text"] = "Here is your answer." }));
        (TestHome home, RunResult result, _) = await FireAsync("sandbox: none\noutput: { kind: reply }\n", textModel);
        await using (home)
        {
            Assert.True(result.State == RunStates.Succeeded, result.Error);
            Assert.Equal("Here is your answer.", result.Text);
        }

        ScriptedChatClient listModel = new((messages, _) => messages.Count(m => m.Role == ChatRole.Tool) switch
        {
            0 => ScriptedChatClient.Call("submit_output", new() { ["output"] = new[] { 1, 2 } }),
            1 => ScriptedChatClient.Call("submit_output", new() { ["output"] = new[] { "a", "b" } }),
            _ => ScriptedChatClient.Text("done"),
        });
        (TestHome home2, RunResult list, _) = await FireAsync("sandbox: none\noutput: { kind: json, schema: list.json }\n", listModel, "",
            ("list.json", """{ "type": "array", "items": { "type": "string" } }"""));
        await using (home2)
        {
            Assert.True(list.State == RunStates.Succeeded, list.Error);
            Assert.Equal("""["a","b"]""", list.Output!.ToJsonString());
        }
    }

    [Fact]
    public async Task Templated_run_parked_on_approval_resumes_after_a_restart_without_rebuilding_its_workspace()
    {
        await using TestHome home = new("""
            name: coder
            model: fake
            approvals: { shell: ask, write: allow, edit: allow }
            """);
        home.WriteTemplate("job",
            ("template.yaml", "sandbox: none\nworkspace: { keep: always }\noutput: { kind: text }\n"),
            ("setup.sh", "echo built >> builds.txt\n"));
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "job.yaml"), "template: job\nallowUnsandboxed: true\napprovals: { timeout: 10m }\n");
        static ScriptedChatClient Model() => new((messages, _) => messages.Count(m => m.Role == ChatRole.Tool) switch
        {
            0 => ScriptedChatClient.Call("shell", new() { ["command"] = "cat builds.txt" }),
            1 => ScriptedChatClient.Call("submit_output", new() { ["text"] = "builds: " + ScriptedChatClient.LastResults(messages)[0].Result }),
            _ => ScriptedChatClient.Text("done"),
        });

        home.Build(Model());
        await home.Triggers.StartAsync(CancellationToken.None);
        RunRecord run = await home.Triggers.FireAsync("job", "go", null, CancellationToken.None);
        await RunOrchestratorTests.WaitForAsync(() => home.Orchestrator.Approvals.Pending(run.Id).FirstOrDefault());
        await home.Services!.DisposeAsync();   // daemon stops while the run waits for a person

        home.Build(Model());
        home.Services!.GetRequiredService<HarnessDb>().FailInterruptedRuns();
        RunOrchestrator runs = home.Orchestrator;
        runs.RehydrateParkedRuns();
        PendingApproval pending = Assert.Single(runs.Approvals.Pending(run.Id));
        Assert.True(runs.ResolveApproval(run.Id, pending.RequestId, new ApprovalAnswer(true, null, "test", "test")));

        RunResult result = await runs.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.State == RunStates.Succeeded, result.Error);
        Assert.Contains("builds:", result.Text);
        Assert.Equal("built\n", File.ReadAllText(Path.Combine(home.Paths.RunsDir, run.Id, "workspace", "builds.txt")));
    }
}
