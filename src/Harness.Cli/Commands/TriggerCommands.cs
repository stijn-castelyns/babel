using System.CommandLine;
using System.Text.Json.Nodes;
using Harness.Client;

namespace Harness.Cli.Commands;

internal static class TriggerCommands
{
    public static Command Create()
    {
        Command triggers = new("triggers", "Trigger status, next fire times and manual fire.");

        Command ls = new("ls", "List triggers.");
        ls.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            IReadOnlyList<TriggerDto> rows = await c.TriggersAsync(ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(rows); return 0; }
            Output.Table(p, ["TRIGGER", "SOURCE", "ENABLED", "NEXT", "LAST RUN", "LAST STATE", "BUDGET TODAY"], rows.Select(t => new[]
            {
                t.Id, t.SourceType, t.Enabled ? "yes" : "no", t.NextFireAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-", t.LastRunId ?? "-", t.LastState ?? "-",
                t.DailyTokens is long budget ? $"{t.TokensToday ?? 0:N0} / {budget:N0}" : "-",
            }));
            return 0;
        }));
        triggers.Subcommands.Add(ls);

        Command next = new("next", "Show the next scheduled fire times.");
        next.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            foreach (TriggerDto t in (await c.TriggersAsync(ct)).Where(t => t.NextFireAt is not null).OrderBy(t => t.NextFireAt))
                Console.WriteLine($"{t.NextFireAt!.Value.ToLocalTime():yyyy-MM-dd HH:mm}  {t.Id}");
            return 0;
        }));
        triggers.Subcommands.Add(next);

        Argument<string> id = new("id") { Description = "Trigger id" };
        Option<string?> text = new("--text") { Description = "Event text passed to the prompt as {event.text}" };
        Option<string[]> input = new("--input", "-i") { Description = "Input as name=value; repeatable", AllowMultipleArgumentsPerToken = false };
        Option<bool> follow = new("--follow", "-f") { Description = "Follow the run after firing" };
        Command fire = new("fire", "Fire a trigger now.") { id, text, input, follow };
        fire.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            JsonObject inputs = [];
            foreach (string pair in p.GetValue(input) ?? [])
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new CliException($"--input '{pair}' must be name=value.");
                inputs[pair[..eq]] = pair[(eq + 1)..];
            }
            SendMessageResponse run = await c.FireTriggerAsync(p.GetValue(id)!, new FireTriggerRequest(p.GetValue(text), inputs), ct);
            Console.WriteLine($"{run.RunId} (session {run.SessionId})");
            if (p.GetValue(follow)) await new RunRenderer(c, p).FollowAsync(run.RunId, ct);
            return 0;
        }));
        triggers.Subcommands.Add(fire);

        foreach ((string name, bool enabled) in new[] { ("enable", true), ("disable", false) })
        {
            Command toggle = new(name, $"{(enabled ? "Enable" : "Disable")} a trigger.") { id };
            toggle.SetAction((p, ct) => HarnessCli.Guard(async () =>
            {
                using HarnessClient c = CliContext.Connect(p);
                await c.SetTriggerEnabledAsync(p.GetValue(id)!, enabled, ct);
                Console.WriteLine(enabled ? "enabled" : "disabled");
                return 0;
            }));
            triggers.Subcommands.Add(toggle);
        }
        return triggers;
    }
}
