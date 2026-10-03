using System.Text.Json;
using Harness.Client;
using Harness.Core.Config;
using Harness.Core.Sessions;
using Harness.Extensions.Plugins;
using Harness.Runs;
using Harness.Sdk;
using Harness.Tools;
using Harness.Triggers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;

namespace Harness.Server;

/// <summary>The daemon API. Every client (CLI, PWA, scripts) uses these endpoints; live data comes back over SSE.</summary>
internal static class ApiEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api");

        api.MapGet("/status", (RunOrchestrator runs, ConfigCatalog catalog, PluginRegistry plugins, TriggerEngine triggers) =>
            new StatusDto(typeof(ApiEndpoints).Assembly.GetName().Version?.ToString() ?? "0.0.0", catalog.Paths.Home, runs.ActiveRuns().Count,
                runs.Approvals.Pending().Count, [.. plugins.LoadErrors, .. triggers.LoadErrors]));

        // ---- catalogue ----

        api.MapGet("/agents", (ConfigCatalog catalog) => catalog.Agents().Select(a => a.ToDto()));
        api.MapGet("/templates", (ConfigCatalog catalog) => catalog.Templates().Select(t => t.ToDto()));

        api.MapGet("/workspaces", (ConfigCatalog catalog) =>
            catalog.Workspaces.Select(kv => new WorkspaceDto(kv.Key, Core.HarnessPaths.ExpandHome(kv.Value))).OrderBy(w => w.Name));

        api.MapGet("/workspaces/{name}/files", (string name, string? q, ConfigCatalog catalog) =>
            catalog.Workspace(name) is { } root && Directory.Exists(root)
                ? Results.Ok(FileCompletion.Match(root, q ?? "", 20))
                : Results.NotFound(new ErrorDto($"Workspace '{name}' not found.")));

        // ---- sessions ----

        api.MapPost("/sessions", (CreateSessionRequest body, RunOrchestrator runs, HttpContext http) =>
        {
            // A raw path is only meaningful (and only safe) for a client on the same machine; remote clients use named workspaces.
            if (body.Workspace is not null && body.WorkspaceName is null && !Listener.IsLocalSocket(http))
                return Results.BadRequest(new ErrorDto("Remote clients must use a named workspace (workspaceName)."));
            SessionFolder session = runs.CreateSession(new SessionRequest
            {
                Agent = body.Agent, Workspace = body.Workspace, WorkspaceName = body.WorkspaceName,
                WorkingDirectory = body.WorkingDirectory, Title = body.Title, Model = body.Model,
            });
            return Results.Created($"/api/sessions/{session.Id}", session.Info.ToDto());
        });

        api.MapGet("/sessions", (string? q, string? workspace, int? limit, SessionStore sessions) =>
            sessions.Index.ListSessions(q, workspace, Math.Clamp(limit ?? 100, 1, 1000)).Select(s => s.ToDto()));

        api.MapGet("/sessions/{id}", (string id, SessionStore sessions) =>
            sessions.TryOpen(id) is { } s ? Results.Ok(s.Info.ToDto()) : NotFound("Session", id));

        api.MapPost("/sessions/{id}/messages", (string id, SendMessageRequest body, RunOrchestrator runs, SessionStore sessions) =>
        {
            if (sessions.TryOpen(id) is null) return NotFound("Session", id);
            if (string.IsNullOrWhiteSpace(body.Text)) return Results.BadRequest(new ErrorDto("Text is required."));
            RunRecord run = runs.Start(new RunRequest { SessionId = id, Messages = [new ChatMessage(ChatRole.User, body.Text)] });
            return Results.Accepted($"/api/runs/{run.Id}", new SendMessageResponse(run.Id, id));
        });

        api.MapGet("/sessions/{id}/messages", (string id, long? before, int? limit, SessionStore sessions) =>
        {
            if (sessions.TryOpen(id) is not { } s) return NotFound("Session", id);
            int take = Math.Clamp(limit ?? 50, 1, 500);
            List<HistoryEntry> page = [.. s.ReadHistory().Where(e => before is null || e.Seq < before)];
            bool more = page.Count > take;
            return Results.Ok(new MessagesPage([.. page.Skip(Math.Max(0, page.Count - take)).Select(e => e.ToDto())], more));
        });

        api.MapPost("/sessions/{id}/fork", (string id, ForkRequest body, SessionStore sessions) =>
            sessions.TryOpen(id) is null ? NotFound("Session", id) : Results.Ok(sessions.Fork(id, body.AtSeq, body.Title).Info.ToDto()));

        api.MapGet("/sessions/{id}/export", (string id, string? format, SessionStore sessions) =>
        {
            if (sessions.TryOpen(id) is not { } s) return NotFound("Session", id);
            return format == "json"
                ? Results.Text(JsonSerializer.Serialize(new { session = s.Info.ToDto(), messages = s.ReadHistory().Select(e => e.ToDto()) }, HarnessClient.Json), "application/json")
                : Results.Text(Mapping.ToMarkdown(s.Info, s.ReadHistory()), "text/markdown");
        });

        api.MapPatch("/sessions/{id}", (string id, UpdateSessionRequest body, SessionStore sessions) =>
        {
            if (sessions.TryOpen(id) is not { } s) return NotFound("Session", id);
            if (body.Title is { } title)
            {
                if (string.IsNullOrWhiteSpace(title)) return Results.BadRequest(new ErrorDto("Title must not be empty."));
                s.Info.Title = title.Trim().ReplaceLineEndings(" ");
                s.Save();
            }
            return Results.Ok(s.Info.ToDto());
        });

        // '@' completion in the TUI composer: paths in the session's own workspace, whichever way it was opened.
        api.MapGet("/sessions/{id}/files", (string id, string? q, SessionStore sessions) =>
            sessions.TryOpen(id) is { } s && Directory.Exists(s.Info.Workspace)
                ? Results.Ok(FileCompletion.Match(s.Info.Workspace, q ?? "", 20))
                : NotFound("Session", id));

        api.MapDelete("/sessions/{id}", (string id, SessionStore sessions) =>
            sessions.Delete(id) ? Results.NoContent() : NotFound("Session", id));

        api.MapPost("/sessions/reindex", (SessionStore sessions) => Results.Ok(sessions.Reindex()));

        api.MapPost("/sandboxes/{name}/test", async (string name, Harness.Sandbox.SandboxProbe probe, Harness.Core.HarnessPaths paths, CancellationToken ct) =>
            (await probe.RunAsync(name, paths.Home, ct)).Select(r => new SandboxProbeDto(r.Check, r.Status, r.Detail)));

        api.MapPost("/retention/sweep", async (PruneRequest body, Retention retention, CancellationToken ct) =>
        {
            RetentionReport r = await retention.SweepAsync(body.DryRun, ct);
            return new PruneReportDto(r.Sessions, r.RunFolders, r.Events, r.DryRun);
        });

        // ---- runs ----

        api.MapGet("/runs", (string? state, DateTimeOffset? since, string? session, int? limit, SessionStore sessions) =>
            sessions.Index.ListRuns(state, since, session, Math.Clamp(limit ?? 100, 1, 1000)).Select(r => r.ToDto()));

        api.MapGet("/runs/{id}", (string id, RunOrchestrator runs) =>
            runs.Get(id) is { } r ? Results.Ok(r.ToDto()) : NotFound("Run", id));

        api.MapPost("/runs/{id}/cancel", (string id, RunOrchestrator runs) =>
            runs.Cancel(id) ? Results.Accepted() : runs.Get(id) is null ? NotFound("Run", id) : Results.Conflict(new ErrorDto("The run is not active.")));

        api.MapGet("/approvals", (RunOrchestrator runs) => runs.Approvals.Pending().Select(p => p.ToDto()));

        api.MapPost("/runs/{id}/approvals/{requestId}", (string id, string requestId, ApprovalDecisionRequest body, RunOrchestrator runs, HttpContext http) =>
        {
            string via = Listener.IsLocalSocket(http) ? "cli" : "api";
            // Over the API, who decided is the token's name; a client cannot claim to be someone else.
            string decidedBy = Listener.IsLocalSocket(http) ? body.DecidedBy ?? via : Auth.Callers.Caller(http).Name;
            bool ok = runs.ResolveApproval(id, requestId, new ApprovalAnswer(body.Approved, body.Reason, decidedBy, via, body.Always));
            return ok ? Results.Ok() : Results.Conflict(new ErrorDto("No such pending approval (it may already be answered)."));
        });

        api.MapGet("/runs/{id}/events", RunEventsAsync);
        api.MapGet("/events", FirehoseAsync);

        // ---- triggers ----

        api.MapGet("/triggers", (TriggerEngine triggers, RunOrchestrator runs) => triggers.Definitions.OrderBy(d => d.Id).Select(d =>
        {
            (DateTimeOffset? _, string? lastRun) = triggers.LastFire(d.Id);
            return new TriggerDto(d.Id, d.SourceType, triggers.IsEnabled(d), triggers.NextFire(d), lastRun, lastRun is null ? null : runs.Get(lastRun)?.State,
                d.Budget.DailyTokens, d.Budget.DailyTokens is null ? null : triggers.TokensUsedToday(d, null));
        }));

        api.MapPost("/triggers/{id}/fire", async (string id, FireTriggerRequest? body, TriggerEngine triggers, CancellationToken ct) =>
        {
            if (triggers.Get(id) is null) return NotFound("Trigger", id);
            RunRecord run = await triggers.FireAsync(id, body?.Text, body?.Inputs, ct);
            return Results.Accepted($"/api/runs/{run.Id}", new SendMessageResponse(run.Id, run.SessionId));
        });

        api.MapPatch("/triggers/{id}", async (string id, JsonElement body, TriggerEngine triggers, CancellationToken ct) =>
        {
            if (triggers.Get(id) is null) return NotFound("Trigger", id);
            if (!body.TryGetProperty("enabled", out JsonElement enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Results.BadRequest(new ErrorDto("Body must be { \"enabled\": true|false }."));
            await triggers.SetEnabledAsync(id, enabled.GetBoolean(), ct);
            return Results.Ok();
        });

        api.MapPost("/triggers/reload", async (TriggerEngine triggers, CancellationToken ct) =>
        {
            await triggers.ReloadAsync(ct);
            return Results.Ok(triggers.LoadErrors);
        });
    }

    private static async Task RunEventsAsync(string id, HttpContext http, RunOrchestrator runs, SessionStore sessions, CancellationToken ct)
    {
        if (runs.Get(id) is not { } run)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new ErrorDto($"Run '{id}' not found."), ct);
            return;
        }
        SessionFolder session = sessions.Open(run.SessionId);
        Sse.Begin(http.Response);
        // Subscribe before replaying so nothing falls between the journal and the live stream.
        using EventHub.Subscription live = runs.Events.Subscribe(e => e.RunId == id);
        long last = Sse.LastEventId(http.Request) ?? 0;
        foreach (HarnessEvent evt in session.ReadEvents(last, id))
        {
            await Sse.WriteAsync(http.Response, evt, evt.Seq, ct);
            last = evt.Seq;
            if (evt.Type == EventTypes.RunFinished) return;
        }
        if (runs.Get(id) is { } now && RunStates.IsFinal(now.State) && runs.ActiveRuns().All(r => r.Id != id)) return;

        while (true)
        {
            (bool ok, EventHub.Envelope? envelope) = await Sse.NextAsync(live.Reader, http.Response, ct);
            if (!ok || envelope is null) return;
            HarnessEvent evt = envelope.Event;
            bool persisted = EventTypes.IsPersisted(evt.Type);
            if (persisted && evt.Seq <= last) continue;
            await Sse.WriteAsync(http.Response, evt, persisted ? evt.Seq : null, ct);
            if (persisted) last = evt.Seq;
            if (evt.Type == EventTypes.RunFinished) return;
        }
    }

    private static async Task FirehoseAsync(HttpContext http, string? session, RunOrchestrator runs, CancellationToken ct)
    {
        Sse.Begin(http.Response);
        await http.Response.Body.FlushAsync(ct);
        using EventHub.Subscription live = runs.Events.Subscribe(e => session is null || e.SessionId == session, Sse.LastEventId(http.Request));
        while (true)
        {
            (bool ok, EventHub.Envelope? envelope) = await Sse.NextAsync(live.Reader, http.Response, ct);
            if (!ok || envelope is null) return;
            await Sse.WriteAsync(http.Response, envelope.Event, EventTypes.IsPersisted(envelope.Event.Type) ? envelope.HubSeq : null, ct);
        }
    }

    private static IResult NotFound(string what, string id) => Results.NotFound(new ErrorDto($"{what} '{id}' not found."));
}

