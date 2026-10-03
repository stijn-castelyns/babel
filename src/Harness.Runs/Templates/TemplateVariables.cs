using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Sdk;

namespace Harness.Runs.Templates;

/// <summary>
/// Placeholders in prompts, step settings, <c>*.tmpl</c> files and sink settings: <c>{event.text}</c>, <c>{event.sender}</c>,
/// <c>{event.id}</c>, <c>{event.data}</c>, <c>{event.data.a.b}</c>, <c>{inputs.&lt;name&gt;}</c>, <c>{trigger.id}</c>, <c>{run.id}</c>, <c>{date}</c>.
/// Unknown placeholders are left as they are, so braces in code survive rendering.
/// </summary>
public static partial class TemplateVariables
{
    [GeneratedRegex(@"\{([A-Za-z0-9_][A-Za-z0-9_.\-]*)\}")]
    private static partial Regex Placeholder();

    public static string Render(string text, IReadOnlyDictionary<string, string> variables) =>
        Placeholder().Replace(text, m => variables.TryGetValue(m.Groups[1].Value, out string? value) ? value : m.Value);

    /// <summary>Renders every string inside a JSON tree.</summary>
    public static JsonNode? Render(JsonNode? node, IReadOnlyDictionary<string, string> variables) => node switch
    {
        null => null,
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Render(kv.Value, variables)))),
        JsonArray a => new JsonArray([.. a.Select(n => Render(n, variables))]),
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => JsonValue.Create(Render(v.GetValue<string>(), variables)),
        _ => node.DeepClone(),
    };

    /// <summary>Adds <c>{prefix.a.b}</c> for every value inside <paramref name="node"/>; strings render bare, everything else as JSON.</summary>
    public static void Flatten(JsonNode? node, string prefix, Dictionary<string, string> vars)
    {
        if (node is not JsonObject obj) return;
        foreach ((string key, JsonNode? value) in obj)
        {
            string name = prefix + "." + key;
            vars[name] = value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : value?.ToJsonString() ?? "";
            Flatten(value, name, vars);
        }
    }

    /// <summary>
    /// The variables for one trigger event. Inputs come from the template's defaults, then the trigger's <c>inputs:</c>, then
    /// inputs given at fire time (<c>event.data.inputs</c>); input values may themselves use the event placeholders.
    /// </summary>
    public static Dictionary<string, string> ForEvent(TriggerEvent evt, IEnumerable<IReadOnlyDictionary<string, string>> inputLayers)
    {
        Dictionary<string, string> vars = new(StringComparer.Ordinal)
        {
            ["event.text"] = evt.Text ?? "",
            ["event.sender"] = evt.Sender ?? "",
            ["event.id"] = evt.EventId,
            ["event.data"] = evt.Data.ToJsonString(),
            ["trigger.id"] = evt.TriggerId,
            ["date"] = DateTimeOffset.Now.ToString("yyyy-MM-dd"),
            ["time"] = DateTimeOffset.Now.ToString("HHmmss"),
            ["chain.depth"] = evt.Data["chainDepth"]?.ToString() ?? "0",
        };
        Flatten(evt.Data, "event.data", vars);
        Dictionary<string, string> eventVars = new(vars);
        foreach (IReadOnlyDictionary<string, string> layer in inputLayers)
            foreach ((string name, string value) in layer)
                vars["inputs." + name] = Render(value, eventVars);
        if (evt.Data["inputs"] is JsonObject given)
            foreach ((string name, JsonNode? value) in given)
                vars["inputs." + name] = value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : value?.ToJsonString() ?? "";
        return vars;
    }
}

/// <summary>What a run needs to know about its template; kept with a parked run so it survives a restart.</summary>
public sealed record TemplateRun(string Name, IReadOnlyDictionary<string, string> Variables);
