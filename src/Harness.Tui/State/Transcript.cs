using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Client;

namespace Harness.Tui.State;

public enum ItemKind { User, Assistant, Tool, Approval, Notice, RunEnd }

public enum ApprovalStatus { Pending, Approved, Denied }

public enum NoticeLevel { Info, Warning, Error }

/// <summary>One entry of a transcript. Mutable: streamed text, tool results and approval answers update it in place.</summary>
public sealed class TranscriptItem
{
    public required ItemKind Kind { get; init; }
    public string? RunId { get; init; }
    /// <summary>History or event sequence number this item started at (0 when unknown).</summary>
    public long Seq { get; set; }
    /// <summary>Bumped on every change, so renderers can cache per item.</summary>
    public int Version { get; private set; }

    public string? Label { get; set; }
    public StringBuilder Text { get; } = new();
    public string? MessageId { get; init; }
    public bool Streaming { get; set; }

    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public JsonObject? Arguments { get; init; }
    public string? Result { get; set; }
    public bool? Ok { get; set; }
    public bool Expanded { get; set; }

    public string? RequestId { get; init; }
    public string? Summary { get; init; }
    public ApprovalStatus Status { get; set; }
    public string? DecidedBy { get; set; }
    public string? Reason { get; set; }

    public NoticeLevel Level { get; init; }
    public string? State { get; init; }

    public void Touch() => Version++;

    /// <summary>The argument that best describes a tool call: the command for shell, the path for file tools.</summary>
    public string MainArgument => MainArgumentOf(Arguments);

    public static string MainArgumentOf(JsonObject? args)
    {
        if (args is null) return "";
        foreach (string key in new[] { "command", "path", "pattern", "handle", "query", "name" })
            if (args[key] is JsonNode v) return v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
        return args.Count == 0 ? "" : args.ToJsonString();
    }
}

/// <summary>
/// The transcript of one session, built from the paged history and from run events (replayed, then live). Pure state:
/// no terminal and no network, so it is tested by feeding recorded event streams.
/// </summary>
public sealed class TranscriptModel(string sessionId)
{
    private readonly HashSet<long> _seen = [];
    private readonly HashSet<string> _streamedRuns = [];

    public string SessionId { get; } = sessionId;
    public List<TranscriptItem> Items { get; } = [];
    public bool HasMoreBefore { get; private set; }
    /// <summary>The lowest history sequence number loaded, for the next <c>before=</c> page.</summary>
    public long? OldestSeq { get; private set; }
    /// <summary>Bumped on every change; views redraw when it moves.</summary>
    public int Version { get; private set; }
    /// <summary>Input tokens of the latest model call: how full the context is.</summary>
    public long LastInputTokens { get; private set; }

    public IEnumerable<TranscriptItem> PendingApprovals => Items.Where(i => i.Kind == ItemKind.Approval && i.Status == ApprovalStatus.Pending);

    private void Changed() => Version++;

    /// <summary>
    /// Replaces the transcript with a history page. Messages of runs whose events will be replayed (active runs) are left
    /// out, because the replay rebuilds them with their tool results and approvals.
    /// </summary>
    public void LoadHistory(MessagesPage page, IReadOnlySet<string>? replayedRuns = null)
    {
        Items.Clear();
        _seen.Clear();
        _streamedRuns.Clear();
        Items.AddRange(FromHistory(page.Messages, replayedRuns));
        HasMoreBefore = page.HasMore;
        OldestSeq = page.Messages.Count > 0 ? page.Messages[0].Seq : null;
        Changed();
    }

    /// <summary>Adds an older history page in front. Returns how many items were added, so a view can keep its place.</summary>
    public int PrependHistory(MessagesPage page)
    {
        List<TranscriptItem> older = FromHistory(page.Messages, null);
        Items.InsertRange(0, older);
        HasMoreBefore = page.HasMore;
        if (page.Messages.Count > 0) OldestSeq = page.Messages[0].Seq;
        Changed();
        return older.Count;
    }

