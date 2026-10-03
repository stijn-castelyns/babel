using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Harness.Tests.TestSupport;

/// <summary>A fake model: each call hands the conversation to a script that decides the reply.</summary>
public sealed class ScriptedChatClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> script) : IChatClient
{
    public List<List<ChatMessage>> Calls { get; } = [];
    public List<ChatOptions?> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> list = [.. messages];
        lock (Calls) { Calls.Add(list); Options.Add(options); }
        ChatResponse response = script(list, options);
        response.Usage ??= new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10, TotalTokenCount = 110 };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }

    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text)) { ResponseId = Guid.NewGuid().ToString("N") };

    public static ChatResponse Call(string tool, Dictionary<string, object?> args, string? callId = null) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId ?? "call_" + Guid.NewGuid().ToString("N")[..8], tool, args)]));

    /// <summary>The last message's tool results, if the last message is a tool message.</summary>
    public static IReadOnlyList<FunctionResultContent> LastResults(IReadOnlyList<ChatMessage> messages) =>
        messages.Count > 0 && messages[^1].Role == ChatRole.Tool ? [.. messages[^1].Contents.OfType<FunctionResultContent>()] : [];
}
