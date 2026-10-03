using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Harness.Core;
using Harness.Core.Agents;
using Harness.Core.Config;
using Harness.Core.Sessions;
using Harness.Extensions.Plugins;
using Harness.Runs;
using Harness.Sdk;
using Harness.Triggers.Sources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Triggers;

/// <summary>
/// Loads trigger definitions, starts their sources, and pushes every event through one pipeline:
/// durable queue → de-duplication → sender filter → <c>OnTriggerFired</c> hooks → session → run.
/// </summary>
public sealed class TriggerEngine : IHostedService
{
    private readonly ConfigCatalog _catalog;
    private readonly TriggerQueue _queue;
    private readonly RunOrchestrator _runs;
    private readonly SecretStore _secrets;
    private readonly PluginRegistry _plugins;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;
    private readonly Dictionary<string, Func<ITriggerSource>> _sourceFactories;
    private readonly ConcurrentDictionary<string, Func<HttpContext, Task>> _webhooks = new(StringComparer.Ordinal);
    private readonly List<(string TriggerId, ITriggerSource Source)> _started = [];
    private Dictionary<string, TriggerDefinition> _definitions = [];

    public TriggerEngine(ConfigCatalog catalog, TriggerQueue queue, RunOrchestrator runs, SecretStore secrets, PluginRegistry plugins,
        IServiceProvider services, ILoggerFactory? loggerFactory = null)
    {
        _catalog = catalog;
        _queue = queue;
        _runs = runs;
        _secrets = secrets;
        _plugins = plugins;
        _services = services;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TriggerEngine>();
        _sourceFactories = new()
        {
            ["schedule"] = () => new ScheduleSource(queue),
            ["webhook"] = () => new WebhookSource(),
            ["file-watch"] = () => new FileWatchSource(),
            ["manual"] = () => new ManualSource(),
        };
        foreach (ITriggerSource source in plugins.TriggerSources())
        {
            ITriggerSource captured = source;
            _sourceFactories[source.Type] = () => captured;
        }
        _queue.EnsureStateTable();
    }

    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    public IReadOnlyCollection<TriggerDefinition> Definitions => _definitions.Values;

    public TriggerDefinition? Get(string id) => _definitions.GetValueOrDefault(id);

    public bool IsEnabled(TriggerDefinition def) => _queue.State(def.Id).Enabled ?? def.Enabled;

    public (DateTimeOffset? LastFiredAt, string? LastRunId) LastFire(string id)
    {
        var state = _queue.State(id);
        return (state.LastFiredAt, state.LastRunId);
    }

    public DateTimeOffset? NextFire(TriggerDefinition def)
    {
        if (def.SourceType != "schedule" || !IsEnabled(def)) return null;
        try { return ScheduleSource.NextFire(Resolve(def.Source), DateTimeOffset.UtcNow); }
        catch (Exception) { return null; }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LoadDefinitions();
        foreach (TriggerDefinition def in _definitions.Values.Where(IsEnabled))
            await StartSourceAsync(def, cancellationToken);
        // Events that arrived but never started a run (a crash or restart in between) are processed now.
        foreach (TriggerEvent pending in _queue.Pending())
            _ = ProcessAsync(pending, CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach ((_, ITriggerSource source) in _started)
        {
            try { await source.StopAsync(cancellationToken); }
            catch (Exception ex) { _log.LogWarning(ex, "Stopping trigger source {Type} failed", source.Type); }
        }
        _started.Clear();
        _webhooks.Clear();
    }

    /// <summary>Re-reads trigger files and restarts sources.</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        TriggerDefinition def = Get(id) ?? throw new KeyNotFoundException($"Trigger '{id}' not found.");
        _queue.SetEnabled(id, enabled);
        await ReloadAsync(ct);
        _log.LogInformation("Trigger {Id} {State}", def.Id, enabled ? "enabled" : "disabled");
    }

