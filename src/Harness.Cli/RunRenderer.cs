using System.CommandLine;
using System.Text.Json.Nodes;
using Harness.Client;
using Spectre.Console;

namespace Harness.Cli;

/// <summary>
/// Streams one run as a transcript: assistant text as it arrives, tool calls folded to one line, and approval prompts
/// answered inline with y / n / a (approve this exact call for the rest of the session).
/// </summary>
internal sealed class RunRenderer(HarnessClient client, ParseResult parse)
{
    private readonly bool _fancy = Output.Fancy(parse);
    private bool _midLine;

    public async Task<EventDto?> FollowAsync(string runId, CancellationToken ct)
    {
        EventDto? finished = null;
        await foreach (EventDto evt in client.RunEventsAsync(runId, null, ct))
        {
            await RenderAsync(evt, ct);
            if (evt.Type == "RUN_FINISHED") finished = evt;
        }
        EndLine();
        return finished;
    }

    private async Task RenderAsync(EventDto evt, CancellationToken ct)
    {
        JsonObject d = evt.Data;
        switch (evt.Type)
        {
            case "TEXT_MESSAGE_CONTENT":
                Console.Write(d["delta"]?.GetValue<string>());
                _midLine = true;
                break;
            case "TEXT_MESSAGE_END":
                EndLine();
                break;
            case "TOOL_CALL_START":
                EndLine();
                string tool = d["toolName"]?.GetValue<string>() ?? "?";
                string arg = MainArgument(d["arguments"] as JsonObject);
                Line($"▸ {tool} {Output.Short(arg, 100)}", "grey");
                break;
            case "TOOL_CALL_RESULT":
                string first = (d["content"]?.GetValue<string>() ?? "").Split('\n')[0];
                Line($"  {Output.Short(first, 120)}", first.StartsWith("Error", StringComparison.Ordinal) || first.StartsWith("Blocked", StringComparison.Ordinal) ? "red" : "grey");
                break;
            case "APPROVAL_REQUESTED":
                EndLine();
                await AskAsync(evt, d, ct);
                break;
            case "APPROVAL_RESOLVED":
                if (d["via"]?.GetValue<string>() is "policy" or "hook")
                    Line($"  {(d["approved"]?.GetValue<bool>() == true ? "auto-approved" : "auto-denied")}: {d["reason"]?.GetValue<string>()}", "grey");
                break;
            case "RUN_STATE" when d["notice"] is JsonNode notice:
                Line($"! {notice.GetValue<string>()}", "yellow");
                break;
            case "RUN_ERROR":
                EndLine();
                Line($"error: {d["message"]?.GetValue<string>()}", "red");
                break;
            case "RUN_FINISHED":
                EndLine();
                string state = d["state"]?.GetValue<string>() ?? "?";
                long input = d["inputTokens"]?.GetValue<long>() ?? 0, output = d["outputTokens"]?.GetValue<long>() ?? 0;
                string error = d["error"]?.GetValue<string>() is { } e && state != "succeeded" ? $" · {e}" : "";
                Line($"── {state} · {Output.Tokens(input)} in / {Output.Tokens(output)} out{error}", state == "succeeded" ? "grey" : "red");
                break;
        }
    }

    private async Task AskAsync(EventDto evt, JsonObject d, CancellationToken ct)
    {
        string tool = d["toolName"]?.GetValue<string>() ?? "?";
        string detail = d["summary"]?.GetValue<string>() ?? d["arguments"]?.ToJsonString() ?? "";
        Line($"! {tool} wants to run:", "yellow");
        Console.WriteLine("    " + detail.Replace("\n", "\n    "));
        if (Console.IsInputRedirected)
        {
            Line("  (no terminal to answer; approve with 'harness approve')", "yellow");
            return;
        }
        // The daemon may resolve the approval first (a hook, another client); answering late is harmless.
        Console.Write("  approve? [y]es / [n]o / [a]lways for this session: ");
        string answer = (await Task.Run(Console.ReadLine, ct) ?? "").Trim().ToLowerInvariant();
        bool approved = answer is "y" or "yes" or "a" or "always";
        string? reason = null;
        if (!approved && answer.Length > 1 && answer is not ("n" or "no")) reason = answer;
        try
        {
            await client.DecideAsync(evt.RunId!, d["requestId"]!.GetValue<string>(), new ApprovalDecisionRequest(approved, reason, answer is "a" or "always", Environment.UserName), ct);
        }
        catch (HarnessApiException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict)
        {
            Line("  (already answered elsewhere)", "grey");
        }
    }

    private static string MainArgument(JsonObject? args)
    {
        if (args is null) return "";
        foreach (string key in new[] { "command", "path", "pattern", "handle" })
            if (args[key] is JsonNode v) return v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
        return args.ToJsonString();
    }

    private void EndLine()
    {
        if (_midLine) Console.WriteLine();
        _midLine = false;
    }

    private void Line(string text, string colour)
    {
        if (_fancy) AnsiConsole.MarkupLine($"[{colour}]{Markup.Escape(text)}[/]");
        else Console.WriteLine(text);
    }
}
