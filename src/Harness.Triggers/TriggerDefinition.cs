using System.Text.Json.Nodes;
using Harness.Core;
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
    /// <summary>
    /// Merge events of one conversation (the session key, or the sender) that arrive within this window into one run, for
    /// example <c>5s</c>. The window restarts with every event, up to six windows after the first.
    /// </summary>
    public string? Coalesce { get; set; }
    public TriggerConcurrency Concurrency { get; set; } = new();
    public TriggerRateLimit RateLimit { get; set; } = new();

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
        def.Validate();
        return def;
    }

    private void Validate()
    {
        static TimeSpan Duration(string text, string field)
        {
            try { return Durations.Parse(text); }
            catch (FormatException) { throw new ConfigException($"{field}: '{text}' is not a duration such as 5s, 10m or 1h."); }
        }
        if (Coalesce is { } coalesce && Duration(coalesce, "coalesce") <= TimeSpan.Zero) throw new ConfigException("coalesce must be positive.");
        if (Approvals.Timeout is { } timeout) Duration(timeout, "approvals.timeout");
        if (Concurrency.Global is < 1 || Concurrency.PerSession is < 1) throw new ConfigException("concurrency limits must be at least 1.");
        if (RateLimit.PerSender is { } perSender) _ = TriggerRateLimit.Parse(perSender);
        if (Concurrency.OnBusy is not ("queue" or "drop")) throw new ConfigException($"concurrency.onBusy must be queue or drop, not '{Concurrency.OnBusy}'.");
    }
}

public sealed class TriggerFilter
{
    /// <summary>Sender allowlist; when set, everything else is dropped and logged.</summary>
    public List<string> Senders { get; set; } = [];
}

/// <summary><c>rateLimit: { perSender: 10/1h }</c>.</summary>
public sealed class TriggerRateLimit
{
    /// <summary>
    /// At most this many events per sender in a sliding window, written <c>count/window</c> (<c>10/1h</c>, <c>3/m</c>). Events
    /// over the limit are dropped and logged (status <c>rate_limited</c>). Manual fires and chained runs are never limited.
    /// </summary>
    public string? PerSender { get; set; }

    public static (int Count, TimeSpan Window) Parse(string text)
    {
        string[] parts = text.Split('/', 2, StringSplitOptions.TrimEntries);
        TimeSpan window = default;
        bool ok = parts.Length == 2 && int.TryParse(parts[0], out int count) && count > 0 && TryDuration(parts[1], out window) && window > TimeSpan.Zero;
        if (!ok) throw new ConfigException($"rateLimit.perSender: '{text}' is not a limit such as 10/1h or 3/m.");
        return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), window);
    }

    private static bool TryDuration(string text, out TimeSpan window)
    {
        try { window = Durations.Parse(char.IsDigit(text.FirstOrDefault()) ? text : "1" + text); return true; }
        catch (Exception ex) when (ex is FormatException or OverflowException) { window = default; return false; }
    }
}

/// <summary><c>concurrency: { perSession: 1, global: 2, onBusy: queue }</c>.</summary>
public sealed class TriggerConcurrency
{
    /// <summary>Runs of this trigger per session key at once (only for triggers with <c>session:</c>).</summary>
    public int? PerSession { get; set; }
    /// <summary>Runs of this trigger at once, across all sessions.</summary>
    public int? Global { get; set; }
    /// <summary>
    /// <c>queue</c> (default): the run waits in the <c>queued</c> state until a slot frees up. <c>drop</c>: the event is dropped
    /// and logged (status <c>busy</c>).
    /// </summary>
    public string OnBusy { get; set; } = "queue";
}

public sealed class TriggerApprovals
{
    /// <summary>How long to wait for an approval, for example <c>10m</c>. Unset means unattended: <c>ask</c> becomes <c>deny</c>.</summary>
    public string? Timeout { get; set; }
    public string OnTimeout { get; set; } = "deny";
}
