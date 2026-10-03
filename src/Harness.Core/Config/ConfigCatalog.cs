using System.Collections.Concurrent;

namespace Harness.Core.Config;

/// <summary>
/// Reads the declarative files under the harness home. Every read checks the file's timestamp, so edits to
/// agents, prompts and sandboxes apply to the next run without a restart.
/// </summary>
public sealed class ConfigCatalog(HarnessPaths paths)
{
    private readonly ConcurrentDictionary<string, (DateTime Stamp, object Value)> _cache = new();

    public HarnessPaths Paths => paths;

    public HarnessConfig Config => Cached(paths.ConfigFile, p => Yaml.Load<HarnessConfig>(p), () => new HarnessConfig());

    public IReadOnlyDictionary<string, SandboxProfile> Sandboxes =>
        Cached(paths.SandboxesFile, p => Yaml.Load<Dictionary<string, SandboxProfile>>(p), () => new Dictionary<string, SandboxProfile>());

    public IReadOnlyDictionary<string, string> Workspaces =>
        Cached(paths.WorkspacesFile, p => Yaml.Load<Dictionary<string, string>>(p), () => new Dictionary<string, string>());

    public IReadOnlyList<AgentDefinition> Agents()
    {
        if (!Directory.Exists(paths.AgentsDir)) return [];
        return [.. Directory.EnumerateFiles(paths.AgentsDir, "*.yaml").Concat(Directory.EnumerateFiles(paths.AgentsDir, "*.yml"))
            .Order(StringComparer.Ordinal)
            .Select(LoadAgentFile)];
    }

    public AgentDefinition Agent(string name)
    {
        foreach (string ext in new[] { ".yaml", ".yml" })
        {
            string file = Path.Combine(paths.AgentsDir, name + ext);
            if (File.Exists(file)) return LoadAgentFile(file);
        }
        return Agents().FirstOrDefault(a => a.Name == name)
            ?? throw new ConfigException($"Agent '{name}' not found in {paths.AgentsDir}.");
    }

    public ModelProfile Model(string name) =>
        Config.Models.TryGetValue(name, out ModelProfile? profile)
            ? profile
            : throw new ConfigException($"Model profile '{name}' is not defined in {paths.ConfigFile}.");

    public SandboxProfile Sandbox(string name)
    {
        if (Sandboxes.TryGetValue(name, out SandboxProfile? profile)) return profile;
        if (name == "none") return new SandboxProfile { Type = "none" };
        throw new ConfigException($"Sandbox profile '{name}' is not defined in {paths.SandboxesFile}.");
    }

    /// <summary>Run templates: every folder under <c>templates/</c> with a <c>template.yaml</c>.</summary>
    public IReadOnlyList<RunTemplate> Templates()
    {
        if (!Directory.Exists(paths.TemplatesDir)) return [];
        List<RunTemplate> templates = [];
        foreach (string dir in Directory.EnumerateDirectories(paths.TemplatesDir).Order(StringComparer.Ordinal))
            if (TemplateFile(dir) is { } file) templates.Add(LoadTemplateFile(file));
        return templates;
    }

    public RunTemplate Template(string name)
    {
        if (name.Contains('/') || name.Contains('\\') || name is "." or "..")
            throw new ConfigException($"Template name '{name}' is not valid.");
        string dir = Path.Combine(paths.TemplatesDir, name);
        if (TemplateFile(dir) is { } file) return LoadTemplateFile(file);
        return Templates().FirstOrDefault(t => t.Name == name)
            ?? throw new ConfigException($"Run template '{name}' not found in {paths.TemplatesDir}.");
    }

