using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Harness.Core;
using Harness.Core.Agents;
using Harness.Core.Config;
using Harness.Core.Sessions;
using Harness.Extensions.Plugins;
using Harness.Runs;
using Harness.Runs.Delivery;
using Harness.Runs.Templates;
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
    private readonly WorkspaceBuilder _workspaces;
    private readonly SecretStore _secrets;
    private readonly PluginRegistry _plugins;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;
    private readonly Dictionary<string, Func<ITriggerSource>> _sourceFactories;
    private readonly ConcurrentDictionary<string, Func<HttpContext, Task>> _webhooks = new(StringComparer.Ordinal);
    private readonly List<(string TriggerId, ITriggerSource Source)> _started = [];
    private Dictionary<string, TriggerDefinition> _definitions = [];

    public TriggerEngine(ConfigCatalog catalog, TriggerQueue queue, RunOrchestrator runs, WorkspaceBuilder workspaces, SecretStore secrets, PluginRegistry plugins,
        IServiceProvider services, ILoggerFactory? loggerFactory = null)
    {
        _catalog = catalog;
        _queue = queue;
        _runs = runs;
        _workspaces = workspaces;
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
            ["run-completed"] = () => new RunCompletedSource(),
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

    /// <summary>The <c>run</c> sink: fires another trigger with a finished run's result. Chains are limited to <see cref="MaxChainDepth"/> runs.</summary>
    public async Task<RunRecord> FireFromRunAsync(string id, string? text, JsonObject? inputs, RunResult source, int depth, CancellationToken ct)
    {
        if (depth >= MaxChainDepth) throw new InvalidOperationException($"run chains stop after {MaxChainDepth} runs.");
        TriggerDefinition def = Get(id) ?? throw new KeyNotFoundException($"Trigger '{id}' not found.");
        JsonObject data = new() { ["inputs"] = inputs?.DeepClone(), ["chainDepth"] = depth + 1, ["run"] = RunData(source) };
        TriggerEvent evt = new($"run:{source.RunId}:{id}", def.Id, DateTimeOffset.UtcNow, "run:" + source.RunId, text, [], data, null);
        if (!_queue.TryEnqueue(evt)) throw new InvalidOperationException($"run {source.RunId} already fired trigger '{id}'.");
        return await ProcessAsync(evt, ct) ?? throw new InvalidOperationException($"Trigger '{id}' did not start a run (filtered or dropped by a hook).");
    }

    public const int MaxChainDepth = 5;

    /// <summary>The <c>run-completed</c> source: one event per finished upstream run, de-duplicated by run id.</summary>
    internal async Task EmitRunCompletedAsync(TriggerSourceContext context, RunResult result)
    {
        int depth = _queue.ChainDepth(result.RunId);
        if (depth >= MaxChainDepth)
        {
            _log.LogWarning("Trigger {Id}: run {RunId} is already {Depth} runs deep; run chains stop after {Max} runs", context.TriggerId, result.RunId, depth, MaxChainDepth);
            return;
        }
        JsonObject data = new() { ["chainDepth"] = depth + 1, ["run"] = RunData(result) };
        TriggerEvent evt = new($"run-completed:{result.RunId}", context.TriggerId, DateTimeOffset.UtcNow, "run:" + result.RunId,
            result.State == RunStates.Succeeded ? result.Text : result.Error ?? result.Text, [], data, result.ReplyTo);
        try { await EmitAsync(evt, CancellationToken.None); }
        catch (Exception ex) { _log.LogError(ex, "Trigger {Id}: run-completed event for run {RunId} failed", context.TriggerId, result.RunId); }
    }

    private static JsonObject RunData(RunResult run) => new()
    {
        ["id"] = run.RunId, ["sessionId"] = run.SessionId, ["triggerId"] = run.TriggerId, ["state"] = run.State, ["text"] = run.Text,
        ["error"] = run.Error, ["output"] = run.Output?.DeepClone(), ["files"] = new JsonArray([.. run.Files.Select(f => (JsonNode)f)]),
        ["inputTokens"] = run.InputTokens, ["outputTokens"] = run.OutputTokens,
    };

    /// <summary>The source instance that serves a trigger, when it can send replies on <paramref name="channel"/>.</summary>
    public IReplyChannel? ReplyChannel(string triggerId, string channel) =>
        _started.Where(s => s.TriggerId == triggerId).Select(s => s.Source).OfType<IReplyChannel>().FirstOrDefault(c => c.Channel == channel);

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
                    if (def.Template is { } template) _ = _catalog.Template(template);   // a missing or broken template is a load error
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
            bool internalSender = evt.Sender == "manual" || evt.Sender?.StartsWith("run:", StringComparison.Ordinal) == true;
            if (def.Filter.Senders.Count > 0 && !internalSender && (evt.Sender is null || !def.Filter.Senders.Contains(evt.Sender)))
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

            RunTemplate? template = def.Template is { } name ? _catalog.Template(name) : null;
            Dictionary<string, string> vars = TemplateVariables.ForEvent(evt, template is null ? [def.Inputs] : [template.Inputs, def.Inputs]);
            string prompt = TemplateVariables.Render(def.Prompt ?? template?.Prompt ?? "{event.text}", vars);
            if (prompt.Trim().Length == 0) prompt = $"Trigger {def.Id} fired.";

            string runId = Ids.NewRunId();
            SessionFolder session = SessionFor(def, template, evt, vars, runId);
            TimeSpan? approvalTimeout = def.Approvals.Timeout is { } t ? Durations.Parse(t) : null;
            RunRecord run = _runs.Start(new RunRequest
            {
                RunId = runId,
                SessionId = session.Id,
                Messages = [new ChatMessage(ChatRole.User, prompt)],
                Template = template is null ? null : new TemplateRun(template.Name, vars),
                Delivery = (def.Sinks ?? OutputDelivery.Normalize(template?.Output.Sinks)) is { Count: > 0 } sinks ? new DeliveryPlan(sinks, vars) : null,
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

    private SessionFolder SessionFor(TriggerDefinition def, RunTemplate? template, TriggerEvent evt, IReadOnlyDictionary<string, string> vars, string runId)
    {
        string? key = def.Session is { } keyTemplate ? TemplateVariables.Render(keyTemplate, vars) : null;
        if (key is not null && _runs.Sessions.FindByKey(key) is { } existing) return existing;
        string workspace = template is not null
            ? _workspaces.WorkspaceFor(runId)
            : def.Workspace is { } ws
                ? _catalog.Workspace(ws) ?? HarnessPaths.ExpandHome(ws)
                : Path.Combine(_catalog.Paths.RunsDir, "scratch", def.Id);
        Directory.CreateDirectory(workspace);
        return _runs.CreateSession(new SessionRequest
        {
            Agent = def.Agent ?? template?.Agent,
            Workspace = Path.GetFullPath(workspace),
            WorkspaceName = template is null && def.Workspace is { } name && _catalog.Workspace(name) is not null ? name : null,
            Title = $"{def.Id}: {evt.Text ?? evt.EventId}",
            Key = key,
            TriggerId = def.Id,
        });
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
