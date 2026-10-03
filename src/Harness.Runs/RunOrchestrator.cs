using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core;
using Harness.Core.Agents;
using Harness.Core.Config;
using Harness.Core.Sessions;
using Harness.Extensions.Skills;
using Harness.Runs.Templates;
using Harness.Sandbox;
using Harness.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Runs;

/// <summary>
/// Executes every run, interactive or triggered, and publishes every step as an event.
/// States: queued → preparing → running ⇄ awaiting_approval → (validating → delivering →) a final state.
/// </summary>
public sealed class RunOrchestrator : IAsyncDisposable
{
    private readonly ConfigCatalog _catalog;
    private readonly SessionStore _sessions;
    private readonly AgentFactory _agents;
    private readonly SandboxFactory _sandboxes;
    private readonly SkillsExtension _skills;
    private readonly SecretStore _secrets;
    private readonly TrustStore _trust;
    private readonly SessionRuntimeRegistry _runtime;
    private readonly EventHub _hub;
    private readonly ApprovalBroker _approvals;
    private readonly ApprovalStore _store;
    private readonly WorkspaceBuilder _workspaces;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    private readonly ConcurrentDictionary<string, ActiveRun> _active = new();
    /// <summary>Runs rehydrated after a restart that wait for approvals with no live task behind them.</summary>
    private readonly ConcurrentDictionary<string, ParkedRequest> _parked = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelLocks = new();
    private readonly SemaphoreSlim _global;
    private readonly CancellationTokenSource _shutdown = new();

    public RunOrchestrator(ConfigCatalog catalog, SessionStore sessions, AgentFactory agents, SandboxFactory sandboxes, SkillsExtension skills,
        SecretStore secrets, TrustStore trust, SessionRuntimeRegistry runtime, EventHub hub, ApprovalBroker approvals, ApprovalStore store, WorkspaceBuilder workspaces, IServiceProvider services,
        ILoggerFactory? loggerFactory = null)
    {
        _catalog = catalog;
        _sessions = sessions;
        _agents = agents;
        _sandboxes = sandboxes;
        _skills = skills;
        _secrets = secrets;
        _trust = trust;
        _runtime = runtime;
        _hub = hub;
        _approvals = approvals;
        _store = store;
        _workspaces = workspaces;
        _services = services;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RunOrchestrator>();
        _global = new SemaphoreSlim(Math.Max(1, catalog.Config.Runs.GlobalConcurrency));
    }

    public EventHub Events => _hub;
    public ApprovalBroker Approvals => _approvals;
    public SessionStore Sessions => _sessions;

    private sealed class ActiveRun(RunRecord record, RunRequest request, CancellationTokenSource cts)
    {
        public RunRecord Record { get; } = record;
        public RunRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = cts;
        public TaskCompletionSource<RunResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TimedOut { get; set; }
    }

    // ---------------------------------------------------------------- sessions

