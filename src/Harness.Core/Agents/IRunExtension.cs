using Harness.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Harness.Core.Agents;

/// <summary>
/// One source of per-run capabilities: built-in tools, MCP servers, skills and plugins each implement it.
/// The agent factory asks every registered extension for its share.
/// </summary>
public interface IRunExtension
{
    ValueTask<IEnumerable<AITool>> GetToolsAsync(RunContext run, CancellationToken cancellationToken) => ValueTask.FromResult<IEnumerable<AITool>>([]);
    ValueTask<IEnumerable<AIContextProvider>> GetContextProvidersAsync(RunContext run, CancellationToken cancellationToken) => ValueTask.FromResult<IEnumerable<AIContextProvider>>([]);
    IEnumerable<IPromptContributor> GetPromptContributors(RunContext run) => [];
    IEnumerable<IHarnessHook> GetHooks(RunContext run) => [];
    /// <summary>Called when the run ends, to release per-run resources such as stdio MCP servers.</summary>
    ValueTask ReleaseAsync(RunContext run) => ValueTask.CompletedTask;
}
