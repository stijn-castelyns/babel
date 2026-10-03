using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Harness.Core.Sessions;

/// <summary>
/// The SQLite index in <c>harness.db</c>: session metadata, a full-text index of messages, and run records.
/// Sessions are always rebuildable from their folders (<see cref="SessionStore.Reindex"/>).
/// </summary>
public sealed class HarnessDb
{
    private readonly string _connectionString;

    public HarnessDb(HarnessPaths paths) : this(paths.Database) { }

    public HarnessDb(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = file, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Migrate()
    {
        using SqliteConnection c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY, path TEXT, title TEXT, agent TEXT, model TEXT, workspace TEXT, workspace_name TEXT,
                session_key TEXT, parent_id TEXT, status TEXT, trigger_id TEXT, tags TEXT,
                created_at TEXT, updated_at TEXT, input_tokens INTEGER, output_tokens INTEGER, message_count INTEGER);
            CREATE INDEX IF NOT EXISTS ix_sessions_key ON sessions(session_key);
            CREATE INDEX IF NOT EXISTS ix_sessions_updated ON sessions(updated_at);
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(session_id UNINDEXED, seq UNINDEXED, role UNINDEXED, text);
            CREATE TABLE IF NOT EXISTS runs (
                id TEXT PRIMARY KEY, session_id TEXT, agent TEXT, model TEXT, trigger_id TEXT, state TEXT,
                created_at TEXT, started_at TEXT, finished_at TEXT, input_tokens INTEGER, output_tokens INTEGER,
                last_tool TEXT, error TEXT, result_text TEXT);
            CREATE INDEX IF NOT EXISTS ix_runs_created ON runs(created_at);
            CREATE INDEX IF NOT EXISTS ix_runs_session ON runs(session_id);
            CREATE TABLE IF NOT EXISTS parked_runs (
                run_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, request TEXT NOT NULL, parked_at TEXT);
            CREATE TABLE IF NOT EXISTS pending_approvals (
                request_id TEXT PRIMARY KEY, run_id TEXT NOT NULL, session_id TEXT NOT NULL, tool_name TEXT, tool_call_id TEXT,
                arguments TEXT, summary TEXT, requested_at TEXT,
                approved INTEGER, reason TEXT, decided_by TEXT, via TEXT, always INTEGER);
            CREATE INDEX IF NOT EXISTS ix_pending_approvals_run ON pending_approvals(run_id);
            CREATE TABLE IF NOT EXISTS trigger_events (
                event_id TEXT NOT NULL, trigger_id TEXT NOT NULL, received_at TEXT, payload TEXT, status TEXT, run_id TEXT,
                PRIMARY KEY (trigger_id, event_id));
            """);
    }

    // ---- sessions ----

    public void UpsertSession(SessionInfo s, string? path = null)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (id, path, title, agent, model, workspace, workspace_name, session_key, parent_id, status, trigger_id, tags,
                                  created_at, updated_at, input_tokens, output_tokens, message_count)
            VALUES ($id, $path, $title, $agent, $model, $ws, $wsn, $key, $parent, $status, $trigger, $tags, $created, $updated, $in, $out, $count)
            ON CONFLICT(id) DO UPDATE SET path = COALESCE($path, path), title = $title, agent = $agent, model = $model, workspace = $ws,
                workspace_name = $wsn, session_key = $key, parent_id = $parent, status = $status, trigger_id = $trigger, tags = $tags,
                updated_at = $updated, input_tokens = $in, output_tokens = $out, message_count = $count;
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)s.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agent", s.Agent);
        cmd.Parameters.AddWithValue("$model", s.Model);
        cmd.Parameters.AddWithValue("$ws", s.Workspace);
        cmd.Parameters.AddWithValue("$wsn", (object?)s.WorkspaceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$key", (object?)s.Key ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$parent", (object?)s.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", s.Status);
        cmd.Parameters.AddWithValue("$trigger", (object?)s.TriggerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", string.Join(',', s.Tags));
        cmd.Parameters.AddWithValue("$created", s.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", s.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$in", s.InputTokens);
        cmd.Parameters.AddWithValue("$out", s.OutputTokens);
        cmd.Parameters.AddWithValue("$count", s.MessageCount);
        cmd.ExecuteNonQuery();
    }

    public string? GetSessionPath(string id) => Scalar("SELECT path FROM sessions WHERE id = $p", id);

    public string? FindSessionIdByKey(string key) =>
        Scalar("SELECT id FROM sessions WHERE session_key = $p ORDER BY updated_at DESC LIMIT 1", key);

    public sealed record SessionRow(string Id, string? Title, string Agent, string Model, string Workspace, string? WorkspaceName,
        string? Key, string? ParentId, string Status, string? TriggerId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        long InputTokens, long OutputTokens, long MessageCount);

    /// <summary>Lists sessions, newest activity first. <paramref name="query"/> searches titles and message text.</summary>
    public IReadOnlyList<SessionRow> ListSessions(string? query = null, string? workspaceName = null, int limit = 100)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        List<string> where = [];
        if (!string.IsNullOrWhiteSpace(query))
        {
            where.Add("(title LIKE $like OR id IN (SELECT session_id FROM messages_fts WHERE messages_fts MATCH $match))");
            cmd.Parameters.AddWithValue("$like", $"%{query}%");
            cmd.Parameters.AddWithValue("$match", FtsQuery(query));
        }
        if (workspaceName is not null)
        {
            where.Add("workspace_name = $wsn");
            cmd.Parameters.AddWithValue("$wsn", workspaceName);
        }
        cmd.CommandText = $"""
            SELECT id, title, agent, model, workspace, workspace_name, session_key, parent_id, status, trigger_id,
                   created_at, updated_at, input_tokens, output_tokens, message_count
            FROM sessions {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY updated_at DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        List<SessionRow> rows = [];
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new SessionRow(r.GetString(0), Str(r, 1), r.GetString(2), r.GetString(3), r.GetString(4), Str(r, 5), Str(r, 6), Str(r, 7),
                r.GetString(8), Str(r, 9), Date(r, 10)!.Value, Date(r, 11)!.Value, r.GetInt64(12), r.GetInt64(13), r.GetInt64(14)));
        return rows;
    }