/// <summary>Fuzzy file-path completion for <c>@</c> mentions.</summary>
internal static class FileCompletion
{
    public static IReadOnlyList<string> Match(string root, string query, int limit)
    {
        Gitignore ignore = new(root);
        List<(int Score, string Path)> hits = [];
        int seen = 0;
        foreach (FileSystemInfo entry in ignore.Walk(root))
        {
            if (entry is not FileInfo || ++seen > 50_000) continue;
            string rel = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            if (Score(rel, query) is int score) hits.Add((score, rel));
        }
        return [.. hits.OrderBy(h => h.Score).ThenBy(h => h.Path.Length).ThenBy(h => h.Path, StringComparer.Ordinal).Take(limit).Select(h => h.Path)];
    }

    /// <summary>Subsequence match; lower is better. Contiguous runs and matches in the file name score better.</summary>
    internal static int? Score(string path, string query)
    {
        if (query.Length == 0) return path.Length;
        int score = 0, last = -1, qi = 0;
        int nameStart = path.LastIndexOf('/') + 1;
        for (int i = 0; i < path.Length && qi < query.Length; i++)
        {
            if (char.ToLowerInvariant(path[i]) != char.ToLowerInvariant(query[qi])) continue;
            score += last < 0 ? i : i - last - 1;
            if (i < nameStart) score += 2;
            last = i;
            qi++;
        }
        return qi == query.Length ? score : null;
    }
}
