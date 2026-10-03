using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Harness.Sdk;
using Microsoft.Extensions.AI;

namespace Harness.Core.Agents;

/// <summary>
/// Chat-client middleware for the model-call hooks: runs <c>OnModelCalling</c>/<c>OnModelCalled</c>,
/// records token usage on the run, emits <c>USAGE</c> events and enforces <c>maxTokens</c>.
/// </summary>
public sealed class HookingChatClient(IChatClient inner, RunContext run, HookPipeline hooks) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        (List<ChatMessage> list, ChatOptions opts) = await BeforeAsync(messages, options, cancellationToken);
        ChatResponse response = await base.GetResponseAsync(list, opts, cancellationToken);
        await AfterAsync(response.Usage, response.ModelId, cancellationToken);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        (List<ChatMessage> list, ChatOptions opts) = await BeforeAsync(messages, options, cancellationToken);
        UsageDetails? usage = null;
        string? modelId = null;
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(list, opts, cancellationToken))
        {
            modelId ??= update.ModelId;
            foreach (AIContent content in update.Contents)
                if (content is UsageContent u)
                {
                    usage ??= new UsageDetails();
                    usage.Add(u.Details);
                }
            yield return update;
        }
        await AfterAsync(usage, modelId, cancellationToken);
    }

    private async ValueTask<(List<ChatMessage>, ChatOptions)> BeforeAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        if (run.Agent.Limits.MaxTokens is long max && run.InputTokens + run.OutputTokens >= max)
            throw new RunLimitExceededException(run.Agent.Limits.MaxTokensReason ?? $"Token limit of {max:N0} reached.");

        List<ChatMessage> list = [.. messages];
        ChatOptions opts = options?.Clone() ?? new ChatOptions();
        if (hooks.Hooks.Count > 0)
            await hooks.ModelCallingAsync(new ModelCallingContext
            {
                RunId = run.RunId, SessionId = run.SessionId, AgentName = run.AgentName, WorkspaceRoot = run.WorkspaceRoot,
                Messages = list, Options = opts,
            }, ct);
        return (list, opts);
    }

    private async ValueTask AfterAsync(UsageDetails? usage, string? modelId, CancellationToken ct)
    {
        if (usage is not null)
        {
            long input = usage.InputTokenCount ?? 0, output = usage.OutputTokenCount ?? 0;
            run.AddUsage(input, output);
            run.Events.Emit(EventTypes.Usage, new JsonObject
            {
                ["inputTokens"] = input,
                ["outputTokens"] = output,
                ["runInputTokens"] = run.InputTokens,
                ["runOutputTokens"] = run.OutputTokens,
                ["model"] = modelId,
            });
        }
        if (hooks.Hooks.Count > 0)
            await hooks.ModelCalledAsync(new ModelCalledContext
            {
                RunId = run.RunId, SessionId = run.SessionId, AgentName = run.AgentName, WorkspaceRoot = run.WorkspaceRoot,
                Usage = usage, ModelId = modelId,
            }, ct);
    }
}
