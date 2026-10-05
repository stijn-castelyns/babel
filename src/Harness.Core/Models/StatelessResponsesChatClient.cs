using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

// The OpenAI SDK still marks its Responses types as evaluation-only; they are used here and nowhere else.
#pragma warning disable OPENAI001

namespace Harness.Core.Models;

/// <summary>
/// Keeps a Responses API client stateless: every request is sent with <c>store: false</c> and the full history from the
/// harness's own session files, never a <c>previous_response_id</c> or conversation. Reasoning comes back encrypted so it
/// can be replayed from local history on the next request.
/// </summary>
public sealed class StatelessResponsesChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, Stateless(options), cancellationToken);
        EnsureStateless(response.ConversationId);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, Stateless(options), cancellationToken))
        {
            EnsureStateless(update.ConversationId);
            yield return update;
        }
    }

    internal static ChatOptions Stateless(ChatOptions? options)
    {
        if (options?.ConversationId is { } id)
            throw new InvalidOperationException($"Conversation '{id}' would continue server-side state; the harness keeps history locally.");
        ChatOptions result = options?.Clone() ?? new ChatOptions();
        Func<IChatClient, object?>? previous = result.RawRepresentationFactory;
        result.RawRepresentationFactory = client =>
        {
            CreateResponseOptions raw = previous?.Invoke(client) as CreateResponseOptions ?? new CreateResponseOptions();
            raw.StoredOutputEnabled = false;
            raw.PreviousResponseId = null;
            if (!raw.IncludedProperties.Contains(IncludedResponseProperty.ReasoningEncryptedContent))
                raw.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
            return raw;
        };
        return result;
    }

    private static void EnsureStateless(string? conversationId)
    {
        if (conversationId is not null)
            throw new InvalidOperationException($"The service returned conversation '{conversationId}', so it stored the response; the harness requires store=false.");
    }
}