    public SessionFolder CreateSession(SessionRequest request)
    {
        string agentName = request.Agent ?? _catalog.Config.Defaults.Agent;
        AgentDefinition agent = _catalog.Agent(agentName);
        string workspace = request.WorkspaceName is { } name
            ? _catalog.Workspace(name) ?? throw new ConfigException($"Workspace '{name}' is not registered. Add it with 'harness workspace add'.")
            : request.Workspace ?? throw new ArgumentException("A workspace path or name is required.");
        workspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(workspace)) throw new DirectoryNotFoundException($"Workspace '{workspace}' does not exist.");
        string? workdir = request.WorkingDirectory is { } wd ? Path.GetFullPath(Path.Combine(workspace, wd)) : null;
        if (workdir is not null && !(workdir == workspace || workdir.StartsWith(workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new ArgumentException("The working directory must be inside the workspace.");

        string model = request.Model ?? agent.Model;
        _ = _catalog.Model(model);
        return _sessions.Create(new SessionInfo
        {
            Agent = agent.Name,
            Model = model,
            Workspace = workspace,
            WorkspaceName = request.WorkspaceName,
            WorkingDirectory = workdir,
            Title = request.Title,
            Key = request.Key,
            TriggerId = request.TriggerId,
        });
    }

    // ---------------------------------------------------------------- runs

    /// <summary>Queues a run and returns its record immediately. The run executes in the background.</summary>
    public RunRecord Start(RunRequest request)
    {
        SessionFolder session = _sessions.Open(request.SessionId);
        RunRecord record = new()
        {
            Id = request.RunId ?? Ids.NewRunId(),
            SessionId = session.Id,
            Agent = session.Info.Agent,
            Model = session.Info.Model,
            TriggerId = request.TriggerId,
            State = RunStates.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (session.Info.Title is null && request.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text is { Length: > 0 } first)
        {
            session.Info.Title = first.Length <= 60 ? first.ReplaceLineEndings(" ") : first[..57].ReplaceLineEndings(" ") + "…";
            session.Save();
        }
        _sessions.Index.UpsertRun(record);
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        ActiveRun active = new(record, request, cts);
        _active[record.Id] = active;
        Emit(session, record.Id, EventTypes.RunStarted, new JsonObject
        {
            ["agent"] = record.Agent,
            ["model"] = record.Model,
            ["triggerId"] = record.TriggerId,
            ["interactive"] = request.Interactive,
            ["input"] = string.Join("\n", request.Messages.Select(m => m.Text)),
        });
        _ = Task.Run(() => ExecuteAsync(active, session));
        return record;
    }

    /// <summary>
    /// Waits for a run to end. A run parked on approvals across a restart is waited for until it resumes and finishes.
    /// When the daemon stops while a run waits for approval, the result carries the non-final state <c>awaiting_approval</c>.
    /// </summary>
    public async Task<RunResult> WaitAsync(string runId, CancellationToken ct = default)
    {
        while (true)
        {
            if (_active.TryGetValue(runId, out ActiveRun? run)) return await run.Completion.Task.WaitAsync(ct);
            if (_sessions.Index.GetRun(runId) is { } done && RunStates.IsFinal(done.State))
                return new RunResult { RunId = done.Id, SessionId = done.SessionId, State = done.State, Text = done.ResultText, Error = done.Error, InputTokens = done.InputTokens, OutputTokens = done.OutputTokens };
            if (!_parked.ContainsKey(runId)) throw new KeyNotFoundException($"Run '{runId}' not found.");
            await Task.Delay(100, ct);
        }
    }

    public bool Cancel(string runId)
    {
        if (_active.TryGetValue(runId, out ActiveRun? run))
        {
            run.Cancellation.Cancel();
            _approvals.CancelRun(runId);
            return true;
        }
        if (!_parked.TryRemove(runId, out ParkedRequest? parked)) return false;
        _approvals.CancelRun(runId);
        FinishParked(runId, RunStates.Cancelled, "Cancelled.", parked.Template);
        return true;
    }

    public RunRecord? Get(string runId) => _active.TryGetValue(runId, out ActiveRun? a) ? a.Record : _sessions.Index.GetRun(runId);

    public IReadOnlyList<RunRecord> ActiveRuns() =>
        [.. _active.Values.Select(a => a.Record).Concat(_parked.Keys.Select(_sessions.Index.GetRun).OfType<RunRecord>()).OrderBy(r => r.CreatedAt)];

    public bool ResolveApproval(string runId, string requestId, ApprovalAnswer answer) => _approvals.Resolve(runId, requestId, answer);

    private async Task ExecuteAsync(ActiveRun active, SessionFolder session)
    {
        RunRecord record = active.Record;
        CancellationToken ct = active.Cancellation.Token;
        RunResult result;
        SemaphoreSlim sessionLock = _sessionLocks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1));
        bool haveSession = false, haveModel = false, haveGlobal = false;
        SemaphoreSlim? modelLock = null;
        try
        {
            await sessionLock.WaitAsync(ct);
            haveSession = true;
            ModelProfile profile = _catalog.Model(session.Info.Model);
            modelLock = _modelLocks.GetOrAdd(session.Info.Model, _ => new SemaphoreSlim(Math.Max(1, profile.MaxConcurrency ?? 8)));
            await modelLock.WaitAsync(ct);
            haveModel = true;
            await _global.WaitAsync(ct);
            haveGlobal = true;
            result = await RunCoreAsync(active, session, profile, ct);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested && record.State == RunStates.AwaitingApproval && _store.IsParked(record.Id))
        {
            // The daemon is stopping while the run waits for a person: leave it parked so it resumes after the restart.
            result = Final(active, RunStates.AwaitingApproval, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = Final(active, active.TimedOut ? RunStates.TimedOut : RunStates.Cancelled, null, active.TimedOut ? "The run exceeded its time limit." : "Cancelled.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Run {RunId} failed", record.Id);
            Emit(session, record.Id, EventTypes.RunError, new JsonObject { ["message"] = ex.Message, ["kind"] = ex.GetType().Name });
            result = Final(active, RunStates.Failed, null, ex.Message);
        }
        finally
        {
            if (haveGlobal) _global.Release();
            if (haveModel) modelLock!.Release();
            if (haveSession) sessionLock.Release();
        }

        if (result.State == RunStates.AwaitingApproval)
        {
            _active.TryRemove(record.Id, out _);
            active.Completion.TrySetResult(result);
            return;
        }

        _store.Unpark(record.Id);
        CleanupWorkspace(active.Request.Template, record.Id, result.State);
        record.State = result.State;
        record.FinishedAt = DateTimeOffset.UtcNow;
        record.Error = result.Error;
        record.ResultText = result.Text;
        record.InputTokens = result.InputTokens;
        record.OutputTokens = result.OutputTokens;
        _sessions.Index.UpsertRun(record);
        Emit(session, record.Id, EventTypes.RunFinished, new JsonObject
        {
            ["state"] = result.State,
            ["error"] = result.Error,
            ["text"] = result.Text,
            ["inputTokens"] = result.InputTokens,
            ["outputTokens"] = result.OutputTokens,
            ["elapsedMs"] = (long)(record.FinishedAt.Value - (record.StartedAt ?? record.CreatedAt)).TotalMilliseconds,
        });
        _active.TryRemove(record.Id, out _);
        active.Cancellation.Dispose();
        active.Completion.TrySetResult(result);
    }

    private async Task<RunResult> RunCoreAsync(ActiveRun active, SessionFolder session, ModelProfile profile, CancellationToken outer)
    {
        RunRecord record = active.Record;
        RunRequest request = active.Request;
        SetState(session, record, RunStates.Preparing);
        record.StartedAt ??= DateTimeOffset.UtcNow;

        // A templated run gets a fresh workspace under runs/<run-id>/workspace; a keyed session follows it from run to run.
        RunTemplate? template = request.Template is { } templateRun ? _catalog.Template(templateRun.Name) : null;
        if (template is not null)
        {
            string runWorkspace = _workspaces.WorkspaceFor(record.Id);
            Directory.CreateDirectory(runWorkspace);
            if (session.Info.Workspace != runWorkspace)
            {
                session.Info.Workspace = runWorkspace;
                session.Info.WorkingDirectory = null;
                session.Save();
            }
        }
        string workspace = session.Info.Workspace;
        AgentDefinition baseAgent = _catalog.Agent(session.Info.Agent);
        string sandboxName = template?.Sandbox ?? baseAgent.Sandbox;
        int maxMinutes = template?.Limits.MaxRunMinutes ?? baseAgent.Limits.MaxRunMinutes;

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(outer);
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        limit.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, maxMinutes)));
        limit.Token.Register(() => { if (!outer.IsCancellationRequested) active.TimedOut = true; });
        CancellationToken ct = limit.Token;
        using IDisposable lease = session.AcquireLease(record.Id);

