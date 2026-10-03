using System.Diagnostics;
using Harness.Core.Config;
using Harness.Core.Sessions;
using Harness.Runs;
using Harness.Runs.Templates;
using Harness.Sdk;
using Harness.Tests.TestSupport;
using Microsoft.Extensions.AI;

namespace Harness.Tests;

public class TemplateTests
{
    [Fact]
    public void Variables_render_known_placeholders_and_leave_others()
    {
        Dictionary<string, string> vars = new() { ["inputs.repo"] = "api", ["event.text"] = "hi" };
        Assert.Equal("api says hi to {nobody} and { spaced }", TemplateVariables.Render("{inputs.repo} says {event.text} to {nobody} and { spaced }", vars));

        TriggerEvent evt = new("e1", "t1", DateTimeOffset.UtcNow, "+31", "hello", [], new() { ["inputs"] = new System.Text.Json.Nodes.JsonObject { ["b"] = "given" } }, null);
        Dictionary<string, string> fromEvent = TemplateVariables.ForEvent(evt,
            [new Dictionary<string, string> { ["a"] = "template", ["b"] = "template" }, new Dictionary<string, string> { ["a"] = "trigger {event.sender}" }]);
        Assert.Equal("trigger +31", fromEvent["inputs.a"]);
        Assert.Equal("given", fromEvent["inputs.b"]);
        Assert.Equal("t1", fromEvent["trigger.id"]);
    }

    [Fact]
    public void Template_files_are_parsed_strictly_except_steps_and_sinks()
    {
        RunTemplate t = RunTemplate.Parse("""
            name: deps
            workspace:
              steps:
                - git: { url: "{inputs.repo}", depth: 1, into: repo }
                - run: ./setup.sh
                - snapshot: { db: main }
              keep: never
            output: { kind: json, retries: 1, sinks: [reply, { type: file, path: out.md }] }
            """);
        Assert.Equal(3, t.Workspace.Steps!.Count);
        Assert.Equal(1, t.Workspace.Steps[0]!["git"]!["depth"]!.GetValue<int>());
        Assert.Equal("never", t.Workspace.Keep);
        Assert.Equal(2, t.Output.Sinks!.Count);
        Assert.Empty(t.Problems());

        Assert.ThrowsAny<Exception>(() => RunTemplate.Parse("prompt: x\nunknownKey: 1\n"));
        Assert.Contains(RunTemplate.Parse("workspace: { keep: sometimes }").Problems(), p => p.Contains("keep"));
        Assert.Contains(RunTemplate.Parse("output: { kind: yaml }").Problems(), p => p.Contains("kind"));
    }

