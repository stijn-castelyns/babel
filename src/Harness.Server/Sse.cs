using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Client;
using Harness.Sdk;
using Microsoft.AspNetCore.Http;

namespace Harness.Server;

/// <summary>Server-Sent Events: resumable with <c>Last-Event-ID</c>, with keep-alive comments for proxies.</summary>
internal static class Sse
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    public static void Begin(HttpResponse response)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    public static long? LastEventId(HttpRequest request) =>
        long.TryParse(request.Headers["Last-Event-ID"].FirstOrDefault() ?? request.Query["lastEventId"].FirstOrDefault(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : null;

    /// <summary>Writes one event. Live-only events get no <c>id:</c> line, so a reconnect never resumes from one.</summary>
    public static async Task WriteAsync(HttpResponse response, HarnessEvent evt, long? id, CancellationToken ct)
    {
        EventDto dto = new(id ?? evt.Seq, evt.Ts, evt.RunId, evt.SessionId, evt.Type, evt.Data);
        string json = JsonSerializer.Serialize(dto, HarnessClient.Json);
        string frame = (id is long i ? $"id: {i.ToString(CultureInfo.InvariantCulture)}\n" : "") + $"event: {evt.Type}\ndata: {json}\n\n";
        await response.WriteAsync(frame, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>Reads the next item, writing keep-alive comments while waiting. Returns false when the channel completes.</summary>
    public static async Task<(bool Ok, T? Item)> NextAsync<T>(ChannelReader<T> reader, HttpResponse response, CancellationToken ct)
    {
        while (true)
        {
            if (reader.TryRead(out T? item)) return (true, item);
            Task<bool> wait = reader.WaitToReadAsync(ct).AsTask();
            while (await Task.WhenAny(wait, Task.Delay(KeepAlive, ct)) != wait)
            {
                await response.WriteAsync(": keep-alive\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
            if (!await wait) return (false, default);
        }
    }
}
