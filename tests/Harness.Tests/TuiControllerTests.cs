using Harness.Client;
using Harness.Core.Agents;
using Harness.Server;
using Harness.Tests.TestSupport;
using Harness.Tui;
using Harness.Tui.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>The TUI controller against a real daemon on its Unix socket: everything but the drawing.</summary>
public class TuiControllerTests
{
    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    [Fact]
    public async Task Sends_follows_runs_and_answers_approvals_through_the_api()
    {
        if (OperatingSystem.IsWindows()) return;
        await using TestHome home = new();
        ScriptedChatClient model = new((m, _) =>
            m[^1].Text == "and then this" ? ScriptedChatClient.Text("noted")
            : ScriptedChatClient.LastResults(m).Count == 0 ? ScriptedChatClient.Call("shell", new() { ["command"] = "echo tui" })
            : ScriptedChatClient.Text("ran it"));
        await using WebApplication app = HarnessServer.Build(home.Paths);
        app.Services.GetRequiredService<AgentFactory>().ChatClientOverride = _ => model;
        await app.StartAsync();
        try
        {
            using HarnessClient client = HarnessClient.ForSocket(home.Paths.Socket);
            Lock gate = new();
            TuiOptions options = new() { NewSession = new CreateSessionRequest(Workspace: home.Workspace), ExportDirectory = home.Root };
            await using TuiController tui = new(client, options, action => { lock (gate) action(); });
            await tui.StartAsync();
            Assert.Null(tui.OpenSessionId);
            Assert.True(tui.Connected);

            // The first message creates the session in the current folder, then the run is followed from its journal.
            await tui.SubmitAsync("run something");
            Assert.NotNull(tui.OpenSessionId);
            await Until(() => tui.Transcript?.PendingApprovals.Any() == true, "the approval card");
            await Until(() => tui.Store.ApprovalsWaiting() == 1, "the inbox to see the approval from the firehose");
            Assert.Equal("awaiting_approval", tui.ActiveRun?.State);

            // A message typed while the turn runs is queued, not sent.
            await tui.SubmitAsync("and then this");
            Assert.Single(tui.Composer.Queued);

            TranscriptItem card = tui.Transcript!.PendingApprovals.Single();
            Assert.Equal("echo tui", card.Summary);
            await tui.DecideAsync(card.RunId!, card.RequestId!, approved: true);
            await Until(() => tui.Transcript!.Items.Count(i => i.Kind == ItemKind.RunEnd) == 2, "the first run and the queued one to finish");
            Assert.Empty(tui.Composer.Queued);
            Assert.Contains(tui.Transcript!.Items, i => i.Kind == ItemKind.Tool && i.ToolName == "shell" && i.Ok == true && i.Result!.StartsWith("exit 0"));
            Assert.Contains(tui.Transcript!.Items, i => i.Kind == ItemKind.User && i.Text.ToString() == "and then this");
            await Until(() => tui.Store.Approvals.Count == 0 && tui.ActiveRun is null, "the store to settle");

            // Reopening loads the history; renaming and exporting go through the API.
            string sessionId = tui.OpenSessionId!;
            await tui.OpenSessionAsync(sessionId);
            Assert.Contains(tui.Transcript!.Items, i => i.Kind == ItemKind.Tool && i.ToolName == "shell");
            await tui.RenameAsync(sessionId, "Renamed in the TUI");
            Assert.Equal("Renamed in the TUI", (await client.SessionAsync(sessionId)).Title);
            await tui.ExportAsync(sessionId);
            Assert.Contains("ran it", File.ReadAllText(Path.Combine(home.Root, sessionId + ".md")));

            // Slash commands: /new starts an empty session that is created on the next message.
            Assert.Null(await tui.SubmitAsync("/new"));
            Assert.Null(tui.OpenSessionId);
            Assert.Equal("help", await tui.SubmitAsync("/help"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Session_file_completion_matches_paths_in_the_workspace()
    {
        if (OperatingSystem.IsWindows()) return;
        await using TestHome home = new();
        Directory.CreateDirectory(Path.Combine(home.Workspace, "src"));
        File.WriteAllText(Path.Combine(home.Workspace, "src", "Program.cs"), "");
        File.WriteAllText(Path.Combine(home.Workspace, "README.md"), "");
        await using WebApplication app = HarnessServer.Build(home.Paths);
        await app.StartAsync();
        try
        {
            using HarnessClient client = HarnessClient.ForSocket(home.Paths.Socket);
            SessionDto s = await client.CreateSessionAsync(new CreateSessionRequest(Workspace: home.Workspace));
            Assert.Equal("src/Program.cs", (await client.SessionFilesAsync(s.Id, "prog"))[0]);
            HarnessApiException missing = await Assert.ThrowsAsync<HarnessApiException>(() => client.RenameSessionAsync(s.Id, "  "));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, missing.Status);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
