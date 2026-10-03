using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Harness.Core.Sessions;
using Harness.Extensions.Plugins;
using Harness.Runs;
using Harness.Sdk;
using Harness.Tests.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

public class SinkTests
{
    /// <summary>Submits <paramref name="output"/> on the first call, then says done.</summary>
    private static ScriptedChatClient Submits(Dictionary<string, object?> output) => new((messages, _) => messages.Any(m => m.Role == ChatRole.Tool)
        ? ScriptedChatClient.Text("done")
        : ScriptedChatClient.Call("submit_output", output));

    private static async Task<RunResult> FireAndWaitAsync(TestHome home, string trigger, string text = "go")
    {
        RunRecord run = await home.Triggers.FireAsync(trigger, text, null, CancellationToken.None);
        return await home.Orchestrator.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static int FreePort()
    {
        using System.Net.Sockets.TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task File_and_signed_webhook_sinks_receive_the_output()
    {
        await using TestHome home = new();
        int port = FreePort();
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task<(string Body, string? Signature, string? Custom)> received = Task.Run(async () =>
        {
            HttpListenerContext ctx = await listener.GetContextAsync();
            string body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            (string, string?, string?) got = (body, ctx.Request.Headers["X-Harness-Signature"], ctx.Request.Headers["X-Custom"]);
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return got;
        });

        home.WriteTemplate("report",
            ("template.yaml", """
                sandbox: none
                output: { kind: json, schema: s.json }
                """),
            ("s.json", """{ "type": "object", "properties": { "summary": { "type": "string" }, "count": { "type": "integer" } }, "required": ["summary"] }"""));
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "nightly.yaml"), $$"""
            template: report
            allowUnsandboxed: true
            output:
              sinks:
                - { type: file, path: "reports/{trigger.id}-{output.count}.md", from: output.summary }
                - { type: webhook, url: "http://127.0.0.1:{{port}}/hook", secret: k3y-for-tests, headers: { X-Custom: "run {run.id}" } }
            """);
        home.Build(Submits(new() { ["summary"] = "3 packages bumped", ["count"] = 3 }));
        await home.Triggers.StartAsync(CancellationToken.None);

        RunResult result = await FireAndWaitAsync(home, "nightly");
        Assert.True(result.State == RunStates.Succeeded, result.Error);
        Assert.Equal("3 packages bumped", File.ReadAllText(Path.Combine(home.Paths.Home, "reports", "nightly-3.md")));

        (string body, string? signature, string? custom) = await received.WaitAsync(TimeSpan.FromSeconds(10));
        JsonObject json = JsonNode.Parse(body)!.AsObject();
        Assert.Equal(result.RunId, json["runId"]!.GetValue<string>());
        Assert.Equal("succeeded", json["state"]!.GetValue<string>());
        Assert.Equal(3, json["output"]!["count"]!.GetValue<int>());
        Assert.Equal("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("k3y-for-tests"u8.ToArray(), Encoding.UTF8.GetBytes(body))), signature);
        Assert.Equal($"run {result.RunId}", custom);

        HarnessEvent[] events = [.. home.Orchestrator.Sessions.Open(result.SessionId).ReadEvents()];
        Assert.Equal(2, events.Count(e => e.Type == EventTypes.OutputDelivered && e.Data["ok"]!.GetValue<bool>()));
        Assert.Contains(events, e => e.Type == EventTypes.RunState && e.Data["state"]?.GetValue<string>() == RunStates.Delivering);
    }

    [Fact]
    public async Task Failed_delivery_fails_the_run_and_sinks_can_opt_in_to_failures()
    {
        await using TestHome home = new();
        home.WriteTemplate("t", ("template.yaml", "sandbox: none\noutput: { kind: text, retries: 0 }\n"));
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "broken-hook.yaml"), $"""
            template: t
            allowUnsandboxed: true
            output: {"{"} sinks: [{"{"} type: webhook, url: "http://127.0.0.1:{FreePort()}/nobody-listens" {"}"}] {"}"}
            """);
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "notify.yaml"), """
            template: t
            allowUnsandboxed: true
            output:
              sinks:
                - { type: file, path: ok.txt }
                - { type: file, path: problems.txt, from: error, on: [invalid_output, failed] }
            """);
        int call = 0;
        home.Build(new ScriptedChatClient((messages, _) =>
            Interlocked.Increment(ref call) == 1 ? ScriptedChatClient.Call("submit_output", new() { ["text"] = "fine" })
            : messages.Any(m => m.Role == ChatRole.Tool) && call == 2 ? ScriptedChatClient.Text("done")
            : ScriptedChatClient.Text("no output this time")));
        await home.Triggers.StartAsync(CancellationToken.None);

        RunResult failed = await FireAndWaitAsync(home, "broken-hook");
        Assert.Equal(RunStates.Failed, failed.State);
        Assert.StartsWith("Output was valid but delivery failed: webhook:", failed.Error);
        Assert.Equal("fine", failed.Text);   // the output itself is kept
        Assert.Contains("fine", home.Orchestrator.Sessions.Index.GetRun(failed.RunId)!.Output);

        RunResult invalid = await FireAndWaitAsync(home, "notify");
        Assert.Equal(RunStates.InvalidOutput, invalid.State);
        Assert.False(File.Exists(Path.Combine(home.Paths.Home, "ok.txt")));   // default on: [succeeded]
        Assert.Contains("without calling submit_output", File.ReadAllText(Path.Combine(home.Paths.Home, "problems.txt")));
    }

    [Fact]
    public async Task Reply_sink_answers_the_sender_through_the_source_that_received_the_event()
    {
        await using TestHome home = new();
        home.WriteTemplate("assistant", ("template.yaml", "sandbox: none\noutput: { kind: reply }\n"));
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "chat.yaml"), """
            source: { type: test-chat }
            template: assistant
            allowUnsandboxed: true
            session: "chat:{event.sender}"
            output: { sinks: [reply] }
            """);
        home.Build(Submits(new() { ["text"] = "Hello back!" }));
        home.Services!.GetRequiredService<PluginRegistry>().Add(new ChatPlugin());
        await home.Triggers.StartAsync(CancellationToken.None);

        ChatSource source = ChatSource.Started!;
        await source.ReceiveAsync("+31612345678", "hi there");
        string runId = await RunOrchestratorTests.WaitForAsync(() => home.Triggers.LastFire("chat").LastRunId);
        RunResult result = await home.Orchestrator.WaitAsync(runId).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.State == RunStates.Succeeded, result.Error);

        (ReplyAddress to, string text) = Assert.Single(source.Sent);
        Assert.Equal("+31612345678", to.Address);
        Assert.Equal("Hello back!", text);

        // A manual fire has no reply address: the reply sink skips instead of failing the run.
        RunResult manual = await FireAndWaitAsync(home, "chat");
        Assert.True(manual.State == RunStates.Succeeded, manual.Error);
        Assert.Single(source.Sent);
    }

    [Fact]
    public async Task Run_sink_chains_triggers_and_stops_runaway_chains()
    {
        await using TestHome home = new();
        home.WriteTemplate("t", ("template.yaml", "sandbox: none\noutput: { kind: json }\n"));
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "first.yaml"), """
            template: t
            allowUnsandboxed: true
            output: { sinks: [{ type: run, trigger: second, text: "Follow up on {output.topic}", inputs: { topic: "{output.topic}" } }] }
            """);
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "second.yaml"), $"""
            workspace: {home.Workspace}
            allowUnsandboxed: true
            prompt: "{"{"}event.text{"}"} (input {"{"}inputs.topic{"}"})"
            """);
        File.WriteAllText(Path.Combine(home.Paths.TriggersDir, "loop.yaml"), """
            template: t
            allowUnsandboxed: true
            output: { sinks: [{ type: run, trigger: loop }] }
            """);
        List<string> prompts = [];
        home.Build(new ScriptedChatClient((messages, _) =>
        {
            if (messages.Any(m => m.Role == ChatRole.Tool)) return ScriptedChatClient.Text("done");
            lock (prompts) prompts.Add(messages.Last(m => m.Role == ChatRole.User).Text);
            return ScriptedChatClient.Call("submit_output", new() { ["topic"] = "dependencies" });
        }));
        await home.Triggers.StartAsync(CancellationToken.None);

        RunResult first = await FireAndWaitAsync(home, "first");
        Assert.True(first.State == RunStates.Succeeded, first.Error);
        string secondId = await RunOrchestratorTests.WaitForAsync(() => home.Triggers.LastFire("second").LastRunId);
        RunResult second = await home.Orchestrator.WaitAsync(secondId).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("done", second.Text);
        Assert.Contains("Follow up on dependencies (input dependencies)", prompts);

        // loop → loop → … : the sixth run's delivery is refused, which fails that run and ends the chain.
        await FireAndWaitAsync(home, "loop");
        RunRecord last = await RunOrchestratorTests.WaitForAsync(() =>
            home.Orchestrator.Sessions.Index.ListRuns().FirstOrDefault(r => r.TriggerId == "loop" && r.State == RunStates.Failed));
        Assert.Contains("run chains stop after 5 runs", last.Error);
        Assert.Equal(6, home.Orchestrator.Sessions.Index.ListRuns().Count(r => r.TriggerId == "loop"));
    }

    private sealed class ChatPlugin : IHarnessPlugin
    {
        public string Id => "test.chat";
        public void Configure(IPluginBuilder plugin) => plugin.AddTriggerSource<ChatSource>("test-chat");
    }

    private sealed class ChatSource : ITriggerSource, IReplyChannel
    {
        public static ChatSource? Started;
        private TriggerSourceContext? _context;
        public List<(ReplyAddress To, string Text)> Sent { get; } = [];

        public string Type => "test-chat";
        public string Channel => "test-chat";

        public Task StartAsync(TriggerSourceContext context, CancellationToken cancellationToken)
        {
            _context = context;
            Started = this;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task ReceiveAsync(string sender, string text) =>
            await _context!.EmitAsync(new TriggerEvent(Guid.NewGuid().ToString("N"), _context.TriggerId, DateTimeOffset.UtcNow, sender, text, [],
                new JsonObject(), new ReplyAddress(Channel, sender)), CancellationToken.None);

        public Task SendAsync(ReplyAddress to, string text, IReadOnlyList<string> files, CancellationToken cancellationToken)
        {
            lock (Sent) Sent.Add((to, text));
            return Task.CompletedTask;
        }
    }
}
