using Harness.Core.Models;
using Harness.Core.Prompts;
using Harness.Core.Sessions;
using Harness.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Core.Agents;

/// <summary>An agent built for one run, plus what the run needs to drive and dispose it.</summary>
/// <param name="Model">The run's model with hooks and usage accounting but no tools or compaction, for side calls such as checkpoint summaries.</param>
public sealed record BuiltAgent(AIAgent Agent, HookPipeline Hooks, IReadOnlyList<IRunExtension> Extensions, IReadOnlyList<AITool> Tools, IChatClient Model);

/// <summary>
/// The single place where tools, skills, MCP, plugins, prompts, history and hooks are wired into a <see cref="ChatClientAgent"/>.
/// </summary>
public sealed class AgentFactory(
    ChatClientFactory models,
    PromptComposer prompts,
    IEnumerable<IRunExtension> extensions,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILoggerFactory _loggers = loggerFactory ?? NullLoggerFactory.Instance;

    /// <summary>Overrides model creation, for tests and for plugins that supply their own clients.</summary>
    public Func<RunContext, IChatClient>? ChatClientOverride { get; set; }

    public async Task<BuiltAgent> BuildAsync(RunContext run, IEnumerable<IHarnessHook> extraHooks, CancellationToken ct)
    {
        IReadOnlyList<IRunExtension> exts = [.. extensions];
        HookPipeline hooks = new([.. extraHooks, .. exts.SelectMany(e => e.GetHooks(run))]);

        List<AITool> raw = [];
        foreach (IRunExtension ext in exts) raw.AddRange(await ext.GetToolsAsync(run, ct));
        IList<AITool> tools = ApprovalPolicy.Apply(run.Agent, raw);

        List<AIContextProvider> contextProviders =
        [
            new FolderInstructionsProvider(prompts, run, [.. exts.SelectMany(e => e.GetPromptContributors(run))]),
        ];
        foreach (IRunExtension ext in exts) contextProviders.AddRange(await ext.GetContextProvidersAsync(run, ct));

        IChatClient model = ChatClientOverride?.Invoke(run) ?? models.Create(run.Model);
        ChatClientFactory.EnsureChatCompletions(model, run.Model);

        ChatClientAgent inner = model.AsBuilder()
            .Use(next => new CompactingChatClient(next, Compaction(run), run, hooks, _loggers.CreateLogger<CompactingChatClient>()))
            .Use(next => new HookingChatClient(next, run, hooks))
            .UseOpenTelemetry(_loggers, sourceName: "Harness")
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = run.Agent.Name,
                Description = run.Agent.Description,
                ChatOptions = new ChatOptions
                {
                    Tools = tools,
                    Temperature = run.Model.Temperature,
                    MaxOutputTokens = run.Model.MaxOutputTokens,
                },
                AIContextProviders = contextProviders,
                ChatHistoryProvider = new FileChatHistoryProvider(run.Session, run.RunId),
            }, _loggers);

        if (inner.ChatClient.GetService<FunctionInvokingChatClient>() is { } fic)
        {
            fic.MaximumIterationsPerRequest = Math.Max(1, run.Agent.Limits.MaxToolIterations);
            fic.IncludeDetailedErrors = true;
        }

        ToolCallMiddleware middleware = new(run, hooks);
        AIAgent agent = inner.AsBuilder().Use(middleware.InvokeAsync).Build();
        IChatClient plain = model.AsBuilder().Use(next => new HookingChatClient(next, run, hooks)).Build();
        return new BuiltAgent(agent, hooks, exts, [.. tools], plain);
    }

    /// <summary>Old tool results fade to stubs first; beyond the turn window, older turns drop out of the model's view. History on disk is never touched.</summary>
    internal static CompactionStrategy Compaction(RunContext run) => new PipelineCompactionStrategy(
    [
        new ToolResultCompactionStrategy(CompactionTriggers.MessagesExceed(Math.Max(4, run.Agent.Compaction.ToolResultsAfter)), 4, null!),
        new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(Math.Max(2, run.Agent.Compaction.SlidingWindowTurns)), Math.Max(1, run.Agent.Compaction.SlidingWindowTurns / 2), null!),
    ]);
}
