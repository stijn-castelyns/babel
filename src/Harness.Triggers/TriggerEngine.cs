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
        // A queued event whose run is still waiting (a reload, not a restart) is left to that run.
        foreach ((TriggerEvent pending, string? runId) in _queue.Pending())
            if (runId is null || _runs.Get(runId) is not { } run || RunStates.IsFinal(run.State))
                _ = ProcessAsync(pending, coalesce: true, CancellationToken.None);
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
        // Events waiting in a coalescing batch stay pending in the queue; the next start replays them.
        lock (_batchGate)
        {
            foreach (Batch batch in _batches.Values)
            {
                batch.Timer?.Cancel();
                foreach (Accepted a in batch.Events) _inFlight.TryRemove(Key(a.Queued), out _);
            }
            _batches.Clear();
        }
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
        return (await ProcessAsync(evt, coalesce: false, ct)).Require(id);
    }

    /// <summary>The <c>run</c> sink: fires another trigger with a finished run's result. Chains are limited to <see cref="MaxChainDepth"/> runs.</summary>
    public async Task<RunRecord> FireFromRunAsync(string id, string? text, JsonObject? inputs, RunResult source, int depth, CancellationToken ct)
    {
        if (depth >= MaxChainDepth) throw new InvalidOperationException($"run chains stop after {MaxChainDepth} runs.");
        TriggerDefinition def = Get(id) ?? throw new KeyNotFoundException($"Trigger '{id}' not found.");
        JsonObject data = new() { ["inputs"] = inputs?.DeepClone(), ["chainDepth"] = depth + 1, ["run"] = RunData(source) };
        TriggerEvent evt = new($"run:{source.RunId}:{id}", def.Id, DateTimeOffset.UtcNow, "run:" + source.RunId, text, [], data, null);
        if (!_queue.TryEnqueue(evt)) throw new InvalidOperationException($"run {source.RunId} already fired trigger '{id}'.");
        return (await ProcessAsync(evt, coalesce: false, ct)).Require(id);
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
        await ProcessAsync(evt, coalesce: true, ct);
    }

    /// <summary>How one event ended: the run it started (or joined, when coalesced), or why it did not.</summary>
    private sealed record Outcome(RunRecord? Run, string Status, string? Reason = null)
    {
        public RunRecord Require(string triggerId) =>
            Run ?? throw new InvalidOperationException($"Trigger '{triggerId}' did not start a run: {Reason ?? Status}.");
    }

    /// <summary>One accepted event: as it was queued (to mark it), and after <c>OnTriggerFired</c> hooks (to run it).</summary>
    private sealed record Accepted(TriggerEvent Queued, TriggerEvent Event);

    /// <summary>
    /// Events of one trigger and conversation that arrive within the trigger's <c>coalesce:</c> window, merged into one run.
    /// The window restarts with every message and is capped at <see cref="CoalesceCap"/> windows after the first.
    /// </summary>
    private sealed class Batch(TimeSpan window)
    {
        public List<Accepted> Events { get; } = [];
        public DateTimeOffset FirstAt { get; } = DateTimeOffset.UtcNow;
        public TimeSpan Window { get; } = window;
        public CancellationTokenSource? Timer { get; set; }
    }

    private const int CoalesceCap = 6;
    private readonly ConcurrentDictionary<string, TriggerSlots> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Batch> _batches = new(StringComparer.Ordinal);
    private readonly Lock _batchGate = new();
    /// <summary>Queued events this daemon is already working on, so a reload's replay of pending events cannot run them twice.</summary>
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    private static string Key(TriggerEvent evt) => evt.TriggerId + "\n" + evt.EventId;

    private async Task<Outcome> ProcessAsync(TriggerEvent evt, bool coalesce, CancellationToken ct)
    {
        if (!_inFlight.TryAdd(Key(evt), 0)) return new Outcome(null, "pending", "the event is already being processed");
        bool release = true;
        try
        {
            if (!_definitions.TryGetValue(evt.TriggerId, out TriggerDefinition? def))
            {
                _queue.Mark(evt, "dropped");
                return new Outcome(null, "dropped", "the trigger no longer exists");
            }

            Accepted accepted;
            try
            {
                (Outcome? refused, TriggerEvent hooked) = await AcceptAsync(def, evt, ct);
                if (refused is not null)
                {
                    _queue.Mark(evt, refused.Status);
                    return refused;
                }
                accepted = new Accepted(evt, hooked);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Trigger {Id}: event {EventId} failed", def.Id, evt.EventId);
                _queue.Mark(evt, "failed");
                throw;
            }

            if (coalesce && def.Coalesce is { } window)
            {
                AddToBatch(def, accepted, Durations.Parse(window));
                release = false;   // the batch owns the event until it starts its run
                return new Outcome(null, "pending", "coalescing");
            }
            return await StartAsync(def, [accepted], ct);
        }
        finally
        {
            if (release) _inFlight.TryRemove(Key(evt), out _);
        }
    }

    /// <summary>Sender filter and <c>OnTriggerFired</c> hooks. Returns why the event is refused, or the event as the hooks left it.</summary>
    private async Task<(Outcome? Refused, TriggerEvent Event)> AcceptAsync(TriggerDefinition def, TriggerEvent evt, CancellationToken ct)
    {
        bool internalSender = IsInternal(evt);
        if (def.Filter.Senders.Count > 0 && !internalSender && (evt.Sender is null || !def.Filter.Senders.Contains(evt.Sender)))
        {
            _log.LogWarning("Trigger {Id}: sender {Sender} is not allowed; event dropped", def.Id, evt.Sender ?? "(none)");
            return (new Outcome(null, "filtered", $"sender {evt.Sender ?? "(none)"} is not allowed"), evt);
        }

        TriggerFiredContext fired = new() { Event = evt };
        await new HookPipeline([.. _plugins.Plugins.SelectMany(p => p.Hooks)]).TriggerFiredAsync(fired, ct);
        if (fired.Dropped)
        {
            _log.LogInformation("Trigger {Id}: event dropped by hook: {Reason}", def.Id, fired.DropReason);
            return (new Outcome(null, "dropped", $"dropped by a hook: {fired.DropReason}"), evt);
        }
        return (null, fired.Event);
    }

    private static bool IsInternal(TriggerEvent evt) => evt.Sender == "manual" || evt.Sender?.StartsWith("run:", StringComparison.Ordinal) == true;

    private void AddToBatch(TriggerDefinition def, Accepted accepted, TimeSpan window)
    {
        // One batch per conversation: the session key when the trigger keeps one, otherwise the sender.
        string conversation = def.Session is { } keyTemplate
            ? TemplateVariables.Render(keyTemplate, TemplateVariables.ForEvent(accepted.Event, [def.Inputs]))
            : accepted.Event.Sender ?? "";
        string key = def.Id + "\n" + conversation;
        lock (_batchGate)
        {
            if (!_batches.TryGetValue(key, out Batch? batch)) _batches[key] = batch = new Batch(window);
            batch.Events.Add(accepted);
            batch.Timer?.Cancel();
            CancellationTokenSource timer = batch.Timer = new CancellationTokenSource();
            TimeSpan due = batch.FirstAt + window * CoalesceCap - DateTimeOffset.UtcNow;
            if (due > window) due = window;
            _ = Task.Delay(due > TimeSpan.Zero ? due : TimeSpan.Zero, timer.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return Task.CompletedTask;
                lock (_batchGate)
                {
                    if (!_batches.TryGetValue(key, out Batch? current) || current.Timer != timer) return Task.CompletedTask;
                    _batches.Remove(key);
                }
                return FlushAsync(def, batch);
            }, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task FlushAsync(TriggerDefinition def, Batch batch)
    {
        try { await StartAsync(def, batch.Events, CancellationToken.None); }
        catch (Exception ex) { _log.LogError(ex, "Trigger {Id}: coalesced events failed", def.Id); }
        finally
        {
            foreach (Accepted a in batch.Events) _inFlight.TryRemove(Key(a.Queued), out _);
        }
    }

    /// <summary>Several events in quick succession become one: texts joined in order, attachments combined, the last reply address.</summary>
    private static TriggerEvent Merge(IReadOnlyList<Accepted> events)
    {
        if (events.Count == 1) return events[0].Event;
        TriggerEvent first = events[0].Event, last = events[^1].Event;
        JsonObject data = (JsonObject)last.Data.DeepClone();
        data["coalesced"] = new JsonArray([.. events.Select(a => (JsonNode)new JsonObject
        {
            ["id"] = a.Event.EventId, ["sender"] = a.Event.Sender, ["text"] = a.Event.Text, ["receivedAt"] = a.Event.ReceivedAt.ToString("O"),
            ["data"] = a.Event.Data.DeepClone(),
        })]);
        return last with
        {
            ReceivedAt = first.ReceivedAt,
            Text = string.Join("\n", events.Select(a => a.Event.Text).Where(t => !string.IsNullOrEmpty(t))),
            Attachments = [.. events.SelectMany(a => a.Event.Attachments)],
            Data = data,
        };
    }

    /// <summary>Starts one run for accepted events (several when coalesced) and marks each of them with it.</summary>
    private Task<Outcome> StartAsync(TriggerDefinition def, IReadOnlyList<Accepted> events, CancellationToken ct)
    {
        TriggerEvent evt = Merge(events);
        try
        {
            RunTemplate? template = def.Template is { } name ? _catalog.Template(name) : null;
            Dictionary<string, string> vars = TemplateVariables.ForEvent(evt, template is null ? [def.Inputs] : [template.Inputs, def.Inputs]);
            string prompt = TemplateVariables.Render(def.Prompt ?? template?.Prompt ?? "{event.text}", vars);
            if (prompt.Trim().Length == 0) prompt = $"Trigger {def.Id} fired.";

            string? key = def.Session is { } keyTemplate ? TemplateVariables.Render(keyTemplate, vars) : null;
            string runId = Ids.NewRunId();
            TriggerSlots.Gate? gate = null;
            bool entered = false;
            if (def.Concurrency.Global is not null || (def.Concurrency.PerSession is not null && key is not null))
            {
                TriggerSlots slots = _slots.GetOrAdd(def.Id, id => new TriggerSlots(id));
                slots.Global = def.Concurrency.Global;
                slots.PerSession = def.Concurrency.PerSession;
                if (def.Concurrency.OnBusy == "drop")
                {
                    if (!slots.TryTake(key))
                    {
                        _log.LogWarning("Trigger {Id}: busy ({Active} runs active); event {EventId} dropped", def.Id, slots.Active, evt.EventId);
                        foreach (Accepted a in events) _queue.Mark(a.Queued, "busy");
                        return Task.FromResult(new Outcome(null, "busy", $"trigger '{def.Id}' is at its concurrency limit"));
                    }
                    entered = true;
                }
                gate = new TriggerSlots.Gate(slots, key, entered,
                    onEntered: () => { foreach (Accepted a in events) _queue.Mark(a.Queued, "started", runId); },
                    onAbandoned: () => { foreach (Accepted a in events) _queue.Mark(a.Queued, "cancelled", runId); });
                // Until the run gets its slot its events stay 'queued': if the daemon stops first, they run on the next start.
                if (!entered) foreach (Accepted a in events) _queue.Mark(a.Queued, "queued", runId);
            }

            SessionFolder session;
            RunRecord run;
            try
            {
                session = SessionFor(def, template, evt, vars, key, runId);
                TimeSpan? approvalTimeout = def.Approvals.Timeout is { } t ? Durations.Parse(t) : null;
                run = _runs.Start(new RunRequest
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
                    Gate = gate,
                });
            }
            catch
            {
                gate?.Exit();
                throw;
            }
            if (gate is null || entered) foreach (Accepted a in events) _queue.Mark(a.Queued, "started", run.Id);
            _queue.RecordFire(def.Id, evt.ReceivedAt, run.Id);
            return Task.FromResult(new Outcome(run, "started"));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Trigger {Id}: event {EventId} failed", def.Id, evt.EventId);
            foreach (Accepted a in events) _queue.Mark(a.Queued, "failed");
            throw;
        }
    }

    private SessionFolder SessionFor(TriggerDefinition def, RunTemplate? template, TriggerEvent evt, IReadOnlyDictionary<string, string> vars, string? key, string runId)
    {
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
