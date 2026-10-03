using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Sdk;

/// <summary>Entry point of a C# plugin. One public, parameterless implementation per plugin assembly.</summary>
public interface IHarnessPlugin
{
    string Id { get; }
    void Configure(IPluginBuilder plugin);
}

/// <summary>Registration surface handed to <see cref="IHarnessPlugin.Configure"/>.</summary>
public interface IPluginBuilder
{
    /// <summary>Plugin-scoped services. Registered types are resolved from here for tools, hooks, sinks and so on.</summary>
    IServiceCollection Services { get; }

    /// <summary>The plugin's section of <c>config.yaml</c> (<c>plugins.&lt;id&gt;</c>).</summary>
    IConfiguration Configuration { get; }

    /// <summary>Every public instance method of <typeparamref name="T"/> marked with <c>[Description]</c> becomes a tool.</summary>
    IPluginBuilder AddTools<T>() where T : class;
    IPluginBuilder AddTool(Func<IToolContext, AITool> factory);
    IPluginBuilder AddSkill(AgentSkill skill);
    IPluginBuilder AddContextProvider<T>() where T : AIContextProvider;
    IPluginBuilder AddPromptContributor<T>() where T : class, IPromptContributor;
    IPluginBuilder AddHook<T>() where T : class, IHarnessHook;
    IPluginBuilder AddTriggerSource<T>(string type) where T : class, ITriggerSource;
    IPluginBuilder AddOutputSink<T>(string type) where T : class, IOutputSink;
    IPluginBuilder AddSandboxProvider<T>(string type) where T : class, ISandboxProvider;
}

/// <summary>What a tool factory knows about the run it is building tools for.</summary>
public interface IToolContext
{
    string RunId { get; }
    string SessionId { get; }
    string AgentName { get; }
    /// <summary>Host path of the workspace root.</summary>
    string WorkspaceRoot { get; }
    ISandbox Sandbox { get; }
    IServiceProvider Services { get; }
}

/// <summary>Adds text to the composed system prompt (layer 7).</summary>
public interface IPromptContributor
{
    ValueTask<string?> ContributeAsync(PromptContributionContext context, CancellationToken cancellationToken);
}

public sealed record PromptContributionContext(string RunId, string SessionId, string AgentName, string WorkspaceRoot, string WorkingDirectory);