    [Fact]
    public async Task Templated_trigger_builds_a_fresh_workspace_runs_the_agent_and_removes_it_on_success()
    {
        await using TestHome home = new();
        string origin = CreateGitRepo(home.Root, ("README.md", "origin readme\n"));
        home.WriteTemplate("deps",
            ("template.yaml", """
                agent: coder
                sandbox: none
                workspace:
                  steps:
                    - git: { url: "{inputs.repo}", into: repo, depth: 1 }
                    - copy: { from: files/, into: . }
                    - run: ./setup.sh
                  keep: onFailure
                prompt: "Look at {inputs.repo} about {event.text}"
                instructions: Keep answers brief.
                inputs: { repo: nowhere, greeting: hello }
                """),
            ("files/notes.md.tmpl", "Greeting: {inputs.greeting} in run {run.id}; code keeps {braces}\n"),
            ("files/plain.txt", "{inputs.greeting} stays literal\n"),
            ("setup.sh", "echo setup-ran > setup.out\n"),
            ("AGENTS.md", "Template rules: work in repo/.\n"));
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "deps.yaml"), $"""
            id: deps
            template: deps
            allowUnsandboxed: true
            inputs: {"{"} repo: "{origin}" {"}"}
            """);

        List<string> seen = [];
        ScriptedChatClient model = new((messages, options) =>
        {
            int step = messages.Count(m => m.Role == ChatRole.Tool);
            if (step == 0)
            {
                seen.Add(string.Join("\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text)) + options?.Instructions);
                seen.Add(messages.Last(m => m.Role == ChatRole.User).Text);
            }
            foreach (FunctionResultContent r in ScriptedChatClient.LastResults(messages)) seen.Add(r.Result?.ToString() ?? "");
            return step switch
            {
                0 => ScriptedChatClient.Call("read", new() { ["path"] = "repo/README.md" }),
                1 => ScriptedChatClient.Call("read", new() { ["path"] = "notes.md" }),
                2 => ScriptedChatClient.Call("read", new() { ["path"] = "setup.out" }),
                3 => ScriptedChatClient.Call("read", new() { ["path"] = "plain.txt" }),
                _ => ScriptedChatClient.Text("all good"),
            };
        });
        home.Build(model);
        await home.Triggers.StartAsync(CancellationToken.None);

        RunRecord run = await home.Triggers.FireAsync("deps", "updates", null, CancellationToken.None);
        RunResult result = await home.Orchestrator.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(result.State == RunStates.Succeeded, result.Error);

        Assert.Contains("Keep answers brief.", seen[0]);
        Assert.Contains("Template rules: work in repo/.", seen[0]);   // the template's AGENTS.md became the folder prompt layer
        Assert.Equal($"Look at {origin} about updates", seen[1]);
        Assert.Contains("origin readme", seen[2]);
        Assert.Contains($"Greeting: hello in run {run.Id}; code keeps {{braces}}", seen[3]);
        Assert.Contains("setup-ran", seen[4]);
        Assert.Contains("{inputs.greeting} stays literal", seen[5]);

        SessionFolder session = home.Orchestrator.Sessions.Open(run.SessionId);
        string workspace = Path.Combine(home.Paths.RunsDir, run.Id, "workspace");
        Assert.Equal(workspace, session.Info.Workspace);
        Assert.False(Directory.Exists(workspace));   // keep: onFailure removes it after success
        Assert.Equal(3, session.ReadEvents().Count(e => e.Type == EventTypes.WorkspaceStep && e.Data["status"]?.GetValue<string>() == "finished"));
    }

    [Fact]
    public async Task Failing_setup_step_fails_the_run_before_any_model_call_and_keeps_the_workspace()
    {
        await using TestHome home = new();
        home.WriteTemplate("broken",
            ("template.yaml", "sandbox: none\n"),
            ("files/a.txt", "a"),
            ("setup.sh", "echo about to fail; exit 3\n"));
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "broken.yaml"), "template: broken\nallowUnsandboxed: true\n");
        ScriptedChatClient model = new((_, _) => ScriptedChatClient.Text("should not be called"));
        home.Build(model);
        await home.Triggers.StartAsync(CancellationToken.None);

        RunRecord run = await home.Triggers.FireAsync("broken", "go", null, CancellationToken.None);
        RunResult result = await home.Orchestrator.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RunStates.Failed, result.State);
        Assert.Contains("exited with 3", result.Error);
        Assert.Empty(model.Calls);
        string workspace = Path.Combine(home.Paths.RunsDir, run.Id, "workspace");
        Assert.True(File.Exists(Path.Combine(workspace, "a.txt")));   // default steps: copy files/, then run setup.sh
        HarnessEvent failed = home.Orchestrator.Sessions.Open(run.SessionId).ReadEvents().Single(e => e.Type == EventTypes.WorkspaceStep && e.Data["status"]?.GetValue<string>() == "failed");
        Assert.Contains("about to fail", failed.Data["log"]!.GetValue<string>());
    }

    [Fact]
    public async Task Setup_runs_inside_the_sandbox_with_the_template_mounted_read_only()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/bwrap")) return;
        await using TestHome home = new();
        File.WriteAllText(home.Paths.SandboxesFile, "box:\n  type: bubblewrap\n  network: none\n");
        home.WriteTemplate("boxed",
            ("template.yaml", "sandbox: box\nworkspace: { keep: always }\n"),
            ("setup.sh", """
                echo "template=$HARNESS_TEMPLATE pwd=$(pwd)" > where.txt
                if touch "$HARNESS_TEMPLATE/x" 2>/dev/null; then echo writable >> where.txt; else echo readonly >> where.txt; fi
                """));
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "boxed.yaml"), "template: boxed\n");
        home.Build(new ScriptedChatClient((_, _) => ScriptedChatClient.Text("ok")));
        await home.Triggers.StartAsync(CancellationToken.None);

        RunRecord run = await home.Triggers.FireAsync("boxed", "go", null, CancellationToken.None);
        RunResult result = await home.Orchestrator.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.State == RunStates.Succeeded, result.Error);

        string where = File.ReadAllText(Path.Combine(home.Paths.RunsDir, run.Id, "workspace", "where.txt"));   // keep: always
        Assert.Contains("template=/harness/template pwd=/workspace", where);
        Assert.Contains("readonly", where);
        Assert.False(File.Exists(Path.Combine(home.Paths.TemplatesDir, "boxed", "x")));
    }

    internal static string CreateGitRepo(string root, params (string Path, string Content)[] files)
    {
        string repo = Path.Combine(root, "origin-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(repo);
        foreach ((string path, string content) in files) File.WriteAllText(Path.Combine(repo, path), content);
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "init");
        return repo;
    }

    private static void Git(string dir, params string[] args)
    {
        ProcessStartInfo psi = new("git") { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }
}