    private static string? TemplateFile(string dir) =>
        new[] { "template.yaml", "template.yml" }.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);

    private RunTemplate LoadTemplateFile(string file) =>
        Cached(file, p => RunTemplate.Load(Path.GetDirectoryName(p)!), () => throw new ConfigException($"{file} does not exist."));

    /// <summary>Resolves a named workspace to its host path, or returns null.</summary>
    public string? Workspace(string name) =>
        Workspaces.TryGetValue(name, out string? path) ? HarnessPaths.ExpandHome(path) : null;

    public void SetWorkspace(string name, string path)
    {
        Dictionary<string, string> all = new(Workspaces) { [name] = path };
        Directory.CreateDirectory(paths.Home);
        File.WriteAllText(paths.WorkspacesFile, Yaml.Serialize(all));
        _cache.TryRemove(paths.WorkspacesFile, out _);
    }

    public bool RemoveWorkspace(string name)
    {
        Dictionary<string, string> all = new(Workspaces);
        if (!all.Remove(name)) return false;
        File.WriteAllText(paths.WorkspacesFile, Yaml.Serialize(all));
        _cache.TryRemove(paths.WorkspacesFile, out _);
        return true;
    }

    /// <summary>Validates every declarative file and returns one message per problem.</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];
        void Try(Action action) { try { action(); } catch (Exception ex) { problems.Add(ex.Message); } }

        Try(() => _ = Config);
        Try(() => _ = Sandboxes);
        Try(() => _ = Workspaces);
        if (Directory.Exists(paths.AgentsDir))
        {
            foreach (string file in Directory.EnumerateFiles(paths.AgentsDir, "*.y*ml"))
            {
                Try(() =>
                {
                    AgentDefinition def = LoadAgentFile(file);
                    if (!Config.Models.ContainsKey(def.Model))
                        problems.Add($"{file}: model profile '{def.Model}' is not defined.");
                    if (def.Sandbox != "none" && !Sandboxes.ContainsKey(def.Sandbox))
                        problems.Add($"{file}: sandbox profile '{def.Sandbox}' is not defined.");
                    if (def.Instructions is { } instr && !File.Exists(paths.Resolve(instr)))
                        problems.Add($"{file}: instructions file '{instr}' does not exist.");
                    foreach ((string tool, string policy) in def.Approvals)
                        if (policy is not ("ask" or "allow" or "deny" or "allowlist"))
                            problems.Add($"{file}: approval policy '{policy}' for '{tool}' must be ask, allow, deny or allowlist.");
                });
            }
        }
        if (Directory.Exists(paths.TemplatesDir))
        {
            foreach (string dir in Directory.EnumerateDirectories(paths.TemplatesDir))
            {
                if (TemplateFile(dir) is not { } file) continue;
                Try(() =>
                {
                    RunTemplate t = LoadTemplateFile(file);
                    if (t.Agent is { } agent && Agents().All(a => a.Name != agent))
                        problems.Add($"{file}: agent '{agent}' is not defined.");
                    if (t.Sandbox is { } sandbox && sandbox != "none" && !Sandboxes.ContainsKey(sandbox))
                        problems.Add($"{file}: sandbox profile '{sandbox}' is not defined.");
                });
            }
        }
        foreach ((string name, ModelProfile profile) in SafeModels())
            if (profile.Provider is not ("ollama" or "azure-openai" or "openai"))
                problems.Add($"{paths.ConfigFile}: model '{name}' has unknown provider '{profile.Provider}'.");
        return problems;

        IEnumerable<KeyValuePair<string, ModelProfile>> SafeModels()
        {
            try { return Config.Models; } catch { return []; }
        }
    }

    private AgentDefinition LoadAgentFile(string file) =>
        Cached(file, p =>
        {
            AgentDefinition def = Yaml.Load<AgentDefinition>(p);
            if (string.IsNullOrEmpty(def.Name)) def.Name = Path.GetFileNameWithoutExtension(p);
            if (string.IsNullOrEmpty(def.Model)) def.Model = Config.Defaults.Model ?? Config.Models.Keys.FirstOrDefault() ?? "";
            def.SourcePath = p;
            return def;
        }, () => throw new ConfigException($"{file} does not exist."));

    private T Cached<T>(string path, Func<string, T> load, Func<T> missing) where T : class
    {
        if (!File.Exists(path))
        {
            _cache.TryRemove(path, out _);
            return missing();
        }
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        if (_cache.TryGetValue(path, out var hit) && hit.Stamp == stamp && hit.Value is T value)
            return value;
        T loaded = load(path);
        _cache[path] = (stamp, loaded);
        return loaded;
    }
}
