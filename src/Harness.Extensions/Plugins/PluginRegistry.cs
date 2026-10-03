using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Harness.Core;
using Harness.Core.Config;
using Harness.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Extensions.Plugins;

/// <summary>Loads plugins from <c>~/.harness/plugins/&lt;id&gt;/</c> and from code (built-in plugins), and serves their registrations.</summary>
public sealed class PluginRegistry(ConfigCatalog catalog, ILoggerFactory? loggerFactory = null) : IAsyncDisposable
{
    public const int SdkMajor = 1;
    private readonly ILogger _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Harness.Plugins");
    private readonly List<LoadedPlugin> _plugins = [];

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    /// <summary>Errors from the last <see cref="LoadInstalled"/>, one per plugin that failed.</summary>
    public List<string> LoadErrors { get; } = [];

    /// <summary>Registers a plugin compiled into the host (or a test).</summary>
    public LoadedPlugin Add(IHarnessPlugin plugin)
    {
        PluginBuilder builder = new(plugin.Id, SectionFor(plugin.Id));
        plugin.Configure(builder);
        LoadedPlugin loaded = builder.Build(null, null);
        _plugins.Add(loaded);
        return loaded;
    }

    public void LoadInstalled()
    {
        string dir = catalog.Paths.PluginsDir;
        if (!Directory.Exists(dir)) return;
        foreach (string pluginDir in Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(pluginDir, "plugin.json"))) continue;
            try
            {
                _plugins.Add(Load(PluginManifest.Load(pluginDir)));
            }
            catch (Exception ex)
            {
                string message = $"Plugin in {pluginDir} failed to load: {ex.Message}";
                LoadErrors.Add(message);
                _log.LogError(ex, "{Message}", message);
            }
        }
    }

    private LoadedPlugin Load(PluginManifest manifest)
    {
        if (!manifest.SupportsSdk(SdkMajor))
            throw new InvalidOperationException($"plugin '{manifest.Id}' supports SDK '{manifest.Sdk}', the host provides SDK {SdkMajor}.");
        string entry = Path.Combine(manifest.Directory, manifest.EntryAssembly);
        if (!File.Exists(entry)) throw new FileNotFoundException($"entry assembly '{manifest.EntryAssembly}' not found.", entry);
        if (manifest.Sha256 is { } expected && !string.Equals(expected, HashFile(entry), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"plugin '{manifest.Id}' entry assembly hash does not match plugin.json; reinstall it.");

        PluginLoadContext context = new(entry);
        Assembly assembly = context.LoadFromAssemblyPath(entry);
        Type type = assembly.GetExportedTypes().FirstOrDefault(t => typeof(IHarnessPlugin).IsAssignableFrom(t) && !t.IsAbstract)
            ?? throw new InvalidOperationException($"no public IHarnessPlugin implementation in {manifest.EntryAssembly}.");
        IHarnessPlugin plugin = (IHarnessPlugin)Activator.CreateInstance(type)!;
        if (plugin.Id != manifest.Id)
            throw new InvalidOperationException($"plugin.json id '{manifest.Id}' does not match the plugin's Id '{plugin.Id}'.");
        PluginBuilder builder = new(plugin.Id, SectionFor(plugin.Id));
        plugin.Configure(builder);
        _log.LogInformation("Loaded plugin {Id} {Version}", manifest.Id, manifest.Version);
        return builder.Build(manifest, context);
    }

    /// <summary>Plugins enabled for an agent: listed under its <c>plugins:</c>, or <c>*</c> for all.</summary>
    public IEnumerable<LoadedPlugin> EnabledFor(AgentDefinition agent) =>
        _plugins.Where(p => agent.Plugins.Contains("*") || agent.Plugins.Contains(p.Id));

    public IReadOnlyList<AgentSkill> SkillsFor(AgentDefinition agent) => [.. EnabledFor(agent).SelectMany(p => p.Skills)];

    public IEnumerable<ISandboxProvider> SandboxProviders() =>
        _plugins.SelectMany(p => p.SandboxProviders.Values.Select(t => p.Resolve<ISandboxProvider>(t)));

    /// <summary>Output sinks keyed by the type they were registered under.</summary>
    public IEnumerable<(string Type, IOutputSink Sink)> OutputSinks() =>
        _plugins.SelectMany(p => p.OutputSinks.Select(kv => (kv.Key, p.Resolve<IOutputSink>(kv.Value))));

    /// <summary>Workspace steps keyed by the type they were registered under.</summary>
    public IEnumerable<(string Type, IWorkspaceStep Step)> WorkspaceSteps() =>
        _plugins.SelectMany(p => p.WorkspaceSteps.Select(kv => (kv.Key, p.Resolve<IWorkspaceStep>(kv.Value))));

    public IEnumerable<ITriggerSource> TriggerSources() =>
        _plugins.SelectMany(p => p.TriggerSources.Values.Select(t => p.Resolve<ITriggerSource>(t)));

    private IConfiguration SectionFor(string id)
    {
        ConfigurationBuilder builder = new();
        if (catalog.Config.Plugins.TryGetValue(id, out object? section) && section is not null)
        {
            string json = Yaml.ToJsonNode(section)?.ToJsonString() ?? "{}";
            if (json.TrimStart().StartsWith('{'))
                builder.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        }
        return builder.Build();
    }

    public static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Copies a built plugin folder into <c>plugins/&lt;id&gt;</c> and records the entry assembly hash.</summary>
    public static PluginManifest Install(HarnessPaths paths, string sourceDir)
    {
        PluginManifest manifest = PluginManifest.Load(sourceDir);
        string target = Path.Combine(paths.PluginsDir, manifest.Id);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        CopyDirectory(sourceDir, target);
        manifest.Sha256 = HashFile(Path.Combine(target, manifest.EntryAssembly));
        File.WriteAllText(Path.Combine(target, "plugin.json"), JsonSerializer.Serialize(manifest, PluginManifest.Json));
        manifest.Directory = target;
        return manifest;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string dir in Directory.EnumerateDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (LoadedPlugin p in _plugins) await p.DisposeAsync();
        _plugins.Clear();
    }
}
