using System.CommandLine;
using System.Text.Json.Nodes;
using Harness.Client;
using Spectre.Console;

namespace Harness.Cli.Commands;

internal static class RunCommands
{
    private static readonly string[] Active = ["queued", "preparing", "running", "awaiting_approval", "validating", "delivering"];

    public static Command Create()
    {
        Command runs = new("runs", "Monitor and control runs.");

        Option<string?> state = new("--state") { Description = "Only runs in this state" };
        Option<string?> since = new("--since") { Description = "Only runs created in this period, for example 24h or 7d" };
        Option<int> limit = new("--limit", "-n") { Description = "Maximum rows", DefaultValueFactory = _ => 30 };
        Command ls = new("ls", "List runs, newest first.") { state, since, limit };
        ls.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            DateTimeOffset? from = p.GetValue(since) is { } s ? DateTimeOffset.UtcNow - Harness.Core.Durations.Parse(s) : null;
            IReadOnlyList<RunDto> rows = await c.RunsAsync(p.GetValue(state), from, null, p.GetValue(limit), ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(rows); return 0; }
            Output.Table(p, ["RUN", "TRIGGER", "AGENT", "STATE", "ELAPSED", "TOKENS", "LAST TOOL"], rows.Select(Row));
            return 0;
        }));
        runs.Subcommands.Add(ls);

        Argument<string> id = new("id") { Description = "Run id" };
        Command show = new("show", "Show a run's summary.") { id };
        show.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            RunDto r = await c.RunAsync(p.GetValue(id)!, ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(r); return 0; }
            Console.WriteLine($"{r.Id} · {r.State} · session {r.SessionId} · {r.Agent} · {r.Model}");
            Console.WriteLine($"created {r.CreatedAt:u} · elapsed {Output.Elapsed(r.StartedAt, r.FinishedAt)} · {Output.Tokens(r.InputTokens)} in / {Output.Tokens(r.OutputTokens)} out");
            if (r.TriggerId is not null) Console.WriteLine($"trigger {r.TriggerId}");
            if (r.Error is not null) Console.WriteLine($"error: {r.Error}");
            if (r.ResultText is not null) Console.WriteLine($"\n{r.ResultText}");
            return 0;
        }));
        runs.Subcommands.Add(show);

        Command logs = new("logs", "Print a run's full event log.") { id };
        logs.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            RunDto r = await c.RunAsync(p.GetValue(id)!, ct);
            bool live = Active.Contains(r.State);
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await foreach (EventDto e in c.RunEventsAsync(r.Id, null, stop.Token))
            {
                if (e.Type == "TEXT_MESSAGE_CONTENT") continue;
                if (p.GetValue(CliContext.Json)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(e, HarnessClient.Json));
                else Console.WriteLine($"{e.Seq,5} {e.Ts:HH:mm:ss} {e.Type,-20} {Output.Short(e.Data.ToJsonString(), 160)}");
                if (!live && e.Type == "RUN_FINISHED") break;
            }
            return 0;
        }));
        runs.Subcommands.Add(logs);

        Command output = new("output", "Print a run's final output.") { id };
        output.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            RunDto r = await c.RunAsync(p.GetValue(id)!, ct);
            Console.WriteLine(r.ResultText ?? r.Error ?? "");
            return 0;
        }));
        runs.Subcommands.Add(output);

        Command attach = new("attach", "Follow a run live and answer its approvals.") { id };
        attach.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            EventDto? done = await new RunRenderer(c, p).FollowAsync(p.GetValue(id)!, ct);
            return done?.Data["state"]?.GetValue<string>() == "succeeded" ? 0 : 1;
        }));
        runs.Subcommands.Add(attach);

        Command cancel = new("cancel", "Cancel a run.") { id };
        cancel.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            await c.CancelAsync(p.GetValue(id)!, ct);
            Console.WriteLine("cancelling");
            return 0;
        }));
        runs.Subcommands.Add(cancel);

        Command watch = new("watch", "Live table of active and recent runs.");
        watch.SetAction((p, ct) => HarnessCli.Guard(() => WatchAsync(p, ct)));
        runs.Subcommands.Add(watch);
        return runs;
    }

    public static IEnumerable<Command> ApprovalCommands()
    {
        Command approvals = new("approvals", "List pending approvals, oldest first.");
        approvals.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            IReadOnlyList<ApprovalDto> rows = await c.ApprovalsAsync(ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(rows); return 0; }
            Output.Table(p, ["RUN", "REQUEST", "TOOL", "WHAT", "WAITING"], rows.Select(a => new[]
            {
                a.RunId, a.RequestId, a.ToolName, Output.Short(a.Summary ?? a.Arguments.ToJsonString(), 70), Output.Ago(a.RequestedAt),
            }));
            return 0;
        }));
        yield return approvals;

        Argument<string> run = new("run") { Description = "Run id" };
        Argument<string> request = new("request") { Description = "Approval request id" };
        Option<string?> reason = new("--reason") { Description = "Reason, shown to the agent" };
        Option<bool> always = new("--always") { Description = "Approve this exact call for the rest of the session" };
        Command approve = new("approve", "Approve a pending tool call.") { run, request, reason, always };
        approve.SetAction((p, ct) => Decide(p, p.GetValue(run)!, p.GetValue(request)!, true, p.GetValue(reason), p.GetValue(always), ct));
        yield return approve;
        Command deny = new("deny", "Deny a pending tool call.") { run, request, reason };
        deny.SetAction((p, ct) => Decide(p, p.GetValue(run)!, p.GetValue(request)!, false, p.GetValue(reason), false, ct));
        yield return deny;
    }

    private static Task<int> Decide(ParseResult p, string run, string request, bool approved, string? reason, bool always, CancellationToken ct) => HarnessCli.Guard(async () =>
    {
        using HarnessClient c = CliContext.Connect(p);
        await c.DecideAsync(run, request, new ApprovalDecisionRequest(approved, reason, always, Environment.UserName), ct);
        Console.WriteLine(approved ? "approved" : "denied");
        return 0;
    });

    private static string[] Row(RunDto r) =>
    [
        r.Id, r.TriggerId ?? "manual", r.Agent, r.State, Output.Elapsed(r.StartedAt ?? r.CreatedAt, r.FinishedAt),
        Output.Tokens(r.InputTokens + r.OutputTokens), Output.Short(r.LastTool ?? "", 40),
    ];

    /// <summary>Redraws the table on run events (live view in a terminal, a line per change otherwise).</summary>
    private static async Task<int> WatchAsync(ParseResult p, CancellationToken ct)
    {
        using HarnessClient c = CliContext.Connect(p);
        Dictionary<string, RunDto> runs = (await c.RunsAsync(limit: 20, ct: ct)).ToDictionary(r => r.Id);
        bool fancy = Output.Fancy(p);

        Table Render()
        {
            Table t = new Table().Border(TableBorder.Simple);
            foreach (string h in new[] { "RUN", "TRIGGER", "AGENT", "STATE", "ELAPSED", "TOKENS", "LAST TOOL" }) t.AddColumn(h);
            foreach (RunDto r in runs.Values.OrderByDescending(r => Active.Contains(r.State)).ThenByDescending(r => r.CreatedAt).Take(25))
                t.AddRow([.. Row(r).Select(Markup.Escape)]);
            return t;
        }

        async Task Refresh(EventDto e)
        {
            if (e.RunId is null || e.Type is "TEXT_MESSAGE_CONTENT" or "TEXT_MESSAGE_START" or "TEXT_MESSAGE_END" or "TOOL_CALL_RESULT" or "TOOL_CALL_END") return;
            if (e.Type == "USAGE" && runs.TryGetValue(e.RunId, out RunDto? known))
            {
                runs[e.RunId] = known with
                {
                    InputTokens = e.Data["runInputTokens"]?.GetValue<long>() ?? known.InputTokens,
                    OutputTokens = e.Data["runOutputTokens"]?.GetValue<long>() ?? known.OutputTokens,
                };
                return;
            }
            runs[e.RunId] = await c.RunAsync(e.RunId, ct);
        }

        if (!fancy)
        {
            await foreach (EventDto e in c.EventsAsync(null, null, ct))
            {
                if (e.Type is not ("RUN_STARTED" or "RUN_STATE" or "RUN_FINISHED" or "TOOL_CALL_START")) continue;
                await Refresh(e);
                Console.WriteLine(string.Join("  ", Row(runs[e.RunId!])));
            }
            return 0;
        }

        await AnsiConsole.Live(Render()).StartAsync(async ctx =>
        {
            using PeriodicTimer tick = new(TimeSpan.FromSeconds(1));
            Task ticker = Task.Run(async () => { while (await tick.WaitForNextTickAsync(ct)) { ctx.UpdateTarget(Render()); } }, ct);
            await foreach (EventDto e in c.EventsAsync(null, null, ct))
            {
                await Refresh(e);
                ctx.UpdateTarget(Render());
            }
        });
        return 0;
    }
}
