using Harness.Core.Agents;
using Harness.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Harness.Extensions.Plugins;

/// <summary>Contributes tools, context providers, prompt contributors and hooks from the plugins enabled for the run's agent.</summary>
public sealed class PluginExtension(PluginRegistry registry) : IRunExtension
{
    public ValueTask<IEnumerable<AITool>> GetToolsAsync(RunContext run, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IEnumerable<AITool>>([.. registry.EnabledFor(run.Agent).SelectMany(p => p.CreateTools(run))]);

    public ValueTask<IEnumerable<AIContextProvider>> GetContextProvidersAsync(RunContext run, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IEnumerable<AIContextProvider>>([.. registry.EnabledFor(run.Agent).SelectMany(p => p.CreateContextProviders(run.Services))]);

    public IEnumerable<IPromptContributor> GetPromptContributors(RunContext run) =>
        registry.EnabledFor(run.Agent).SelectMany(p => p.PromptContributors);

    public IEnumerable<IHarnessHook> GetHooks(RunContext run) =>
        registry.EnabledFor(run.Agent).SelectMany(p => p.Hooks);
}
