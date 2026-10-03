using System.Text.Json;
using Harness.Core.Sessions;
using Harness.Sdk;
using Microsoft.Data.Sqlite;

namespace Harness.Triggers;

/// <summary>
/// The durable queue: every event is written to SQLite before anything else happens and de-duplicated by
/// (trigger, event id), so retried webhooks and restarts neither lose nor double-run an event.
/// </summary>
public sealed class TriggerQueue(HarnessDb db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Returns false when the event was seen before.</summary>
    public bool TryEnqueue(TriggerEvent evt)
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO trigger_events (event_id, trigger_id, received_at, payload, status)
            VALUES ($e, $t, $r, $p, 'pending')
            """;
        cmd.Parameters.AddWithValue("$e", evt.EventId);
        cmd.Parameters.AddWithValue("$t", evt.TriggerId);
        cmd.Parameters.AddWithValue("$r", evt.ReceivedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(evt, Json));
        return cmd.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<TriggerEvent> Pending()
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload FROM trigger_events WHERE status = 'pending' ORDER BY received_at";
        List<TriggerEvent> events = [];
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
            if (JsonSerializer.Deserialize<TriggerEvent>(r.GetString(0), Json) is { } evt) events.Add(evt);
        return events;
    }

    public void Mark(TriggerEvent evt, string status, string? runId = null)
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE trigger_events SET status = $s, run_id = $r WHERE trigger_id = $t AND event_id = $e";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$r", (object?)runId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", evt.TriggerId);
        cmd.Parameters.AddWithValue("$e", evt.EventId);
        cmd.ExecuteNonQuery();
    }

    // ---- per-trigger state: enabled override and last fire time ----

    public void EnsureStateTable()
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS trigger_state (trigger_id TEXT PRIMARY KEY, enabled INTEGER, last_fired_at TEXT, last_run_id TEXT)";
        cmd.ExecuteNonQuery();
    }

    public (bool? Enabled, DateTimeOffset? LastFiredAt, string? LastRunId) State(string triggerId)
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT enabled, last_fired_at, last_run_id FROM trigger_state WHERE trigger_id = $t";
        cmd.Parameters.AddWithValue("$t", triggerId);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null, null);
        return (r.IsDBNull(0) ? null : r.GetInt64(0) == 1,
                r.IsDBNull(1) ? null : DateTimeOffset.Parse(r.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                r.IsDBNull(2) ? null : r.GetString(2));
    }

    public void SetEnabled(string triggerId, bool enabled) => Upsert(triggerId, "enabled", enabled ? 1 : 0);

    public void RecordFire(string triggerId, DateTimeOffset at, string? runId)
    {
        Upsert(triggerId, "last_fired_at", at.ToString("O"));
        if (runId is not null) Upsert(triggerId, "last_run_id", runId);
    }

    private void Upsert(string triggerId, string column, object value)
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO trigger_state (trigger_id, {column}) VALUES ($t, $v) ON CONFLICT(trigger_id) DO UPDATE SET {column} = $v";
        cmd.Parameters.AddWithValue("$t", triggerId);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
