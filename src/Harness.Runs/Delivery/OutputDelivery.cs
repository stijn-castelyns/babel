using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core;
using Harness.Extensions.Plugins;
using Harness.Runs.Templates;
using Harness.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Runs.Delivery;

/// <summary>Where a run's result goes: the sinks from the trigger (or template) and the variables their settings are rendered with.</summary>
public sealed record DeliveryPlan(JsonArray Sinks, IReadOnlyDictionary<string, string> Variables);

/// <summary>
/// Delivers a run's result to its sinks once the run reached a final state. A sink runs for <c>succeeded</c> unless its
/// <c>on:</c> lists other states (<c>invalid_output</c>, <c>failed</c>); timeouts, cancellations and denied approvals never deliver.
/// </summary>
public sealed class OutputDelivery(IServiceProvider services, PluginRegistry plugins, SecretStore secrets)
{
    private static readonly string[] Deliverable = [RunStates.Succeeded, RunStates.InvalidOutput, RunStates.Failed];

    /// <summary>A bare string such as <c>reply</c> means <c>{ type: reply }</c>.</summary>
    public static JsonArray Normalize(JsonArray? sinks) =>
        [.. (sinks ?? []).Select(s => s switch
        {
            JsonValue v => new JsonObject { ["type"] = v.ToString() },
            JsonObject o when o["type"] is JsonValue => o.DeepClone(),
            _ => throw new Core.Config.ConfigException($"Each output sink needs a type: {s?.ToJsonString() ?? "null"}."),
        })];

    public static bool Applies(JsonObject sink, string state)
    {
        if (!Deliverable.Contains(state)) return false;
        return sink["on"] switch
        {
            JsonArray states => states.Any(s => s?.ToString() == state),
            JsonValue one => one.ToString() == state,
            _ => state == RunStates.Succeeded,
        };
    }

    public IReadOnlyDictionary<string, IOutputSink> Sinks()
    {
        Dictionary<string, IOutputSink> sinks = new(StringComparer.Ordinal);
        foreach (IOutputSink sink in services.GetServices<IOutputSink>()) sinks[sink.Type] = sink;
        foreach ((string type, IOutputSink sink) in plugins.OutputSinks()) sinks.TryAdd(type, sink);
        return sinks;
    }

    /// <summary>Delivers to every applicable sink and returns one message per failed sink.</summary>
    public async Task<IReadOnlyList<string>> DeliverAsync(RunResult result, DeliveryPlan plan, Action<JsonObject> emit, CancellationToken ct)
    {
        IReadOnlyDictionary<string, IOutputSink> available = Sinks();
        Dictionary<string, string> vars = Variables(result, plan.Variables);
        List<string> errors = [];
        int index = 0;
        foreach (JsonObject sink in plan.Sinks.OfType<JsonObject>())
        {
            index++;
            if (!Applies(sink, result.State)) continue;
            string type = sink["type"]!.ToString();
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                if (!available.TryGetValue(type, out IOutputSink? target))
                    throw new InvalidOperationException($"no output sink of type '{type}'. Available: {string.Join(", ", available.Keys.Order())}.");
                JsonObject settings = ResolveSecrets((JsonObject)TemplateVariables.Render(sink, vars)!);
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(settings["timeoutSeconds"] is JsonValue t ? double.Parse(t.ToString(), System.Globalization.CultureInfo.InvariantCulture) : 60));
                await target.DeliverAsync(result, new SinkOptions(settings, vars), timeout.Token);
                emit(new JsonObject { ["index"] = index, ["type"] = type, ["ok"] = true, ["elapsedMs"] = clock.ElapsedMilliseconds });
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                string message = ex is OperationCanceledException ? "timed out" : ex.Message;
                errors.Add($"{type}: {message}");
                emit(new JsonObject { ["index"] = index, ["type"] = type, ["ok"] = false, ["error"] = message, ["elapsedMs"] = clock.ElapsedMilliseconds });
            }
        }
        return errors;
    }

    /// <summary>The run's variables plus <c>{run.id}</c>, <c>{state}</c>, <c>{text}</c>, <c>{error}</c>, <c>{output}</c> and <c>{output.a.b}</c>.</summary>
    public static Dictionary<string, string> Variables(RunResult result, IReadOnlyDictionary<string, string> variables)
    {
        Dictionary<string, string> vars = new(variables, StringComparer.Ordinal)
        {
            ["run.id"] = result.RunId,
            ["session.id"] = result.SessionId,
            ["state"] = result.State,
            ["text"] = result.Text ?? "",
            ["error"] = result.Error ?? "",
            ["date"] = DateTimeOffset.Now.ToString("yyyy-MM-dd"),
            ["output"] = result.Output?.ToJsonString() ?? "",
        };
        TemplateVariables.Flatten(result.Output, "output", vars);
        return vars;
    }

    private JsonObject ResolveSecrets(JsonObject settings)
    {
        JsonNode? Resolve(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Resolve(kv.Value)))),
            JsonArray a => new JsonArray([.. a.Select(Resolve)]),
            JsonValue v when v.GetValueKind() == JsonValueKind.String && SecretStore.IsReference(v.GetValue<string>()) => secrets.Resolve(v.GetValue<string>()),
            _ => node?.DeepClone(),
        };
        return (JsonObject)Resolve(settings)!;
    }
}

/// <summary>Picks what a sink sends: <c>from: text | output | output.a.b | error | file:&lt;name&gt;</c>.</summary>
public static class SinkContent
{
    public static string Select(RunResult result, string? from)
    {
        static string Pretty(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>()
            : node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "";

        switch (from)
        {
            case null or "":
                return result.Output is not null ? Pretty(result.Output) : result.Text ?? result.Error ?? "";
            case "text": return result.Text ?? "";
            case "error": return result.Error ?? "";
            case "output": return Pretty(result.Output);
            case var f when f.StartsWith("output.", StringComparison.Ordinal):
                JsonNode? node = result.Output;
                foreach (string part in f["output.".Length..].Split('.')) node = node is JsonObject o ? o[part] : null;
                return node is null ? throw new InvalidOperationException($"the output has no '{f["output.".Length..]}'.") : Pretty(node);
            case var f when f.StartsWith("file:", StringComparison.Ordinal):
                string name = f[5..].Replace('\\', '/');
                string? file = result.Files.FirstOrDefault(p => p.Replace('\\', '/').EndsWith("/files/" + name, StringComparison.Ordinal));
                return file is null ? throw new InvalidOperationException($"the run produced no file '{name}'.") : File.ReadAllText(file);
            default:
                throw new InvalidOperationException($"unknown 'from: {from}'; use text, output, output.<field>, error or file:<name>.");
        }
    }
}