        // Workspace steps run before any model call; a failing step fails the run. A resumed run already has its workspace.
        if (template is not null && !request.Resumed)
        {
            SandboxSpec setup = RequireSandbox(_sandboxes.Resolve(sandboxName, workspace), request, baseAgent.Name);
            await _workspaces.BuildAsync(template, record.Id, workspace, request.Template!.Variables, setup, (type, data) => Emit(session, record.Id, type, data), ct);
        }

        (AgentDefinition agent, IReadOnlyList<string> ignored) = EffectiveAgent.Resolve(baseAgent, workspace, _trust);
        if (template is not null)
        {
            // The template decides the sandbox and limits; folder config in a cloned repository cannot change them.
            agent.Sandbox = sandboxName;
            agent.Limits.MaxRunMinutes = maxMinutes;
            if (template.Limits.MaxTokens is long maxTokens) agent.Limits.MaxTokens = maxTokens;
            if (template.Limits.MaxToolIterations is int maxTools) agent.Limits.MaxToolIterations = maxTools;
        }
        if (ignored.Count > 0)
            Emit(session, record.Id, EventTypes.RunState, new JsonObject
            {
                ["state"] = RunStates.Preparing,
                ["notice"] = $"Ignored untrusted folder config: {string.Join("; ", ignored)}. Run 'harness trust {workspace}' to apply it.",
            });

