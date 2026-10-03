using System.ComponentModel;
using System.Reflection;
using Harness.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Extensions.Plugins;

/// <summary>Collects one plugin's registrations. After <see cref="IHarnessPlugin.Configure"/> it becomes a <see cref="LoadedPlugin"/>.</summary>
internal sealed class PluginBuilder(string id, IConfiguration configuration) : IPluginBuilder
{
    public IServiceCollection Services { get; } = new ServiceCollection();
    public IConfiguration Configuration => configuration;

    public List<Type> ToolTypes { get; } = [];
    public List<Func<IToolContext, AITool>> ToolFactories { get; } = [];
    public List<AgentSkill> Skills { get; } = [];
    public List<Type> ContextProviderTypes { get; } = [];
    public List<Type> PromptContributorTypes { get; } = [];
    public List<Type> HookTypes { get; } = [];
    public Dictionary<string, Type> TriggerSources { get; } = [];
    public Dictionary<string, Type> OutputSinks { get; } = [];
    public Dictionary<string, Type> SandboxProviders { get; } = [];
    public Dictionary<string, Type> WorkspaceSteps { get; } = [];

    public IPluginBuilder AddTools<T>() where T : class { Services.AddSingleton<T>(); ToolTypes.Add(typeof(T)); return this; }
    public IPluginBuilder AddTool(Func<IToolContext, AITool> factory) { ToolFactories.Add(factory); return this; }
    public IPluginBuilder AddSkill(AgentSkill skill) { Skills.Add(skill); return this; }
    public IPluginBuilder AddContextProvider<T>() where T : AIContextProvider { ContextProviderTypes.Add(typeof(T)); return this; }
    public IPluginBuilder AddPromptContributor<T>() where T : class, IPromptContributor { Services.AddSingleton<T>(); PromptContributorTypes.Add(typeof(T)); return this; }
    public IPluginBuilder AddHook<T>() where T : class, IHarnessHook { Services.AddSingleton<T>(); HookTypes.Add(typeof(T)); return this; }
    public IPluginBuilder AddTriggerSource<T>(string type) where T : class, ITriggerSource { Services.AddSingleton<T>(); TriggerSources[type] = typeof(T); return this; }
    public IPluginBuilder AddOutputSink<T>(string type) where T : class, IOutputSink { Services.AddSingleton<T>(); OutputSinks[type] = typeof(T); return this; }
    public IPluginBuilder AddSandboxProvider<T>(string type) where T : class, ISandboxProvider { Services.AddSingleton<T>(); SandboxProviders[type] = typeof(T); return this; }
    public IPluginBuilder AddWorkspaceStep<T>(string type) where T : class, IWorkspaceStep { Services.AddSingleton<T>(); WorkspaceSteps[type] = typeof(T); return this; }

    public LoadedPlugin Build(PluginManifest? manifest, PluginLoadContext? context)
    {
        Services.AddSingleton(Configuration);
        ServiceProvider provider = Services.BuildServiceProvider();
        return new LoadedPlugin(id, manifest, context, provider, this);
    }
}

/// <summary>A configured plugin and its service provider.</summary>
public sealed class LoadedPlugin : IAsyncDisposable
{
    private readonly PluginBuilder _b;

    internal LoadedPlugin(string id, PluginManifest? manifest, PluginLoadContext? context, ServiceProvider services, PluginBuilder builder)
    {
        Id = id;
        Manifest = manifest;
        Context = context;
        Services = services;
        _b = builder;
    }

    public string Id { get; }
    public PluginManifest? Manifest { get; }
    public PluginLoadContext? Context { get; }
    public ServiceProvider Services { get; }

    public IReadOnlyList<AgentSkill> Skills => _b.Skills;
    public IReadOnlyDictionary<string, Type> TriggerSources => _b.TriggerSources;
    public IReadOnlyDictionary<string, Type> OutputSinks => _b.OutputSinks;
    public IReadOnlyDictionary<string, Type> SandboxProviders => _b.SandboxProviders;
    public IReadOnlyDictionary<string, Type> WorkspaceSteps => _b.WorkspaceSteps;

    public IEnumerable<AITool> CreateTools(IToolContext context)
    {
        foreach (Type type in _b.ToolTypes)
        {
            object instance = Services.GetRequiredService(type);
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (method.GetCustomAttribute<DescriptionAttribute>() is not null)
                    yield return AIFunctionFactory.Create(method, instance, new AIFunctionFactoryOptions { Name = ToolName(method.Name) });
        }
        foreach (Func<IToolContext, AITool> factory in _b.ToolFactories) yield return factory(context);
    }

    public IEnumerable<AIContextProvider> CreateContextProviders(IServiceProvider runServices) =>
        _b.ContextProviderTypes.Select(t => (AIContextProvider)ActivatorUtilities.CreateInstance(new CompositeServiceProvider(Services, runServices), t));

    public IEnumerable<IPromptContributor> PromptContributors => _b.PromptContributorTypes.Select(t => (IPromptContributor)Services.GetRequiredService(t));

    public IEnumerable<IHarnessHook> Hooks => _b.HookTypes.Select(t => (IHarnessHook)Services.GetRequiredService(t));

    public T Resolve<T>(Type type) => (T)Services.GetRequiredService(type);

    /// <summary><c>GetFileStatus</c> becomes <c>get_file_status</c>.</summary>
    internal static string ToolName(string method)
    {
        if (method.EndsWith("Async", StringComparison.Ordinal)) method = method[..^5];
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < method.Length; i++)
        {
            if (char.IsUpper(method[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(method[i]));
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Context?.Unload();
    }

    private sealed class CompositeServiceProvider(IServiceProvider first, IServiceProvider second) : IServiceProvider
    {
        public object? GetService(Type serviceType) => first.GetService(serviceType) ?? second.GetService(serviceType);
    }
}
