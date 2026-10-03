using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Sessions;
using Harness.Sdk;
using Microsoft.Data.Sqlite;

namespace Harness.Runs;

/// <summary>What a parked run needs to resume after a restart: everything in its <see cref="RunRequest"/> except messages and extra tools.</summary>
public sealed record ParkedRequest(
    bool Interactive,
    string? TriggerId,
    string? RunInstructions,
    ReplyAddress? ReplyTo,
    bool AllowUnsandboxed,
    double? ApprovalTimeoutSeconds,
    bool OnApprovalTimeoutApprove,
    Templates.TemplateRun? Template = null,
    Delivery.DeliveryPlan? Delivery = null)
{
    public static ParkedRequest From(RunRequest r) => new(r.Interactive, r.TriggerId, r.RunInstructions, r.ReplyTo, r.AllowUnsandboxed,
        r.ApprovalTimeout?.TotalSeconds, r.OnApprovalTimeoutApprove, r.Template, r.Delivery);

    public RunRequest ToRequest(string sessionId, IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages) => new()
    {
        SessionId = sessionId,
        Messages = messages,
        Interactive = Interactive,
        TriggerId = TriggerId,
        RunInstructions = RunInstructions,
        ReplyTo = ReplyTo,
        AllowUnsandboxed = AllowUnsandboxed,
        ApprovalTimeout = ApprovalTimeoutSeconds is double s ? TimeSpan.FromSeconds(s) : null,
        OnApprovalTimeoutApprove = OnApprovalTimeoutApprove,
        Template = Template,
        Delivery = Delivery,
        Resumed = true,
    };
}

/// <summary>One approval row: the request, and the decision once there is one.</summary>
public sealed record StoredApproval(
    string RequestId, string RunId, string SessionId, string ToolName, string ToolCallId, JsonObject Arguments, string? Summary,
    DateTimeOffset RequestedAt, ApprovalAnswer? Answer);

/// <summary>
/// Durable approvals: runs waiting on a person are parked in SQLite together with their approval requests, so a pending
/// approval survives a daemon restart and the run resumes when it is answered.
/// </summary>
public sealed class ApprovalStore(HarnessDb db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Park(string runId, string sessionId, ParkedRequest request)
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO parked_runs (run_id, session_id, request, parked_at) VALUES ($r, $s, $q, $t)";
        cmd.Parameters.AddWithValue("$r", runId);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$q", JsonSerializer.Serialize(request, Json));
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Forgets a run and all of its approval rows.</summary>
    public void Unpark(string runId)
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM parked_runs WHERE run_id = $r; DELETE FROM pending_approvals WHERE run_id = $r;";
        cmd.Parameters.AddWithValue("$r", runId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string RunId, string SessionId, ParkedRequest Request)> Parked()
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT run_id, session_id, request FROM parked_runs ORDER BY parked_at";
        List<(string, string, ParkedRequest)> rows = [];
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
            if (JsonSerializer.Deserialize<ParkedRequest>(r.GetString(2), Json) is { } request) rows.Add((r.GetString(0), r.GetString(1), request));
        return rows;
    }

    public bool IsParked(string runId)
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM parked_runs WHERE run_id = $r";
        cmd.Parameters.AddWithValue("$r", runId);
        return cmd.ExecuteScalar() is not null;
    }

    public void Add(StoredApproval a)
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO pending_approvals (request_id, run_id, session_id, tool_name, tool_call_id, arguments, summary, requested_at)
            VALUES ($id, $run, $s, $tool, $call, $args, $sum, $at)
            """;
        cmd.Parameters.AddWithValue("$id", a.RequestId);
        cmd.Parameters.AddWithValue("$run", a.RunId);
        cmd.Parameters.AddWithValue("$s", a.SessionId);
        cmd.Parameters.AddWithValue("$tool", a.ToolName);
        cmd.Parameters.AddWithValue("$call", a.ToolCallId);
        cmd.Parameters.AddWithValue("$args", a.Arguments.ToJsonString());
        cmd.Parameters.AddWithValue("$sum", (object?)a.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", a.RequestedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void Decide(string requestId, ApprovalAnswer answer)
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE pending_approvals SET approved = $a, reason = $r, decided_by = $b, via = $v, always = $al WHERE request_id = $id";
        cmd.Parameters.AddWithValue("$id", requestId);
        cmd.Parameters.AddWithValue("$a", answer.Approved ? 1 : 0);
        cmd.Parameters.AddWithValue("$r", (object?)answer.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", answer.DecidedBy);
        cmd.Parameters.AddWithValue("$v", answer.Via);
        cmd.Parameters.AddWithValue("$al", answer.AlwaysForSession ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<StoredApproval> ForRun(string runId)
    {
        using SqliteConnection c = db.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT request_id, run_id, session_id, tool_name, tool_call_id, arguments, summary, requested_at, approved, reason, decided_by, via, always
            FROM pending_approvals WHERE run_id = $r ORDER BY requested_at, rowid
            """;
        cmd.Parameters.AddWithValue("$r", runId);
        List<StoredApproval> rows = [];
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            ApprovalAnswer? answer = r.IsDBNull(8) ? null : new ApprovalAnswer(r.GetInt64(8) == 1, r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? "unknown" : r.GetString(10), r.IsDBNull(11) ? "unknown" : r.GetString(11), !r.IsDBNull(12) && r.GetInt64(12) == 1);
            rows.Add(new StoredApproval(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                JsonNode.Parse(r.GetString(5)) as JsonObject ?? [], r.IsDBNull(6) ? null : r.GetString(6),
                DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture), answer));
        }
        return rows;
    }
}
