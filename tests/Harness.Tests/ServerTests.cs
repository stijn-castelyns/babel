using Harness.Client;
using Harness.Core.Agents;
using Harness.Server;
using Harness.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

public class ServerTests
{
    private static int FreePort()
    {
        using System.Net.Sockets.TcpListener l = new(System.Net.IPAddress.Loopback, 0);
        l.Start();
        return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Api_over_the_local_socket_streams_and_resumes_run_events()
    {
        if (OperatingSystem.IsWindows()) return;
        await using TestHome home = new();
        File.WriteAllText(Path.Combine(home.Workspace, "a.txt"), "hello");
        ScriptedChatClient model = new((m, _) => ScriptedChatClient.LastResults(m).Count == 0
            ? ScriptedChatClient.Call("read", new() { ["path"] = "a.txt" })
            : ScriptedChatClient.Text("it says hello"));

        await using WebApplication app = HarnessServer.Build(home.Paths);
        app.Services.GetRequiredService<AgentFactory>().ChatClientOverride = _ => model;
        await app.StartAsync();
        try
        {
            using HarnessClient client = HarnessClient.ForSocket(home.Paths.Socket);
            Assert.Equal(0, (await client.StatusAsync()).ActiveRuns);

            SessionDto session = await client.CreateSessionAsync(new CreateSessionRequest(Workspace: home.Workspace));
            SendMessageResponse sent = await client.SendAsync(session.Id, "what is in a.txt?");

            List<EventDto> events = [];
            await foreach (EventDto e in client.RunEventsAsync(sent.RunId)) events.Add(e);
            Assert.Equal("RUN_FINISHED", events[^1].Type);
            Assert.Equal("succeeded", events[^1].Data["state"]!.GetValue<string>());
            Assert.Contains(events, e => e.Type == "TEXT_MESSAGE_END" && e.Data["text"]!.GetValue<string>() == "it says hello");

            // Resuming after an event replays only what came later, from the journal.
            EventDto toolStart = events.First(e => e.Type == "TOOL_CALL_START");
            List<EventDto> resumed = [];
            await foreach (EventDto e in client.RunEventsAsync(sent.RunId, toolStart.Seq)) resumed.Add(e);
            Assert.All(resumed, e => Assert.True(e.Seq > toolStart.Seq));
            Assert.Equal("TOOL_CALL_END", resumed[0].Type);
            Assert.DoesNotContain(resumed, e => e.Type == "TEXT_MESSAGE_CONTENT");   // live-only events are not replayed

            MessagesPage page = await client.MessagesAsync(session.Id);
            Assert.Equal(["user", "assistant", "tool", "assistant"], page.Messages.Select(m => m.Role));
            Assert.Contains(page.Messages[1].Contents, c => c.Type == "tool_call" && c.ToolName == "read");

            Assert.Contains(await client.RunsAsync(), r => r.Id == sent.RunId && r.State == "succeeded");
            Assert.Contains("it says hello", await client.ExportAsync(session.Id, "md"));

            SessionDto fork = await client.ForkAsync(session.Id, new ForkRequest(2));
            Assert.Equal(2, fork.MessageCount);
            Assert.Equal(session.Id, fork.ParentId);

            HarnessApiException missing = await Assert.ThrowsAsync<HarnessApiException>(() => client.SessionAsync("s_nope"));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.Status);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Api_listener_requires_a_token_and_named_workspaces()
    {
        await using TestHome home = new();
        int port = FreePort();
        new Harness.Core.SecretStore(home.Paths).Set("api-token", "test-token-123");
        File.AppendAllText(home.Paths.ConfigFile, $"\nlisteners:\n  api: http://127.0.0.1:{port}\n  apiToken: secret:api-token\n");
        await using WebApplication app = HarnessServer.Build(home.Paths);
        await app.StartAsync();
        try
        {
            using HarnessClient anonymous = HarnessClient.ForUrl(new Uri($"http://127.0.0.1:{port}/"), null);
            HarnessApiException denied = await Assert.ThrowsAsync<HarnessApiException>(() => anonymous.StatusAsync());
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.Status);

            using HarnessClient remote = HarnessClient.ForUrl(new Uri($"http://127.0.0.1:{port}/"), "test-token-123");
            Assert.NotNull(await remote.StatusAsync());
            HarnessApiException rawPath = await Assert.ThrowsAsync<HarnessApiException>(() => remote.CreateSessionAsync(new CreateSessionRequest(Workspace: "/")));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, rawPath.Status);

            new Harness.Core.Config.ConfigCatalog(home.Paths).SetWorkspace("ws", home.Workspace);
            SessionDto session = await remote.CreateSessionAsync(new CreateSessionRequest(WorkspaceName: "ws"));
            Assert.Equal(home.Workspace, session.Workspace);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