    private static List<TranscriptItem> FromHistory(IReadOnlyList<MessageDto> messages, IReadOnlySet<string>? skipRuns)
    {
        List<TranscriptItem> items = [];
        Dictionary<string, TranscriptItem> calls = [];
        foreach (MessageDto m in messages)
        {
            if (m.RunId is not null && skipRuns?.Contains(m.RunId) == true) continue;
            switch (m.Role)
            {
                case "user":
                    if (m.Text.Length > 0) items.Add(Make(ItemKind.User, m.RunId, m.Seq, m.Text, label: "you"));
                    break;
                case "assistant":
                    foreach (ContentDto c in m.Contents)
                    {
                        if (c.Type == "text" && !string.IsNullOrEmpty(c.Text)) items.Add(Make(ItemKind.Assistant, m.RunId, m.Seq, c.Text));
                        else if (c.Type == "tool_call")
                        {
                            TranscriptItem call = new()
                            {
                                Kind = ItemKind.Tool, RunId = m.RunId, Seq = m.Seq, ToolCallId = c.CallId, ToolName = c.ToolName,
                                Arguments = c.Arguments as JsonObject,
                            };
                            items.Add(call);
                            if (c.CallId is not null) calls[c.CallId] = call;
                        }
                    }
                    break;
                case "tool":
                    foreach (ContentDto c in m.Contents.Where(c => c.Type == "tool_result"))
                        if (c.CallId is not null && calls.TryGetValue(c.CallId, out TranscriptItem? call))
                        {
                            call.Result = c.Result;
                            call.Ok = !IsError(c.Result);
                        }
                    break;
            }
        }
        return items;
    }

    private static TranscriptItem Make(ItemKind kind, string? runId, long seq, string text, string? label = null)
    {
        TranscriptItem item = new() { Kind = kind, RunId = runId, Seq = seq, Label = label };
        item.Text.Append(text);
        return item;
    }

    internal static bool IsError(string? result) =>
        result is not null && (result.StartsWith("Error", StringComparison.Ordinal) || result.StartsWith("Blocked", StringComparison.Ordinal)
            || result.StartsWith("Denied", StringComparison.Ordinal));

    /// <summary>Drops whatever the transcript holds of a run, before its events are replayed from the start.</summary>
    public void BeginReplay(string runId)
    {
        if (!_streamedRuns.Add(runId)) return;
        Items.RemoveAll(i => i.RunId == runId);
        Changed();
    }