    public void IndexMessages(string sessionId, IEnumerable<HistoryEntry> entries)
    {
        using SqliteConnection c = Open();
        using SqliteTransaction tx = c.BeginTransaction();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO messages_fts (session_id, seq, role, text) VALUES ($s, $q, $r, $t)";
        SqliteParameter seq = cmd.Parameters.Add("$q", SqliteType.Integer);
        SqliteParameter role = cmd.Parameters.Add("$r", SqliteType.Text);
        SqliteParameter text = cmd.Parameters.Add("$t", SqliteType.Text);
        cmd.Parameters.AddWithValue("$s", sessionId);
        foreach (HistoryEntry e in entries)
        {
            string body = e.Message.Text;
            if (string.IsNullOrWhiteSpace(body)) continue;
            seq.Value = e.Seq;
            role.Value = e.Message.Role.Value;
            text.Value = body;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void DeleteSession(string id)
    {
        using SqliteConnection c = Open();
        Exec(c, "DELETE FROM sessions WHERE id = $p; DELETE FROM messages_fts WHERE session_id = $p;", id);
    }

    public void ClearSessions()
    {
        using SqliteConnection c = Open();
        Exec(c, "DELETE FROM sessions; DELETE FROM messages_fts;");
    }

    // ---- runs ----

    public void UpsertRun(RunRecord r)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runs (id, session_id, agent, model, trigger_id, state, created_at, started_at, finished_at, input_tokens, output_tokens, last_tool, error, result_text)
            VALUES ($id, $s, $a, $m, $t, $state, $c, $st, $f, $in, $out, $tool, $err, $res)
            ON CONFLICT(id) DO UPDATE SET state = $state, started_at = $st, finished_at = $f, input_tokens = $in, output_tokens = $out,
                last_tool = $tool, error = $err, result_text = $res;
            """;
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.Parameters.AddWithValue("$s", r.SessionId);
        cmd.Parameters.AddWithValue("$a", r.Agent);
        cmd.Parameters.AddWithValue("$m", r.Model);
        cmd.Parameters.AddWithValue("$t", (object?)r.TriggerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$state", r.State);
        cmd.Parameters.AddWithValue("$c", r.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$st", (object?)r.StartedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", (object?)r.FinishedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$in", r.InputTokens);
        cmd.Parameters.AddWithValue("$out", r.OutputTokens);
        cmd.Parameters.AddWithValue("$tool", (object?)r.LastTool ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)r.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$res", (object?)r.ResultText ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public RunRecord? GetRun(string id) => QueryRuns("WHERE id = $id", cmd => cmd.Parameters.AddWithValue("$id", id), 1).FirstOrDefault();

    public IReadOnlyList<RunRecord> ListRuns(string? state = null, DateTimeOffset? since = null, string? sessionId = null, int limit = 100)
    {
        List<string> where = [];
        if (state is not null) where.Add("state = $state");
        if (since is not null) where.Add("created_at >= $since");
        if (sessionId is not null) where.Add("session_id = $sid");
        return QueryRuns(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "", cmd =>
        {
            if (state is not null) cmd.Parameters.AddWithValue("$state", state);
            if (since is not null) cmd.Parameters.AddWithValue("$since", since.Value.ToUniversalTime().ToString("O"));
            if (sessionId is not null) cmd.Parameters.AddWithValue("$sid", sessionId);
        }, limit);
    }

    /// <summary>
    /// Marks runs that were active when the daemon stopped as failed, except runs parked on an approval:
    /// those resume when the approval is answered.
    /// </summary>
    public int FailInterruptedRuns()
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE runs SET state = 'failed', error = 'Daemon stopped while the run was active.', finished_at = $now
            WHERE state IN ('queued', 'preparing', 'running', 'awaiting_approval', 'validating', 'delivering')
              AND id NOT IN (SELECT run_id FROM parked_runs);
            UPDATE sessions SET status = 'idle' WHERE status <> 'idle';
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery();
    }

    private List<RunRecord> QueryRuns(string where, Action<SqliteCommand> bind, int limit)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, session_id, agent, model, trigger_id, state, created_at, started_at, finished_at, input_tokens, output_tokens, last_tool, error, result_text
            FROM runs {where} ORDER BY created_at DESC LIMIT $limit
            """;
        bind(cmd);
        cmd.Parameters.AddWithValue("$limit", limit);
        List<RunRecord> rows = [];
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new RunRecord
            {
                Id = r.GetString(0), SessionId = r.GetString(1), Agent = r.GetString(2), Model = r.GetString(3), TriggerId = Str(r, 4),
                State = r.GetString(5), CreatedAt = Date(r, 6)!.Value, StartedAt = Date(r, 7), FinishedAt = Date(r, 8),
                InputTokens = r.GetInt64(9), OutputTokens = r.GetInt64(10), LastTool = Str(r, 11), Error = Str(r, 12), ResultText = Str(r, 13),
            });
        return rows;
    }

    // ---- helpers ----

    private string? Scalar(string sql, string p)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", p);
        return cmd.ExecuteScalar() as string;
    }

    private static void Exec(SqliteConnection c, string sql, string? p = null)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (p is not null) cmd.Parameters.AddWithValue("$p", p);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Turns free text into an FTS5 query of quoted prefix terms, so user input cannot inject query syntax.</summary>
    internal static string FtsQuery(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*"));

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? Date(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
