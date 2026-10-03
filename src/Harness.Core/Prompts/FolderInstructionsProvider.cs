using Harness.Core.Agents;
using Harness.Sdk;
using Microsoft.Agents.AI;

namespace Harness.Core.Prompts;

/// <summary>Injects the composed prompt layers on every agent invocation, so edits to AGENTS.md apply on the next turn.</summary>
public sealed class FolderInstructionsProvider(PromptComposer composer, RunContext run, IReadOnlyList<IPromptContributor> contributors)
    : AIContextProvider(null, null, null)
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) =>
        new() { Instructions = await composer.ComposeAsync(run, contributors, cancellationToken) };
}
