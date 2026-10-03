using System.CommandLine;
using Harness.Client;

namespace Harness.Cli.Commands;

internal static class SessionCommands
{
    public static Command Create()
    {
        Command sessions = new("sessions", "List, inspect, search, fork, export and delete sessions.");

        Option<string?> query = new("--query", "-q") { Description = "Full-text search over titles and messages" };
        Option<string?> workspace = new("--workspace", "-w") { Description = "Only sessions in this named workspace" };
        Option<int> limit = new("--limit", "-n") { Description = "Maximum rows", DefaultValueFactory = _ => 30 };
        Command ls = new("ls", "List sessions, most recent first.") { query, workspace, limit };
        ls.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            IReadOnlyList<SessionDto> rows = await c.SessionsAsync(p.GetValue(query), p.GetValue(workspace), p.GetValue(limit), ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(rows); return 0; }
            Output.Table(p, ["SESSION", "TITLE", "AGENT", "WORKSPACE", "MSGS", "TOKENS", "ACTIVE"], rows.Select(s => new[]
            {
                s.Id, Output.Short(s.Title ?? "", 40), s.Agent, Output.Short(s.WorkspaceName ?? s.Workspace, 30), s.MessageCount.ToString(),
                Output.Tokens(s.InputTokens + s.OutputTokens), Output.Ago(s.UpdatedAt),
            }));
            return 0;
        }));
        sessions.Subcommands.Add(ls);

        Argument<string> id = new("id") { Description = "Session id" };
        Command show = new("show", "Show a session and its recent transcript.") { id };
        show.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            SessionDto s = await c.SessionAsync(p.GetValue(id)!, ct);
            MessagesPage page = await c.MessagesAsync(s.Id, null, 20, ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(new { session = s, page.Messages }); return 0; }
            Console.WriteLine($"{s.Id} · {s.Title} · {s.Agent} · {s.Model}\n{s.Workspace} · {s.MessageCount} messages · {Output.Tokens(s.InputTokens)} in / {Output.Tokens(s.OutputTokens)} out");
            if (s.ParentId is not null) Console.WriteLine($"forked from {s.ParentId}");
            Console.WriteLine();
            foreach (MessageDto m in page.Messages)
                foreach (ContentDto content in m.Contents)
                    Console.WriteLine(content.Type switch
                    {
                        "text" => $"#{m.Seq} {m.Role}: {content.Text}",
                        "tool_call" => $"#{m.Seq}   ▸ {content.ToolName} {Output.Short(content.Arguments?.ToJsonString(), 100)}",
                        "tool_result" => $"#{m.Seq}     {Output.Short(content.Result?.Split('\n')[0], 100)}",
                        _ => $"#{m.Seq}   [{content.Type}]",
                    });
            return 0;
        }));
        sessions.Subcommands.Add(show);

        Option<long?> at = new("--at") { Description = "Message sequence number to fork at (default: the end)" };
        Command fork = new("fork", "Fork a session into a new one.") { id, at };
        fork.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            SessionDto s = await c.ForkAsync(p.GetValue(id)!, new ForkRequest(p.GetValue(at)), ct);
            Console.WriteLine(s.Id);
            return 0;
        }));
        sessions.Subcommands.Add(fork);

        Option<string> format = new("--format") { Description = "md or json", DefaultValueFactory = _ => "md" };
        Command export = new("export", "Export a session transcript.") { id, format };
        export.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            Console.Write(await c.ExportAsync(p.GetValue(id)!, p.GetValue(format)!, ct));
            return 0;
        }));
        sessions.Subcommands.Add(export);

        Argument<string[]> title = new("title") { Arity = ArgumentArity.OneOrMore, Description = "New title" };
        Command rename = new("rename", "Rename a session.") { id, title };
        rename.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            SessionDto s = await c.RenameSessionAsync(p.GetValue(id)!, string.Join(' ', p.GetValue(title)!), ct);
            if (p.GetValue(CliContext.Json)) Output.Json(s);
            else Console.WriteLine(s.Title);
            return 0;
        }));
        sessions.Subcommands.Add(rename);

        Command delete = new("delete", "Delete a session and its files.") { id };
        delete.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            await c.DeleteSessionAsync(p.GetValue(id)!, ct);
            Console.WriteLine("deleted");
            return 0;
        }));
        sessions.Subcommands.Add(delete);

        Command reindex = new("reindex", "Rebuild the session index from the session folders.");
        reindex.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            Console.WriteLine($"{await c.ReindexAsync(ct)} sessions indexed");
            return 0;
        }));
        sessions.Subcommands.Add(reindex);

        Option<bool> dryRun = new("--dry-run") { Description = "List what would be removed without removing it" };
        Command prune = new("prune", "Apply the retention policies now: old sessions, run folders and trigger events.") { dryRun };
        prune.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            PruneReportDto r = await c.PruneAsync(p.GetValue(dryRun), ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(r); return 0; }
            string verb = r.DryRun ? "would remove" : "removed";
            foreach (string s in r.Sessions) Console.WriteLine($"session {s}");
            foreach (string run in r.RunFolders) Console.WriteLine($"run folder {run}");
            Console.WriteLine($"{verb} {r.Sessions.Count} session(s), {r.RunFolders.Count} run folder(s){(r.DryRun ? "" : $" and {r.Events} trigger event(s)")}");
            return 0;
        }));
        sessions.Subcommands.Add(prune);

        Argument<string[]> words = new("text") { Arity = ArgumentArity.OneOrMore };
        Command search = new("search", "Search session titles and messages.") { words };
        search.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            IReadOnlyList<SessionDto> rows = await c.SessionsAsync(string.Join(' ', p.GetValue(words)!), ct: ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(rows); return 0; }
            Output.Table(p, ["SESSION", "TITLE", "WORKSPACE", "ACTIVE"], rows.Select(s => new[] { s.Id, Output.Short(s.Title ?? "", 50), Output.Short(s.WorkspaceName ?? s.Workspace, 30), Output.Ago(s.UpdatedAt) }));
            return 0;
        }));
        sessions.Subcommands.Add(search);
        return sessions;
    }
}