        SandboxSpec spec = RequireSandbox(_sandboxes.Resolve(agent.Sandbox, workspace), request, agent.Name);
        // Skill directories are mounted read-only so skill scripts can run inside the sandbox.
        IReadOnlyList<string> skillDirs = _skills.SkillDirectories(agent);
        spec = spec with { Mounts = [.. spec.Mounts, .. skillDirs.Select((d, i) => new MountSpec(d, $"/harness/skills/{i}", MountMode.ReadOnly))] };
        if (spec.Limits.WallClockMinutes is int wallClock && wallClock < maxMinutes)
            limit.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, wallClock)) - elapsed.Elapsed);

        await using ISandbox sandbox = await _sandboxes.CreateAsync(spec, ct);
        session.Info.Status = "running";
        session.Save();

        SessionRuntimeState state = _runtime.Get(session.Id);
        state.NextTurn();
        RunEvents events = new(this, session, record);
        RunContext run = new()
        {
            RunId = record.Id,
            Session = session,
            Agent = agent,
            Model = profile,
            WorkspaceRoot = session.Info.Workspace,
            WorkingDirectory = session.Info.WorkingDirectory ?? session.Info.Workspace,
            Sandbox = sandbox,
            Services = _services,
            Events = events,
            State = state,
            Interactive = request.Interactive,
            RunInstructions = JoinInstructions(request.RunInstructions, template?.Instructions),
            TriggerId = request.TriggerId,
            SecretValues = _secrets.Values(),
            SpillThresholdChars = _catalog.Config.Tools.SpillThresholdChars,
            FolderInstructionFiles = _catalog.Config.Prompts.FolderFiles,
        };

        // A resumed run continues the usage it had before it was parked; the session already counted that part.
        long priorInput = request.Resumed ? record.InputTokens : 0, priorOutput = request.Resumed ? record.OutputTokens : 0;
        run.AddUsage(priorInput, priorOutput);

        BuiltAgent built = await _agents.BuildAsync(run, [], ct);
        try
        {
            RunStartingContext starting = new()
            {
                RunId = run.RunId, SessionId = run.SessionId, AgentName = run.AgentName, WorkspaceRoot = run.WorkspaceRoot,
                Messages = [.. request.Messages],
            };
            if (request.Resumed)
                Emit(session, record.Id, EventTypes.RunState, new JsonObject { ["state"] = RunStates.Running, ["notice"] = "Resumed with the approval answers." });
            else
                await built.Hooks.RunStartingAsync(starting, ct);
            if (starting.Cancelled)
                return Final(active, RunStates.Cancelled, null, $"Cancelled by hook: {starting.CancelReason}", run);

            AgentSession agentSession = session.Info.AgentState is JsonElement saved
                ? await built.Agent.DeserializeSessionAsync(saved, cancellationToken: ct)
                : await built.Agent.CreateSessionAsync(ct);

            AgentRunOptions? options = request.ExtraTools.Count > 0
                ? new ChatClientAgentRunOptions(new ChatOptions { Tools = [.. request.ExtraTools] })
                : null;

            IEnumerable<ChatMessage> next = starting.Messages;
            string lastText = "";
            bool rejected = false;
            while (true)
            {
                SetState(session, record, RunStates.Running);
                (string text, List<ToolApprovalRequestContent> approvalRequests) = await StreamTurnAsync(built.Agent, next, agentSession, options, run, ct);
                if (text.Length > 0) lastText = text;
                if (approvalRequests.Count == 0) break;

                // The whole batch is stored before anything is decided, so a restart mid-batch loses neither requests nor answers.
                // The agent session is saved too: Agent Framework binds approval responses to requests kept in its state.
                session.Info.AgentState = await built.Agent.SerializeSessionAsync(agentSession, cancellationToken: ct);
                session.Save();
                _store.Park(record.Id, session.Id, ParkedRequest.From(request));
                foreach (ToolApprovalRequestContent req in approvalRequests)
                {
                    ApprovalInfo info = Describe(req);
                    _store.Add(new StoredApproval(req.RequestId, record.Id, session.Id, info.ToolName, info.CallId, info.ArgumentsJson, info.Summary, DateTimeOffset.UtcNow, null));
                }
                SetState(session, record, RunStates.AwaitingApproval);
                List<AIContent> responses = [];
                foreach (ToolApprovalRequestContent req in approvalRequests)
                {
                    ApprovalAnswer answer = await DecideAsync(req, run, built.Hooks, request, ct);
                    _store.Decide(req.RequestId, answer);
                    if (!answer.Approved && !request.Interactive) rejected = true;
                    responses.Add(answer.Approved ? req.CreateResponse(true, answer.Reason) : req.CreateResponse(false, answer.Reason ?? "Denied."));
                }
                _store.Unpark(record.Id);
                if (rejected) break;
                next = [new ChatMessage(ChatRole.User, responses)];
            }

            session.Info.AgentState = await built.Agent.SerializeSessionAsync(agentSession, cancellationToken: ct);
            string finalState = rejected ? RunStates.Rejected : RunStates.Succeeded;
            RunResult result = Final(active, finalState, lastText, rejected ? "An approval was denied." : null, run);

            RunCompletedContext completed = new()
            {
                RunId = run.RunId, SessionId = run.SessionId, AgentName = run.AgentName, WorkspaceRoot = run.WorkspaceRoot, Result = result,
            };
            await built.Hooks.RunCompletedAsync(completed, ct);
            return completed.Result;
        }
        finally
        {
            session.Info.Status = "idle";
            session.Info.InputTokens += run.InputTokens - priorInput;
            session.Info.OutputTokens += run.OutputTokens - priorOutput;
            session.Save();
            record.InputTokens = run.InputTokens;
            record.OutputTokens = run.OutputTokens;
            foreach (IRunExtension ext in built.Extensions)
            {
                try { await ext.ReleaseAsync(run); }
                catch (Exception ex) { _log.LogWarning(ex, "Releasing {Extension} for run {RunId} failed", ext.GetType().Name, run.RunId); }
            }
        }
    }

    /// <summary>Streams one agent invocation, emitting text events, and returns the final text and any approval requests.</summary>
    private static async Task<(string Text, List<ToolApprovalRequestContent> Approvals)> StreamTurnAsync(
        AIAgent agent, IEnumerable<ChatMessage> messages, AgentSession agentSession, AgentRunOptions? options, RunContext run, CancellationToken ct)
    {
        List<ToolApprovalRequestContent> approvals = [];
        StringBuilder current = new();
        string? messageId = null;
        string lastText = "";

        void EndMessage()
        {
            if (messageId is null) return;
            string text = current.ToString();
            run.Events.Emit(EventTypes.TextMessageEnd, new JsonObject { ["messageId"] = messageId, ["role"] = "assistant", ["text"] = run.Mask(text) });
            if (text.Trim().Length > 0) lastText = text;
            current.Clear();
            messageId = null;
        }

        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, agentSession, options, ct))
        {
            foreach (AIContent content in update.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } t:
                        string id = update.MessageId ?? messageId ?? Ids.New("m_");
                        if (messageId is not null && id != messageId) EndMessage();
                        if (messageId is null)
                        {
                            messageId = id;
                            run.Events.Emit(EventTypes.TextMessageStart, new JsonObject { ["messageId"] = id, ["role"] = "assistant" });
                        }
                        current.Append(t.Text);
                        run.Events.Emit(EventTypes.TextMessageContent, new JsonObject { ["messageId"] = id, ["delta"] = run.Mask(t.Text) });
                        break;
                    case FunctionCallContent:
                        EndMessage();   // text before a tool call is its own message
                        break;
                    case ToolApprovalRequestContent req:
                        approvals.Add(req);
                        break;
                }
            }
        }
        EndMessage();
        return (lastText, approvals);
    }

    /// <summary>
    /// Decides one approval: session "always" grants, the agent's allowlist and hooks first, then a person.
    /// Unattended runs without an approver deny.
    /// </summary>
    private async Task<ApprovalAnswer> DecideAsync(ToolApprovalRequestContent req, RunContext run, HookPipeline hooks, RunRequest request, CancellationToken ct)
    {
        (string toolName, string callId, IReadOnlyDictionary<string, object?> args, JsonObject argsJson, string? summary) = Describe(req);
        string key = ApprovalPolicy.InvocationKey(toolName, args);

        JsonObject requested = new()
        {
            ["requestId"] = req.RequestId,
            ["toolCallId"] = callId,
            ["toolName"] = toolName,
            ["arguments"] = argsJson,
            ["summary"] = summary is null ? null : run.Mask(summary),
        };
        run.Events.Emit(EventTypes.ApprovalRequested, requested);

        ApprovalAnswer? auto = null;
        if (run.State.AlwaysApproved.ContainsKey(key)) auto = new(true, "Approved earlier for this session.", "session", "policy");
        else if (ApprovalPolicy.MatchesAllowlist(run.Agent, toolName, args)) auto = new(true, "Matches the agent's allowlist.", "allowlist", "policy");
        else
        {
            ApprovalRequestedContext hookContext = new()
            {
                RunId = run.RunId, SessionId = run.SessionId, AgentName = run.AgentName, WorkspaceRoot = run.WorkspaceRoot,
                RequestId = req.RequestId, ToolName = toolName, Arguments = args,
            };
            await hooks.ApprovalRequestedAsync(hookContext, ct);
            auto = hookContext.Decision switch
            {
                ApprovalDecision.Approve => new(true, hookContext.Reason, "hook", "hook"),
                ApprovalDecision.Deny => new(false, hookContext.Reason, "hook", "hook"),
                _ => null,
            };
        }
        if (auto is null && !request.Interactive && request.ApprovalTimeout is null)
            auto = new(false, "Unattended run with no approver: 'ask' is treated as 'deny'.", "policy", "policy");

        ApprovalAnswer answer;
        if (auto is not null) answer = auto;
        else
        {
            PendingApproval pending = new()
            {
                RequestId = req.RequestId, RunId = run.RunId, SessionId = run.SessionId, ToolName = toolName,
                ToolCallId = callId, Arguments = argsJson, Summary = summary,
            };
            _approvals.Add(pending);
            try
            {
                Task<ApprovalAnswer> wait = pending.Completion.Task.WaitAsync(ct);
                if (request.ApprovalTimeout is TimeSpan timeout)
                {
                    Task finished = await Task.WhenAny(wait, Task.Delay(timeout, ct));
                    answer = finished == wait
                        ? await wait
                        : new ApprovalAnswer(request.OnApprovalTimeoutApprove, $"No answer within {timeout}.", "timeout", "policy");
                }
                else answer = await wait;
            }
            finally
            {
                _approvals.Remove(req.RequestId);
            }
        }

        if (answer.Approved && answer.AlwaysForSession) run.State.AlwaysApproved[key] = true;
        run.Events.Emit(EventTypes.ApprovalResolved, new JsonObject
        {
            ["requestId"] = req.RequestId,
            ["toolName"] = toolName,
            ["approved"] = answer.Approved,
            ["reason"] = answer.Reason,
            ["decidedBy"] = answer.DecidedBy,
            ["via"] = answer.Via,
            ["always"] = answer.AlwaysForSession,
        });
        return answer;
    }

    private sealed record ApprovalInfo(string ToolName, string CallId, IReadOnlyDictionary<string, object?> Arguments, JsonObject ArgumentsJson, string? Summary);

    private static ApprovalInfo Describe(ToolApprovalRequestContent req)
    {
        FunctionCallContent? call = req.ToolCall as FunctionCallContent;
        string toolName = call?.Name ?? "unknown";
        IReadOnlyDictionary<string, object?> args = call?.Arguments?.AsReadOnly() ?? new Dictionary<string, object?>().AsReadOnly();
        JsonObject argsJson = JsonSerializer.SerializeToNode(args, AIJsonUtilities.DefaultOptions) as JsonObject ?? new JsonObject();
        return new ApprovalInfo(toolName, call?.CallId ?? "", args, argsJson, ApprovalPolicy.MainArgument(toolName, args));
    }

    // ---------------------------------------------------------------- parked runs (durable approvals)

    /// <summary>
    /// Brings back runs that were waiting for approval when the daemon stopped: their unanswered approvals become pending
    /// again, and each run resumes on its session as soon as its last approval is answered. Returns the number of runs.
    /// </summary>
    public int RehydrateParkedRuns()
    {
        int count = 0;
        foreach ((string runId, string sessionId, ParkedRequest parked) in _store.Parked())
        {
            if (_active.ContainsKey(runId)) continue;
            if (_sessions.Index.GetRun(runId) is not { } record || RunStates.IsFinal(record.State) || _sessions.TryOpen(sessionId) is null)
            {
                _store.Unpark(runId);
                continue;
            }
            _parked[runId] = parked;
            count++;
            foreach (StoredApproval row in _store.ForRun(runId).Where(r => r.Answer is null))
            {
                if (!parked.Interactive && parked.ApprovalTimeoutSeconds is null)
                {
                    AnswerParked(runId, row, new ApprovalAnswer(false, "Unattended run with no approver: 'ask' is treated as 'deny'.", "policy", "policy"));
                    continue;
                }
                PendingApproval pending = new()
                {
                    RequestId = row.RequestId, RunId = runId, SessionId = sessionId, ToolName = row.ToolName,
                    ToolCallId = row.ToolCallId, Arguments = row.Arguments, Summary = row.Summary, RequestedAt = row.RequestedAt,
                };
                _approvals.Add(pending);
                StoredApproval captured = row;
                _ = pending.Completion.Task.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully) AnswerParked(runId, captured, t.Result);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                if (parked.ApprovalTimeoutSeconds is double seconds)
                {
                    TimeSpan remaining = row.RequestedAt + TimeSpan.FromSeconds(seconds) - DateTimeOffset.UtcNow;
                    ApprovalAnswer onTimeout = new(parked.OnApprovalTimeoutApprove, $"No answer within {TimeSpan.FromSeconds(seconds)}.", "timeout", "policy");
                    _ = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _shutdown.Token)
                        .ContinueWith(t => { if (!t.IsCanceled) _approvals.Resolve(runId, captured.RequestId, onTimeout); }, TaskScheduler.Default);
                }
            }
            TryResumeParked(runId);
        }
        return count;
    }

    private void AnswerParked(string runId, StoredApproval row, ApprovalAnswer answer)
    {
        _store.Decide(row.RequestId, answer);
        if (_sessions.TryOpen(row.SessionId) is { } session)
            Emit(session, runId, EventTypes.ApprovalResolved, new JsonObject
            {
                ["requestId"] = row.RequestId,
                ["toolName"] = row.ToolName,
                ["approved"] = answer.Approved,
                ["reason"] = answer.Reason,
                ["decidedBy"] = answer.DecidedBy,
                ["via"] = answer.Via,
                ["always"] = answer.AlwaysForSession,
            });
        TryResumeParked(runId);
    }

    /// <summary>Once every approval of a parked run is answered, resumes it with the answers (or ends it, if an unattended run was denied).</summary>
    private void TryResumeParked(string runId)
    {
        if (!_parked.TryGetValue(runId, out ParkedRequest? parked)) return;
        IReadOnlyList<StoredApproval> rows = _store.ForRun(runId);
        if (rows.Any(r => r.Answer is null)) return;
        if (!_parked.TryRemove(runId, out _)) return;   // another answer got here first

        RunRecord record = _sessions.Index.GetRun(runId)!;
        SessionFolder session = _sessions.Open(record.SessionId);
        Dictionary<string, ToolApprovalRequestContent> requests = [];
        HashSet<string> wanted = [.. rows.Select(r => r.RequestId)];
        foreach (HistoryEntry entry in session.ReadHistory().Reverse())
        {
            foreach (ToolApprovalRequestContent req in entry.Message.Contents.OfType<ToolApprovalRequestContent>())
                if (wanted.Contains(req.RequestId)) requests.TryAdd(req.RequestId, req);
            if (requests.Count == wanted.Count) break;
        }
        if (requests.Count != wanted.Count)
        {
            FinishParked(runId, RunStates.Failed, "The approval requests of this run are missing from the session history.", parked.Template);
            return;
        }

        if (!parked.Interactive && rows.Any(r => r.Answer!.Approved == false))
        {
            FinishParked(runId, RunStates.Rejected, "An approval was denied.", parked.Template);
            return;
        }

        SessionRuntimeState state = _runtime.Get(session.Id);
        List<AIContent> responses = [];
        foreach (StoredApproval row in rows)
        {
            ApprovalAnswer answer = row.Answer!;
            ToolApprovalRequestContent req = requests[row.RequestId];
            if (answer.Approved && answer.AlwaysForSession)
                state.AlwaysApproved[ApprovalPolicy.InvocationKey(row.ToolName, Describe(req).Arguments)] = true;
            responses.Add(req.CreateResponse(answer.Approved, answer.Approved ? answer.Reason : answer.Reason ?? "Denied."));
        }

        // Unpark before resuming: if the daemon dies mid-resume, approved tools must not run a second time on the next start.
        _store.Unpark(runId);
        RunRequest request = parked.ToRequest(session.Id, [new ChatMessage(ChatRole.User, responses)]);
        ActiveRun active = new(record, request, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        _active[runId] = active;
        _ = Task.Run(() => ExecuteAsync(active, session));
    }

    private void FinishParked(string runId, string state, string error, TemplateRun? template)
    {
        _store.Unpark(runId);
        CleanupWorkspace(template, runId, state);
        if (_sessions.Index.GetRun(runId) is not { } record) return;
        record.State = state;
        record.Error = error;
        record.FinishedAt = DateTimeOffset.UtcNow;
        _sessions.Index.UpsertRun(record);
        if (_sessions.TryOpen(record.SessionId) is { } session)
            Emit(session, runId, EventTypes.RunFinished, new JsonObject
            {
                ["state"] = state,
                ["error"] = error,
                ["inputTokens"] = record.InputTokens,
                ["outputTokens"] = record.OutputTokens,
            });
    }

    /// <summary>Triggered runs refuse to start without a sandbox unless the trigger allows it.</summary>
    private static SandboxSpec RequireSandbox(SandboxSpec spec, RunRequest request, string agentName)
    {
        if (!request.Interactive && spec.Type == "none" && !request.AllowUnsandboxed)
            throw new InvalidOperationException($"Triggered runs need a sandbox; agent '{agentName}' uses sandbox '{spec.Name}' of type none. Set allowUnsandboxed: true on the trigger to override.");
        return spec;
    }

    private static string? JoinInstructions(params string?[] parts)
    {
        string joined = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return joined.Length > 0 ? joined : null;
    }

    /// <summary>Applies a templated run's <c>keep</c> policy to its workspace.</summary>
    private void CleanupWorkspace(TemplateRun? templateRun, string runId, string finalState)
    {
        if (templateRun is null) return;
        string keep;
        try { keep = _catalog.Template(templateRun.Name).Workspace.Keep; }
        catch (ConfigException) { keep = "onFailure"; }
        try { _workspaces.Cleanup(runId, keep, finalState); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Removing the workspace of run {RunId} failed", runId); }
    }

    private RunResult Final(ActiveRun active, string state, string? text, string? error, RunContext? run = null) => new()
    {
        RunId = active.Record.Id,
        SessionId = active.Record.SessionId,
        State = state,
        TriggerId = active.Request.TriggerId,
        Text = text,
        Error = error,
        ReplyTo = active.Request.ReplyTo,
        InputTokens = run?.InputTokens ?? active.Record.InputTokens,
        OutputTokens = run?.OutputTokens ?? active.Record.OutputTokens,
    };

    private void SetState(SessionFolder session, RunRecord record, string state)
    {
        if (record.State == state) return;
        record.State = state;
        _sessions.Index.UpsertRun(record);
        Emit(session, record.Id, EventTypes.RunState, new JsonObject { ["state"] = state });
    }

    internal void Emit(SessionFolder session, string runId, string type, JsonObject data)
    {
        HarnessEvent evt = session.AppendEvent(runId, type, data);
        _hub.Publish(evt);
    }

    private sealed class RunEvents(RunOrchestrator owner, SessionFolder session, RunRecord record) : IRunEvents
    {
        public void Emit(string type, JsonObject data)
        {
            if (type == EventTypes.ToolCallStart)
                record.LastTool = data["toolName"]?.GetValue<string>() is { } tool
                    ? (ApprovalPolicy.MainArgument(tool, ArgsOf(data)) is { } main ? $"{tool}: {(main.Length > 60 ? main[..60] + "…" : main)}" : tool)
                    : record.LastTool;
            if (type == EventTypes.Usage)
            {
                record.InputTokens = data["runInputTokens"]?.GetValue<long>() ?? record.InputTokens;
                record.OutputTokens = data["runOutputTokens"]?.GetValue<long>() ?? record.OutputTokens;
            }
            owner.Emit(session, record.Id, type, data);
        }

        private static IReadOnlyDictionary<string, object?> ArgsOf(JsonObject data) =>
            data["arguments"] is JsonObject args
                ? args.ToDictionary(kv => kv.Key, kv => (object?)(kv.Value?.GetValueKind() == JsonValueKind.String ? kv.Value.GetValue<string>() : kv.Value?.ToJsonString()))
                : new Dictionary<string, object?>();
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _shutdown.Cancel();
        Task[] pending = [.. _active.Values.Select(a => (Task)a.Completion.Task)];
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(10)));
        _shutdown.Dispose();
    }
}
