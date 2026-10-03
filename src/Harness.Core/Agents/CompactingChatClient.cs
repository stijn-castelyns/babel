using Harness.Sdk;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Harness.Core.Agents;

/// <summary>
/// Applies the agent's compaction strategy to the messages of every model call, including the calls between tool
/// iterations of one invocation. Only the model's view shrinks: <c>history.jsonl</c> keeps every message.
/// </summary>
/// <remarks>
/// Agent Framework's <see cref="CompactionProvider"/> registered as a chat-client context provider stops the agent from
/// persisting request messages, so the strategy is run here through <see cref="CompactionProvider.CompactAsync"/> instead.
/// </remarks>
public sealed class CompactingChatClient(IChatClient inner, CompactionStrategy strategy, RunContext run, HookPipeline hooks, ILogger logger)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await base.GetResponseAsync(await CompactAsync(messages, cancellationToken), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(await CompactAsync(messages, cancellationToken), options, cancellationToken))
            yield return update;
    }

    private async Task<IEnumerable<ChatMessage>> CompactAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        List<ChatMessage> original = [.. messages];
        List<ChatMessage> compacted = [.. await CompactionProvider.CompactAsync(strategy, original, logger, ct)];
        if (compacted.Count == original.Count || hooks.Hooks.Count == 0) return compacted;

        CompactingContext context = new()
        {
            RunId = run.RunId, SessionId = run.SessionId, AgentName = run.AgentName, WorkspaceRoot = run.WorkspaceRoot,
            Messages = original,
        };
        await hooks.CompactingAsync(context, ct);
        // Pinned messages that compaction dropped come back right after any leading system messages.
        // Tool calls and results cannot be pinned on their own: re-inserting one half of a pair would break the history.
        List<ChatMessage> restore = [.. context.Pinned.Order()
            .Where(i => i >= 0 && i < original.Count)
            .Select(i => original[i])
            .Where(m => !compacted.Contains(m) && !m.Contents.Any(c => c is FunctionCallContent or FunctionResultContent))];
        if (restore.Count == 0) return compacted;
        int at = compacted.TakeWhile(m => m.Role == ChatRole.System).Count();
        compacted.InsertRange(at, restore);
        return compacted;
    }
}
