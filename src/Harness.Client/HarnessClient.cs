using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Harness.Client;

/// <summary>
/// Typed client for the daemon API. Locally it connects over the Unix domain socket, where file permissions are the
/// authentication; remotely it uses HTTPS with a bearer token.
/// </summary>
public sealed class HarnessClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public HarnessClient(HttpClient http) => _http = http;

    public Uri BaseAddress => _http.BaseAddress!;

    public static HarnessClient ForSocket(string socketPath)
    {
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = async (_, ct) =>
            {
                Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HarnessClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/"), Timeout = Timeout.InfiniteTimeSpan });
    }

    public static HarnessClient ForUrl(Uri url, string? token)
    {
        HttpClient http = new() { BaseAddress = url, Timeout = Timeout.InfiniteTimeSpan };
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new HarnessClient(http);
    }

    // ---- status and catalogue ----

    public Task<StatusDto> StatusAsync(CancellationToken ct = default) => GetAsync<StatusDto>("api/status", ct);
    public Task<IReadOnlyList<AgentDto>> AgentsAsync(CancellationToken ct = default) => GetAsync<IReadOnlyList<AgentDto>>("api/agents", ct);
    public Task<IReadOnlyList<WorkspaceDto>> WorkspacesAsync(CancellationToken ct = default) => GetAsync<IReadOnlyList<WorkspaceDto>>("api/workspaces", ct);
    public Task<IReadOnlyList<string>> WorkspaceFilesAsync(string name, string query, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<string>>($"api/workspaces/{Uri.EscapeDataString(name)}/files?q={Uri.EscapeDataString(query)}", ct);

    // ---- sessions ----

    public Task<SessionDto> CreateSessionAsync(CreateSessionRequest request, CancellationToken ct = default) => PostAsync<SessionDto>("api/sessions", request, ct);

    public Task<IReadOnlyList<SessionDto>> SessionsAsync(string? query = null, string? workspace = null, int limit = 100, CancellationToken ct = default)
    {
        List<string> q = [$"limit={limit}"];
        if (query is not null) q.Add("q=" + Uri.EscapeDataString(query));
        if (workspace is not null) q.Add("workspace=" + Uri.EscapeDataString(workspace));
        return GetAsync<IReadOnlyList<SessionDto>>("api/sessions?" + string.Join('&', q), ct);
    }

    public Task<SessionDto> SessionAsync(string id, CancellationToken ct = default) => GetAsync<SessionDto>($"api/sessions/{id}", ct);

    public Task<SendMessageResponse> SendAsync(string sessionId, string text, CancellationToken ct = default) =>
        PostAsync<SendMessageResponse>($"api/sessions/{sessionId}/messages", new SendMessageRequest(text), ct);

    public Task<MessagesPage> MessagesAsync(string sessionId, long? before = null, int limit = 50, CancellationToken ct = default) =>
        GetAsync<MessagesPage>($"api/sessions/{sessionId}/messages?limit={limit}" + (before is long b ? $"&before={b}" : ""), ct);

    public Task<SessionDto> ForkAsync(string sessionId, ForkRequest request, CancellationToken ct = default) => PostAsync<SessionDto>($"api/sessions/{sessionId}/fork", request, ct);

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default) =>
        await EnsureAsync(await _http.DeleteAsync($"api/sessions/{sessionId}", ct), ct);

    public async Task<string> ExportAsync(string sessionId, string format, CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.GetAsync($"api/sessions/{sessionId}/export?format={format}", ct);
        await EnsureAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public Task<int> ReindexAsync(CancellationToken ct = default) => PostAsync<int>("api/sessions/reindex", new { }, ct);

    // ---- runs ----

    public Task<IReadOnlyList<RunDto>> RunsAsync(string? state = null, DateTimeOffset? since = null, string? sessionId = null, int limit = 100, CancellationToken ct = default)
    {
        List<string> q = [$"limit={limit}"];
        if (state is not null) q.Add("state=" + Uri.EscapeDataString(state));
        if (since is not null) q.Add("since=" + Uri.EscapeDataString(since.Value.ToString("O")));
        if (sessionId is not null) q.Add("session=" + Uri.EscapeDataString(sessionId));
        return GetAsync<IReadOnlyList<RunDto>>("api/runs?" + string.Join('&', q), ct);
    }

    public Task<RunDto> RunAsync(string runId, CancellationToken ct = default) => GetAsync<RunDto>($"api/runs/{runId}", ct);

    public async Task CancelAsync(string runId, CancellationToken ct = default) =>
        await EnsureAsync(await _http.PostAsync($"api/runs/{runId}/cancel", null, ct), ct);

    public Task<IReadOnlyList<ApprovalDto>> ApprovalsAsync(CancellationToken ct = default) => GetAsync<IReadOnlyList<ApprovalDto>>("api/approvals", ct);

    public async Task DecideAsync(string runId, string requestId, ApprovalDecisionRequest decision, CancellationToken ct = default) =>
        await EnsureAsync(await _http.PostAsJsonAsync($"api/runs/{runId}/approvals/{requestId}", decision, Json, ct), ct);

    /// <summary>Events of one run: the journal is replayed first, then live events follow until the run finishes.</summary>
    public IAsyncEnumerable<EventDto> RunEventsAsync(string runId, long? afterSeq = null, CancellationToken ct = default) =>
        StreamAsync($"api/runs/{runId}/events", afterSeq, ct);

    /// <summary>The firehose of all runs, optionally filtered by session.</summary>
    public IAsyncEnumerable<EventDto> EventsAsync(string? sessionId = null, long? afterSeq = null, CancellationToken ct = default) =>
        StreamAsync("api/events" + (sessionId is null ? "" : "?session=" + Uri.EscapeDataString(sessionId)), afterSeq, ct);

    // ---- triggers ----

    public Task<IReadOnlyList<TriggerDto>> TriggersAsync(CancellationToken ct = default) => GetAsync<IReadOnlyList<TriggerDto>>("api/triggers", ct);

    public Task<SendMessageResponse> FireTriggerAsync(string id, FireTriggerRequest request, CancellationToken ct = default) =>
        PostAsync<SendMessageResponse>($"api/triggers/{id}/fire", request, ct);

    public async Task SetTriggerEnabledAsync(string id, bool enabled, CancellationToken ct = default) =>
        await EnsureAsync(await _http.PatchAsJsonAsync($"api/triggers/{id}", new { enabled }, Json, ct), ct);

    // ---- plumbing ----

    private async IAsyncEnumerable<EventDto> StreamAsync(string path, long? afterSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (afterSeq is long after) request.Headers.Add("Last-Event-ID", after.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureAsync(response, ct);
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (EventDto evt in SseReader.ReadAsync(stream, ct)) yield return evt;
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        HttpResponseMessage response = await _http.GetAsync(path, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(path, body, Json, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync(ct);
        string message = body;
        try { message = JsonSerializer.Deserialize<ErrorDto>(body, Json)?.Error ?? body; } catch (JsonException) { }
        throw new HarnessApiException(response.StatusCode, string.IsNullOrWhiteSpace(message) ? response.ReasonPhrase ?? "error" : message);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class HarnessApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Parses a Server-Sent Events stream of <see cref="EventDto"/> payloads.</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<EventDto> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using StreamReader reader = new(stream);
        System.Text.StringBuilder data = new();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    EventDto? evt = JsonSerializer.Deserialize<EventDto>(data.ToString(), HarnessClient.Json);
                    data.Clear();
                    if (evt is not null) yield return evt;
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }
    }
}
