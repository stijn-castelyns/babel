using System.CommandLine;
using Harness.Client;
using Spectre.Console;

namespace Harness.Cli.Commands;

/// <summary>
/// <c>harness chat</c>: line-mode chat with an agent. Locally the current directory is the workspace; remote clients pick a
/// named workspace. Ctrl-C cancels the running turn; a second Ctrl-C within two seconds quits.
/// </summary>
internal static class ChatCommand
{
    private static readonly Argument<string[]> Message = new("message") { Description = "Send this message and exit after the reply", Arity = ArgumentArity.ZeroOrMore };
    private static readonly Option<string?> Agent = new("--agent", "-a") { Description = "Agent definition to use" };
    private static readonly Option<string?> Workspace = new("--workspace", "-w") { Description = "Named workspace (required with --remote)" };
    private static readonly Option<string?> Resume = new("--resume", "-r") { Description = "Resume a session by id" };
    private static readonly Option<bool> Continue = new("--continue", "-c") { Description = "Resume the most recent session in this workspace" };
    private static readonly Option<string?> Model = new("--model", "-m") { Description = "Model profile override for a new session" };

    public static Command Create()
    {
        Command chat = new("chat", "Chat with an agent in this directory (or a named workspace).") { Message, Agent, Workspace, Resume, Continue, Model };
        chat.SetAction((p, ct) => RunAsync(p, p.GetValue(Message), ct));
        return chat;
    }

    public static Task<int> RunAsync(ParseResult p, string[]? message, CancellationToken ct) => HarnessCli.Guard(async () =>
    {
        using HarnessClient client = CliContext.Connect(p);
        SessionDto session = await OpenSessionAsync(client, p, ct);
        bool fancy = Output.Fancy(p);
        if (fancy) AnsiConsole.MarkupLine($"[grey]{Markup.Escape($"{session.Id} · {session.Agent} · {session.Model} · {session.Workspace}")}[/]");
        else Console.WriteLine($"session {session.Id} · {session.Agent} · {session.Model} · {session.Workspace}");

        RunRenderer renderer = new(client, p);
        if (message is { Length: > 0 })
        {
            EventDto? done = await SendAsync(client, renderer, session.Id, string.Join(' ', message), ct);
            return done?.Data["state"]?.GetValue<string>() == "succeeded" ? 0 : 1;
        }

        if (!fancy) Console.WriteLine("Type a message and press Enter. /exit quits, /new starts a new session, /id shows the session id.");
        DateTime lastInterrupt = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (fancy) AnsiConsole.Markup("[bold blue]›[/] ");
            else Console.Write("> ");
            string? line = Console.ReadLine();
            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;
            switch (line)
            {
                case "/exit" or "/quit": return 0;
                case "/id": Console.WriteLine(session.Id); continue;
                case "/new":
                    session = await client.CreateSessionAsync(new CreateSessionRequest(session.Agent, session.WorkspaceName is null ? session.Workspace : null, session.WorkspaceName), ct);
                    Console.WriteLine($"new session {session.Id}");
                    continue;
            }
            if ((DateTime.UtcNow - lastInterrupt).TotalSeconds < 2) return 130;
            EventDto? finished = await SendAsync(client, renderer, session.Id, line, ct);
            if (finished?.Data["state"]?.GetValue<string>() == "cancelled") lastInterrupt = DateTime.UtcNow;
        }
        return 0;
    });

    private static async Task<SessionDto> OpenSessionAsync(HarnessClient client, ParseResult p, CancellationToken ct)
    {
        if (p.GetValue(Resume) is { } id) return await client.SessionAsync(id, ct);
        string? workspaceName = p.GetValue(Workspace);
        if (workspaceName is null && CliContext.IsRemote(p))
            throw new CliException("A remote daemon cannot see this directory; pick a named workspace with --workspace.");
        string? workspacePath = workspaceName is null ? Directory.GetCurrentDirectory() : null;

        if (p.GetValue(Continue))
        {
            IReadOnlyList<SessionDto> recent = await client.SessionsAsync(workspace: workspaceName, limit: 50, ct: ct);
            SessionDto? last = recent.FirstOrDefault(s => workspaceName is not null || s.Workspace == workspacePath);
            if (last is not null) return last;
        }
        return await client.CreateSessionAsync(new CreateSessionRequest(p.GetValue(Agent), workspacePath, workspaceName, Model: p.GetValue(Model)), ct);
    }

    private static async Task<EventDto?> SendAsync(HarnessClient client, RunRenderer renderer, string sessionId, string text, CancellationToken ct)
    {
        SendMessageResponse sent = await client.SendAsync(sessionId, text, ct);
        // Ctrl-C cancels the running turn instead of killing the client.
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            _ = client.CancelAsync(sent.RunId, CancellationToken.None);
        };
        Console.CancelKeyPress += onCancel;
        try { return await renderer.FollowAsync(sent.RunId, ct); }
        finally { Console.CancelKeyPress -= onCancel; }
    }
}
