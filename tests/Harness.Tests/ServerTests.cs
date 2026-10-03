using Harness.Client;
using Harness.Core.Agents;
using Harness.Server;
using Harness.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

public class ServerTests
{
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
            Assert.Contains(events, e => e.Type == "TEXT_MESSAGE_CONTENT");

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
}
