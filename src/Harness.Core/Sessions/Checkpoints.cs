using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Config;
using Harness.Sdk;
using Microsoft.Extensions.AI;

namespace Harness.Core.Sessions;

/// <summary>
/// Writes compaction checkpoints: when a session's history since its last checkpoint grows past the agent's threshold,
/// everything before the last few user turns is summarised by the model into a <c>checkpoints.jsonl</c> record.
/// <see cref="FileChatHistoryProvider"/> then sends the summary plus the messages after it; <c>history.jsonl</c> is never
/// rewritten, so the full conversation stays available for audit, export and forks.
/// </summary>
public static class Checkpoints
{
    public const int DefaultThresholdTokens = 32_000;
    private const int MaxResultChars = 1_500;
    private static readonly JsonSerializerOptions Compact = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    public const string Instructions = """
        You maintain the memory of a long-running coding agent session. Summarise the conversation below so the agent can
        continue the work without it. Keep: the user's goals and constraints, decisions made and why, files and commands that
        matter (with paths), the current state of the work, errors still open, and anything the user asked to remember. Drop
        pleasantries and superseded attempts. Write plain Markdown, at most about 600 words. If an earlier summary is included,
        fold it in.
        """;

    /// <summary>Rough token estimate of a message (about four characters per token of its JSON).</summary>
    public static long EstimateTokens(ChatMessage message) => JsonSerializer.Serialize(message, Compact).Length / 4;

    public static long Threshold(CompactionConfig config, ModelProfile model) =>
        config.SummarizeAfterTokens ?? (model.ContextWindow is int window and > 0 ? window / 2 : DefaultThresholdTokens);

    /// <summary>
    /// Writes a checkpoint when the history since the last one is over the threshold and there are enough turns to keep.
    /// Returns the checkpoint, or null when none was needed.
    /// </summary>
    public static async Task<CompactionCheckpoint?> MaybeWriteAsync(SessionFolder session, CompactionConfig config, ModelProfile model, IChatClient chat,
        Action<string, JsonObject> emit, CancellationToken ct)
    {
        long threshold = Threshold(config, model);
        if (threshold <= 0) return null;
        CompactionCheckpoint? previous = session.ReadCheckpoints().LastOrDefault();
        List<HistoryEntry> entries = [.. session.ReadHistory().Where(e => previous is null || e.Seq > previous.UpToSeq)];
        long estimate = entries.Sum(e => EstimateTokens(e.Message));
        if (estimate <= threshold) return null;

        // Cut just before the user turn that starts the kept tail, so no tool call is separated from its result.
        List<int> turnStarts = [.. entries.Select((e, i) => (e, i)).Where(x => IsTurnStart(x.e.Message)).Select(x => x.i)];
        int keep = Math.Max(1, config.KeepTurns);
        if (turnStarts.Count <= keep) return null;
        int firstKept = turnStarts[^keep];
        if (firstKept == 0) return null;
        List<HistoryEntry> summarised = entries[..firstKept];

        string transcript = Transcript(previous?.Summary, summarised, model.ContextWindow is int w and > 0 ? w * 2 : DefaultThresholdTokens * 4);
        ChatResponse response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, transcript)],
            new ChatOptions { Temperature = 0 }, ct);
        string summary = response.Text.Trim();
        if (summary.Length == 0) return null;

        CompactionCheckpoint checkpoint = new(summarised[^1].Seq, DateTimeOffset.UtcNow, summary);
        session.AppendCheckpoint(checkpoint);
        emit(EventTypes.Checkpoint, new JsonObject
        {
            ["upToSeq"] = checkpoint.UpToSeq,
            ["messages"] = summarised.Count,
            ["estimatedTokens"] = summarised.Sum(e => EstimateTokens(e.Message)),
            ["summaryChars"] = summary.Length,
        });
        return checkpoint;
    }

    /// <summary>A user message a person (or a trigger) wrote, as opposed to approval answers or harness retry prompts.</summary>
    private static bool IsTurnStart(ChatMessage message) =>
        message.Role == ChatRole.User && message.Contents.Any(c => c is TextContent) && !message.Contents.Any(c => c is ToolApprovalResponseContent);

    /// <summary>The conversation as plain text, tool results clipped, keeping the newest part when it is too long.</summary>
    internal static string Transcript(string? previousSummary, IEnumerable<HistoryEntry> entries, int maxChars)
    {
        List<string> parts = [];
        foreach (HistoryEntry e in entries)
            foreach (AIContent content in e.Message.Contents)
            {
                string? line = content switch
                {
                    TextContent { Text.Length: > 0 } t => $"{e.Message.Role.Value}: {t.Text}",
                    FunctionCallContent call => $"{e.Message.Role.Value} called {call.Name}({Clip(JsonSerializer.Serialize(call.Arguments, Compact), 400)})",
                    FunctionResultContent result => $"result: {Clip(result.Result?.ToString() ?? "", MaxResultChars)}",
                    _ => null,
                };
                if (line is not null) parts.Add(line);
            }
        int budget = Math.Max(4_000, maxChars - (previousSummary?.Length ?? 0));
        int start = parts.Count;
        int length = 0;
        while (start > 0 && length + parts[start - 1].Length + 2 <= budget) length += parts[--start].Length + 2;
        StringBuilder sb = new();
        if (previousSummary is not null) sb.Append("Earlier summary:\n").Append(previousSummary).Append("\n\n");
        sb.Append("Conversation").Append(start > 0 ? $" (the oldest {start} entries are omitted)" : "").Append(":\n\n");
        sb.AppendJoin("\n\n", parts.Skip(start));
        return sb.ToString();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max / 2)] + " … " + text[^(max / 2)..];
}
