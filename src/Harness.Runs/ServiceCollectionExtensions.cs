using Harness.Core;
using Harness.Core.Agents;
using Harness.Core.Config;
using Harness.Core.Models;
using Harness.Core.Prompts;
using Harness.Core.Sessions;
using Harness.Extensions.Hooks;
using Harness.Extensions.Mcp;
using Harness.Extensions.Plugins;
using Harness.Extensions.Skills;
using Harness.Runs.Templates;
using Harness.Sandbox;
using Harness.Sdk;
using Harness.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Runs;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers everything a daemon needs to run agents: config, sessions, models, tools, extensions, sandboxes and the orchestrator.</summary>
    public static IServiceCollection AddHarnessRuntime(this IServiceCollection services, HarnessPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<ConfigCatalog>();
        services.AddSingleton<SecretStore>();
        services.AddSingleton<TrustStore>();
        services.AddSingleton<HarnessDb>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<SessionRuntimeRegistry>();
        services.AddSingleton<ChatClientFactory>();
        services.AddSingleton<ModelDoctor>();
        services.AddSingleton<PromptComposer>();

        services.AddSingleton(sp =>
        {
            PluginRegistry registry = new(sp.GetRequiredService<ConfigCatalog>(), sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>());
            registry.LoadInstalled();
            return registry;
        });
        services.AddSingleton<McpExtension>();
        services.AddSingleton<SkillsExtension>();
        services.AddSingleton<IRunExtension, BuiltinToolsExtension>();
        services.AddSingleton<IRunExtension>(sp => sp.GetRequiredService<McpExtension>());
        services.AddSingleton<IRunExtension>(sp => sp.GetRequiredService<SkillsExtension>());
        services.AddSingleton<IRunExtension>(sp => new PluginExtension(sp.GetRequiredService<PluginRegistry>()));
        services.AddSingleton<IRunExtension, CommandHooksExtension>();
        services.AddSingleton<AgentFactory>();

        services.AddSingleton(sp => new SandboxFactory(
            sp.GetRequiredService<ConfigCatalog>(),
            [.. sp.GetRequiredService<PluginRegistry>().SandboxProviders(), .. SandboxFactory.BuiltIn()]));

        services.AddSingleton<EventHub>();
        services.AddSingleton<ApprovalBroker>();
        services.AddSingleton<ApprovalStore>();
        services.AddSingleton<IWorkspaceStep, GitStep>();
        services.AddSingleton<IWorkspaceStep, CopyStep>();
        services.AddSingleton<IWorkspaceStep, RunStep>();
        services.AddSingleton<WorkspaceBuilder>();
        services.AddSingleton<RunOrchestrator>();
        return services;
    }
}