    /// <summary>Applies one run event. Persisted events are applied once (by sequence number), so a resumed stream may overlap.</summary>
    public bool Apply(EventDto e)
    {
        if (e.SessionId is not null && e.SessionId != SessionId) return false;
        if (e.Type != "TEXT_MESSAGE_CONTENT" && e.Seq > 0 && !_seen.Add(e.Seq)) return false;
        JsonObject d = e.Data;
        switch (e.Type)
        {
            case "RUN_STARTED":
                string input = Str(d, "input") ?? "";
                bool interactive = d["interactive"]?.GetValue<bool>() ?? true;
                if (input.Length > 0)
                    Items.Add(Make(ItemKind.User, e.RunId, e.Seq, input, interactive ? "you" : $"trigger {Str(d, "triggerId") ?? ""}".TrimEnd()));
                break;
            case "TEXT_MESSAGE_START":
                AssistantFor(e, Str(d, "messageId")).Streaming = true;
                break;
            case "TEXT_MESSAGE_CONTENT":
            {
                TranscriptItem item = AssistantFor(e, Str(d, "messageId"));
                if (!item.Streaming && item.Text.Length > 0) return false;   // already complete; a late delta
                item.Streaming = true;
                item.Text.Append(Str(d, "delta"));
                item.Touch();
                break;
            }
            case "TEXT_MESSAGE_END":
            {
                TranscriptItem item = AssistantFor(e, Str(d, "messageId"));
                item.Text.Clear().Append(Str(d, "text"));
                item.Streaming = false;
                item.Touch();
                if (item.Text.Length == 0) Items.Remove(item);
                break;
            }
            case "TOOL_CALL_START":
                Items.Add(new TranscriptItem
                {
                    Kind = ItemKind.Tool, RunId = e.RunId, Seq = e.Seq, ToolCallId = Str(d, "toolCallId"), ToolName = Str(d, "toolName"),
                    Arguments = d["arguments"]?.DeepClone() as JsonObject,
                });
                break;
            case "TOOL_CALL_END":
                if (ToolFor(Str(d, "toolCallId")) is { } ended)
                {
                    ended.Ok = d["ok"]?.GetValue<bool>() ?? true;
                    ended.Touch();
                }
                break;
            case "TOOL_CALL_RESULT":
                if (ToolFor(Str(d, "toolCallId")) is { } done)
                {
                    done.Result = Str(d, "content");
                    if (IsError(done.Result)) done.Ok = false;
                    done.Ok ??= true;
                    done.Touch();
                }
                break;
            case "APPROVAL_REQUESTED":
                Items.Add(new TranscriptItem
                {
                    Kind = ItemKind.Approval, RunId = e.RunId, Seq = e.Seq, RequestId = Str(d, "requestId"), ToolCallId = Str(d, "toolCallId"),
                    ToolName = Str(d, "toolName"), Arguments = d["arguments"]?.DeepClone() as JsonObject, Summary = Str(d, "summary"),
                });
                break;
            case "APPROVAL_RESOLVED":
                if (Items.LastOrDefault(i => i.Kind == ItemKind.Approval && i.RequestId == Str(d, "requestId")) is { } card)
                {
                    bool approved = d["approved"]?.GetValue<bool>() ?? false;
                    string via = Str(d, "via") ?? "";
                    // Calls the policy let through need no card; everything a person (or a hook, or a timeout) decided stays visible.
                    if (approved && via == "policy") Items.Remove(card);
                    else
                    {
                        card.Status = approved ? ApprovalStatus.Approved : ApprovalStatus.Denied;
                        card.DecidedBy = Str(d, "decidedBy");
                        card.Reason = Str(d, "reason");
                        card.Touch();
                    }
                }
                break;
            case "USAGE":
                LastInputTokens = Json.Long(d["inputTokens"]) ?? LastInputTokens;
                return true;
            case "RUN_STATE":
                if (Str(d, "notice") is { } notice) Items.Add(Notice(e, notice, NoticeLevel.Warning));
                else return false;
                break;
            case "RUN_ERROR":
                Items.Add(Notice(e, "error: " + (Str(d, "message") ?? "unknown"), NoticeLevel.Error));
                break;
            case "RUN_FINISHED":
                foreach (TranscriptItem open in Items.Where(i => i.RunId == e.RunId && i.Streaming)) open.Streaming = false;
                TranscriptItem end = new() { Kind = ItemKind.RunEnd, RunId = e.RunId, Seq = e.Seq, State = Str(d, "state") };
                long input2 = Json.Long(d["inputTokens"]) ?? 0, output = Json.Long(d["outputTokens"]) ?? 0;
                end.Text.Append($"{Format.Tokens(input2)} in / {Format.Tokens(output)} out");
                if (Json.Long(d["elapsedMs"]) is long ms) end.Text.Append($" · {Format.Duration(TimeSpan.FromMilliseconds(ms))}");
                if (end.State != "succeeded" && Str(d, "error") is { } error) end.Reason = error;
                Items.Add(end);
                break;
            case "WORKSPACE_STEP":
                Items.Add(Notice(e, $"workspace step {d["index"]} {Str(d, "type")}: {Str(d, "status")}" + (Str(d, "error") is { } err ? $" · {err}" : ""),
                    Str(d, "status") == "failed" ? NoticeLevel.Error : NoticeLevel.Info));
                break;
            case "OUTPUT_VALIDATED":
                bool valid = d["valid"]?.GetValue<bool>() ?? false;
                Items.Add(Notice(e, valid ? "output accepted" : "output invalid: " + Format.OneLine(d["errors"]?.ToJsonString() ?? "", 200), valid ? NoticeLevel.Info : NoticeLevel.Warning));
                break;
            case "OUTPUT_DELIVERED":
                Items.Add(Notice(e, $"delivered to {Str(d, "sink") ?? Str(d, "type") ?? "sink"}" + (Str(d, "error") is { } derr ? $" · failed: {derr}" : ""),
                    Str(d, "error") is null ? NoticeLevel.Info : NoticeLevel.Error));
                break;
            case "CHECKPOINT":
                Items.Add(Notice(e, $"summarised {d["messages"]} older messages into a checkpoint", NoticeLevel.Info));
                break;
            case "EGRESS_DENIED":
                Items.Add(Notice(e, $"network: blocked {Str(d, "host")}:{d["port"]}", NoticeLevel.Warning));
                break;
            default:
                return false;
        }
        Changed();
        return true;
    }