    /// <summary>Fires a trigger by hand (CLI, PWA). Works whatever the trigger's source type.</summary>
    public async Task<RunRecord> FireAsync(string id, string? text, JsonObject? inputs, CancellationToken ct)
    {
        TriggerDefinition def = Get(id) ?? throw new KeyNotFoundException($"Trigger '{id}' not found.");
        TriggerEvent evt = new($"manual:{Ids.New("")}", def.Id, DateTimeOffset.UtcNow, "manual", text, [], new JsonObject { ["inputs"] = inputs?.DeepClone() }, null);
        _queue.TryEnqueue(evt);
        return await ProcessAsync(evt, ct) ?? throw new InvalidOperationException($"Trigger '{id}' did not start a run (filtered or dropped by a hook).");
    }

    /// <summary>Dispatches a request on the webhook listener to the source that mapped its path.</summary>
    public async Task HandleWebhookAsync(string path, HttpContext http)
    {
        if (_webhooks.TryGetValue(path.Trim('/'), out Func<HttpContext, Task>? handler)) await handler(http);
        else http.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private void LoadDefinitions()
    {
        Dictionary<string, TriggerDefinition> defs = [];
        List<string> errors = [];
        string dir = _catalog.Paths.TriggersDir;
        if (Directory.Exists(dir))
            foreach (string file in Directory.EnumerateFiles(dir, "*.y*ml").Order(StringComparer.Ordinal))
            {
                try
                {
                    TriggerDefinition def = TriggerDefinition.Load(file);
                    if (!defs.TryAdd(def.Id, def)) errors.Add($"{file}: duplicate trigger id '{def.Id}'.");
                }
                catch (Exception ex) { errors.Add(ex.Message); }
            }
        foreach (string e in errors) _log.LogError("{Error}", e);
        LoadErrors = errors;
        _definitions = defs;
    }

    private async Task StartSourceAsync(TriggerDefinition def, CancellationToken ct)
    {
        if (!_sourceFactories.TryGetValue(def.SourceType, out Func<ITriggerSource>? factory))
        {
            _log.LogError("Trigger {Id}: no source of type {Type}", def.Id, def.SourceType);
            return;
        }
        ITriggerSource source = factory();
        try
        {
            await source.StartAsync(new SourceContext(this, def, Resolve(def.Source)), ct);
            _started.Add((def.Id, source));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Trigger {Id}: source {Type} failed to start", def.Id, def.SourceType);
        }
    }

    /// <summary>Resolves <c>secret:</c> and <c>env:</c> references in a source block.</summary>
    private JsonObject Resolve(JsonObject source)
    {
        JsonObject copy = (JsonObject)source.DeepClone();
        foreach ((string key, JsonNode? value) in copy.ToList())
            if (value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String && SecretStore.IsReference(v.GetValue<string>()))
                copy[key] = _secrets.Resolve(v.GetValue<string>());
        return copy;
    }

    internal async ValueTask EmitAsync(TriggerEvent evt, CancellationToken ct)
    {
        if (!_queue.TryEnqueue(evt))
        {
            _log.LogInformation("Trigger {Id}: duplicate event {EventId} ignored", evt.TriggerId, evt.EventId);
            return;
        }
        await ProcessAsync(evt, ct);
    }

    private async Task<RunRecord?> ProcessAsync(TriggerEvent evt, CancellationToken ct)
    {
        if (!_definitions.TryGetValue(evt.TriggerId, out TriggerDefinition? def))
        {
            _queue.Mark(evt, "dropped");
            return null;
        }
        try
        {
            if (def.Filter.Senders.Count > 0 && evt.Sender != "manual" && (evt.Sender is null || !def.Filter.Senders.Contains(evt.Sender)))
            {
                _log.LogWarning("Trigger {Id}: sender {Sender} is not allowed; event dropped", def.Id, evt.Sender ?? "(none)");
                _queue.Mark(evt, "filtered");
                return null;
            }

            TriggerFiredContext fired = new() { Event = evt };
            await new HookPipeline([.. _plugins.Plugins.SelectMany(p => p.Hooks)]).TriggerFiredAsync(fired, ct);
            if (fired.Dropped)
            {
                _log.LogInformation("Trigger {Id}: event dropped by hook: {Reason}", def.Id, fired.DropReason);
                _queue.Mark(evt, "dropped");
                return null;
            }
            evt = fired.Event;

            SessionFolder session = SessionFor(def, evt);
            TimeSpan? approvalTimeout = def.Approvals.Timeout is { } t ? Durations.Parse(t) : null;
            RunRecord run = _runs.Start(new RunRequest
            {
                SessionId = session.Id,
                Messages = [new ChatMessage(ChatRole.User, Render(def.Prompt, def, evt))],
                Interactive = false,
                TriggerId = def.Id,
                ReplyTo = evt.ReplyTo,
                AllowUnsandboxed = def.AllowUnsandboxed,
                ApprovalTimeout = approvalTimeout,
                OnApprovalTimeoutApprove = def.Approvals.OnTimeout == "approve",
            });
            _queue.Mark(evt, "started", run.Id);
            _queue.RecordFire(def.Id, evt.ReceivedAt, run.Id);
            return run;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Trigger {Id}: event {EventId} failed", def.Id, evt.EventId);
            _queue.Mark(evt, "failed");
            throw;
        }
    }

    private SessionFolder SessionFor(TriggerDefinition def, TriggerEvent evt)
    {
        string? key = def.Session is { } template ? Render(template, def, evt) : null;
        if (key is not null && _runs.Sessions.FindByKey(key) is { } existing) return existing;
        string workspace = def.Workspace is { } ws
            ? _catalog.Workspace(ws) ?? HarnessPaths.ExpandHome(ws)
            : Path.Combine(_catalog.Paths.RunsDir, "scratch", def.Id);
        Directory.CreateDirectory(workspace);
        return _runs.CreateSession(new SessionRequest
        {
            Agent = def.Agent,
            Workspace = Path.GetFullPath(workspace),
            WorkspaceName = def.Workspace is { } name && _catalog.Workspace(name) is not null ? name : null,
            Title = $"{def.Id}: {evt.Text ?? evt.EventId}",
            Key = key,
            TriggerId = def.Id,
        });
    }

    internal static string Render(string template, TriggerDefinition def, TriggerEvent evt)
    {
        string result = template
            .Replace("{event.text}", evt.Text ?? "", StringComparison.Ordinal)
            .Replace("{event.sender}", evt.Sender ?? "", StringComparison.Ordinal)
            .Replace("{event.id}", evt.EventId, StringComparison.Ordinal)
            .Replace("{event.data}", evt.Data.ToJsonString(), StringComparison.Ordinal)
            .Replace("{date}", DateTimeOffset.Now.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
        JsonObject? overrides = evt.Data["inputs"] as JsonObject;
        foreach ((string name, string value) in def.Inputs)
            result = result.Replace("{inputs." + name + "}", overrides?[name]?.ToString() ?? value, StringComparison.Ordinal);
        if (overrides is not null)
            foreach ((string name, JsonNode? value) in overrides)
                result = result.Replace("{inputs." + name + "}", value?.ToString() ?? "", StringComparison.Ordinal);
        return result.Trim().Length > 0 ? result : $"Trigger {def.Id} fired.";
    }

    private sealed class SourceContext(TriggerEngine engine, TriggerDefinition def, JsonObject settings) : TriggerSourceContext
    {
        public override string TriggerId => def.Id;
        public override JsonObject Settings => settings;
        public override IServiceProvider Services => engine._services;
        public override ValueTask EmitAsync(TriggerEvent triggerEvent, CancellationToken cancellationToken) => engine.EmitAsync(triggerEvent, cancellationToken);
        public override void MapWebhook(string path, Func<HttpContext, Task> handler) => engine._webhooks[path.Trim('/')] = handler;
    }

    /// <summary>A source that never emits on its own: the trigger only fires by hand.</summary>
    private sealed class ManualSource : ITriggerSource
    {
        public string Type => "manual";
        public Task StartAsync(TriggerSourceContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
