using System.Text.Json.Nodes;
using Harness.Core.Config;

namespace Harness.Triggers;

/// <summary>
/// A trigger in <c>triggers/&lt;id&gt;.yaml</c>: when to run and for whom. It references a run template (<c>template:</c>) that
/// builds the run's workspace and declares the output contract, or names an agent and workspace directly for simple runs.
/// </summary>
public sealed class TriggerDefinition
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>The <c>source:</c> block; <c>type</c> picks the trigger source.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public JsonObject Source { get; set; } = new() { ["type"] = "manual" };
    public TriggerFilter Filter { get; set; } = new();
    /// <summary>Session key template, for example <c>whatsapp:{event.sender}</c>. Unset starts a fresh session per run.</summary>
    public string? Session { get; set; }
    /// <summary>Run template under <c>templates/</c>. When set, every run gets a fresh workspace built by the template.</summary>
    public string? Template { get; set; }
    /// <summary>Agent; overrides the template's.</summary>
    public string? Agent { get; set; }
    /// <summary>Named workspace or path, for runs without a template.</summary>
    public string? Workspace { get; set; }
    /// <summary>
    /// Prompt; overrides the template's, and defaults to <c>{event.text}</c>. Placeholders: <c>{event.text}</c>, <c>{event.sender}</c>,
    /// <c>{event.id}</c>, <c>{event.data}</c>, <c>{inputs.&lt;name&gt;}</c>, <c>{trigger.id}</c>, <c>{date}</c>.
    /// </summary>
    public string? Prompt { get; set; }
    public Dictionary<string, string> Inputs { get; set; } = [];
    public bool AllowUnsandboxed { get; set; }
    /// <summary>The <c>output.sinks:</c> list (<c>reply</c>, <c>file</c>, <c>webhook</c>, <c>run</c>, or plugin sinks); unset uses the template's.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public JsonArray? Sinks { get; set; }
    public TriggerApprovals Approvals { get; set; } = new();

    [YamlDotNet.Serialization.YamlIgnore]
    public string SourceType => Source["type"]?.GetValue<string>() ?? "manual";

    public static TriggerDefinition Load(string file)
    {
        try { return Parse(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file)); }
        catch (Exception ex) when (ex is not ConfigException) { throw new ConfigException($"{file}: {ex.Message}", ex); }
    }

    public static TriggerDefinition Parse(string yaml, string fallbackId)
    {
        // The source block stays free-form (plugin sources define their own settings); everything else is parsed strictly.
        Dictionary<object, object?> map = new YamlDotNet.Serialization.DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build()
            .Deserialize<Dictionary<object, object?>>(yaml) ?? [];
        map.Remove("source", out object? source);
        JsonArray? sinks = null;
        if (map.Remove("output", out object? output) && output is not null)
        {
            if (output is not Dictionary<object, object?> o || o.Keys.Any(k => k.ToString() != "sinks"))
                throw new ConfigException("output: may only contain sinks.");
            sinks = Yaml.ToJsonNode(o.GetValueOrDefault("sinks")) as JsonArray ?? throw new ConfigException("output.sinks must be a list.");
        }
        TriggerDefinition def = Yaml.Parse<TriggerDefinition>(new YamlDotNet.Serialization.SerializerBuilder().Build().Serialize(map));
        def.Source = Yaml.ToJsonNode(source) as JsonObject ?? new JsonObject { ["type"] = "manual" };
        def.Sinks = sinks is null ? null : Harness.Runs.Delivery.OutputDelivery.Normalize(sinks);
        if (string.IsNullOrEmpty(def.Id)) def.Id = fallbackId;
        return def;
    }
}

public sealed class TriggerFilter
{
    /// <summary>Sender allowlist; when set, everything else is dropped and logged.</summary>
    public List<string> Senders { get; set; } = [];
}

public sealed class TriggerApprovals
{
    /// <summary>How long to wait for an approval, for example <c>10m</c>. Unset means unattended: <c>ask</c> becomes <c>deny</c>.</summary>
    public string? Timeout { get; set; }
    public string OnTimeout { get; set; } = "deny";
}