    private static TranscriptItem Notice(EventDto e, string text, NoticeLevel level)
    {
        TranscriptItem item = new() { Kind = ItemKind.Notice, RunId = e.RunId, Seq = e.Seq, Level = level };
        item.Text.Append(text);
        return item;
    }

    private TranscriptItem AssistantFor(EventDto e, string? messageId)
    {
        TranscriptItem? item = messageId is null ? null : Items.LastOrDefault(i => i.Kind == ItemKind.Assistant && i.MessageId == messageId);
        if (item is not null) return item;
        item = new TranscriptItem { Kind = ItemKind.Assistant, RunId = e.RunId, Seq = e.Seq, MessageId = messageId };
        Items.Add(item);
        return item;
    }

    private TranscriptItem? ToolFor(string? callId) =>
        callId is null ? null : Items.LastOrDefault(i => i.Kind == ItemKind.Tool && i.ToolCallId == callId);

    private static string? Str(JsonObject d, string key) =>
        d[key] is JsonNode n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;

    // ---- navigation helpers for the message cursor ----

    public int PreviousUserTurn(int from)
    {
        for (int i = Math.Min(from, Items.Count) - 1; i >= 0; i--) if (Items[i].Kind == ItemKind.User) return i;
        return from;
    }

    public int NextUserTurn(int from)
    {
        for (int i = from + 1; i < Items.Count; i++) if (Items[i].Kind == ItemKind.User) return i;
        return from;
    }

    /// <summary>The next item (wrapping) whose text, tool call or result contains <paramref name="query"/>, case-insensitively.</summary>
    public int? Find(string query, int from, bool forward)
    {
        if (query.Length == 0 || Items.Count == 0) return null;
        for (int step = 1; step <= Items.Count; step++)
        {
            int i = ((forward ? from + step : from - step) % Items.Count + Items.Count) % Items.Count;
            if (SearchText(Items[i]).Contains(query, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return null;
    }

    /// <summary>The text a search or a copy sees for an item.</summary>
    public static string SearchText(TranscriptItem i) => i.Kind switch
    {
        ItemKind.Tool => $"{i.ToolName} {i.MainArgument}\n{i.Result}",
        ItemKind.Approval => $"{i.ToolName} {i.Summary ?? i.Arguments?.ToJsonString()}",
        _ => i.Text.ToString(),
    };
}

/// <summary>Short human formats shared by the views.</summary>
public static class Format
{
    public static string Tokens(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M" : n >= 1_000 ? $"{n / 1000}k" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes:00}m" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m{d.Seconds:00}s" : $"{d.TotalSeconds:0.0}s";

    public static string Clock(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{(int)d.TotalMinutes:00}:{d.Seconds:00}";

    public static string Ago(DateTimeOffset at, DateTimeOffset now) => Brief(now - at, "now");

    /// <summary>A duration in one unit: 40s, 2m, 3h, 5d.</summary>
    public static string Brief(TimeSpan d, string? zero = null) =>
        d.TotalSeconds < 60 ? zero ?? $"{Math.Max(0, (int)d.TotalSeconds)}s" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m" : d.TotalHours < 48 ? $"{(int)d.TotalHours}h" : $"{(int)d.TotalDays}d";

    public static string OneLine(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string line = s.ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..Math.Max(0, max - 1)] + "…";
    }
}
