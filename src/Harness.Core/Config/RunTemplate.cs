using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace Harness.Core.Config;

/// <summary>
/// A run template: a folder under <c>templates/</c> with a <c>template.yaml</c> that declares how to build a triggered run's
/// workspace, which agent and sandbox it gets, the prompt, and the output contract.
/// </summary>
public sealed class RunTemplate
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Agent definition; unset uses the trigger's agent or the default agent.</summary>
    public string? Agent { get; set; }
    /// <summary>Sandbox profile; overrides the agent's (and any folder config in the built workspace).</summary>
    public string? Sandbox { get; set; }
    public TemplateWorkspace Workspace { get; set; } = new();
    /// <summary>The task; placeholders such as <c>{inputs.repo}</c> and <c>{event.text}</c> are rendered.</summary>
    public string? Prompt { get; set; }
    /// <summary>Extra instructions for prompt layer 6, next to the output contract.</summary>
    public string? Instructions { get; set; }
    /// <summary>Input defaults; the trigger's <c>inputs:</c> and inputs given at fire time override them.</summary>
    public Dictionary<string, string> Inputs { get; set; } = [];
    public TemplateOutput Output { get; set; } = new();
    public TemplateLimits Limits { get; set; } = new();

    /// <summary>Host path of the template folder.</summary>
    [YamlIgnore]
    public string Directory { get; set; } = "";

    public static RunTemplate Load(string directory)
    {
        string file = Path.Combine(directory, "template.yaml");
        if (!File.Exists(file)) file = Path.Combine(directory, "template.yml");
        if (!File.Exists(file)) throw new ConfigException($"{directory}: no template.yaml.");
        RunTemplate template;
        try { template = Parse(File.ReadAllText(file)); }
        catch (YamlDotNet.Core.YamlException ex) { throw new ConfigException($"{file}:{ex.Start.Line}:{ex.Start.Column}: {ex.InnerException?.Message ?? ex.Message}", ex); }
        catch (Exception ex) when (ex is not ConfigException) { throw new ConfigException($"{file}: {ex.Message}", ex); }
        if (string.IsNullOrEmpty(template.Name)) template.Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        template.Directory = Path.GetFullPath(directory);
        foreach (string problem in template.Problems()) throw new ConfigException($"{file}: {problem}");
        return template;
    }

    public static RunTemplate Parse(string yaml)
    {
        // Steps and sinks are free-form (plugins define their own); everything else is parsed strictly.
        Dictionary<object, object?> map = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build()
            .Deserialize<Dictionary<object, object?>>(yaml) ?? [];
        JsonArray? steps = null, sinks = null;
        if (map.GetValueOrDefault("workspace") is Dictionary<object, object?> ws && ws.Remove("steps", out object? s))
            steps = Yaml.ToJsonNode(s) as JsonArray ?? throw new ConfigException("workspace.steps must be a list.");
        if (map.GetValueOrDefault("output") is Dictionary<object, object?> output && output.Remove("sinks", out object? k))
            sinks = Yaml.ToJsonNode(k) as JsonArray ?? throw new ConfigException("output.sinks must be a list.");
        RunTemplate template = Yaml.Parse<RunTemplate>(new SerializerBuilder().Build().Serialize(map));
        template.Workspace.Steps = steps;
        template.Output.Sinks = sinks;
        return template;
    }

    /// <summary>Problems that make the template unusable.</summary>
    public IEnumerable<string> Problems()
    {
        if (Workspace.Keep is not ("always" or "onFailure" or "never"))
            yield return $"workspace.keep must be always, onFailure or never, not '{Workspace.Keep}'.";
        if (Output.Kind is not ("text" or "json" or "files" or "reply"))
            yield return $"output.kind must be text, json, files or reply, not '{Output.Kind}'.";
        if (Output.Retries < 0) yield return "output.retries must not be negative.";
        if (Output.Schema is not null && Output.Kind != "json") yield return "output.schema applies only to output.kind: json.";
        if (Output.Schema is { } schema && Directory.Length > 0 && !File.Exists(Path.Combine(Directory, schema)))
            yield return $"output.schema '{schema}' does not exist in the template folder.";
        foreach (JsonNode? step in Workspace.Steps ?? [])
            if (step is not JsonObject { Count: 1 })
                yield return $"each workspace step must be a map with one key (git, copy, run, or a plugin step type), not {step?.ToJsonString() ?? "null"}.";
    }
}

public sealed class TemplateWorkspace
{
    /// <summary><c>- git: {...}</c>, <c>- copy: {...}</c>, <c>- run: ./setup.sh</c>, or a plugin step. Unset copies <c>files/</c> and runs <c>setup.sh</c> when they exist.</summary>
    [YamlIgnore]
    public JsonArray? Steps { get; set; }
    /// <summary>When to keep <c>runs/&lt;run-id&gt;/workspace</c> after the run: <c>always</c>, <c>onFailure</c> or <c>never</c>.</summary>
    public string Keep { get; set; } = "onFailure";
}

public sealed class TemplateOutput
{
    /// <summary><c>text</c>, <c>json</c>, <c>files</c> or <c>reply</c>.</summary>
    public string Kind { get; set; } = "text";
    /// <summary>JSON Schema file (relative to the template folder) that <c>submit_output</c> data must satisfy; <c>json</c> only.</summary>
    public string? Schema { get; set; }
    /// <summary>Files, relative to the workspace, that must exist when output is submitted. They are kept with the run's output.</summary>
    public List<string> Files { get; set; } = [];
    /// <summary>How many times the agent is told what is wrong and asked again before the run ends as <c>invalid_output</c>.</summary>
    public int Retries { get; set; } = 2;
    /// <summary>Default sinks, used when the trigger declares none.</summary>
    [YamlIgnore]
    public JsonArray? Sinks { get; set; }
}

public sealed class TemplateLimits
{
    public int? MaxRunMinutes { get; set; }
    public long? MaxTokens { get; set; }
    public int? MaxToolIterations { get; set; }
}
