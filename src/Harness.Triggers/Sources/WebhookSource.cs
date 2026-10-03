using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Sdk;
using Microsoft.AspNetCore.Http;

namespace Harness.Triggers.Sources;

/// <summary>
/// <c>POST /hooks/&lt;trigger-id&gt;</c> with an HMAC-SHA256 signature of the body in <c>X-Harness-Signature: sha256=&lt;hex&gt;</c>
/// (or GitHub's <c>X-Hub-Signature-256</c>), keyed with <c>source.secret</c>. Unsigned requests are rejected. A JSON body's
/// <c>text</c> and <c>sender</c> fields become the event's text and sender (for sender filters, rate limits and coalescing).
/// </summary>
public sealed class WebhookSource : ITriggerSource
{
    public const int MaxBodyBytes = 1024 * 1024;
    public string Type => "webhook";

    public Task StartAsync(TriggerSourceContext context, CancellationToken cancellationToken)
    {
        string secret = context.Settings["secret"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Webhook trigger '{context.TriggerId}' needs source.secret.");
        context.MapWebhook(context.TriggerId, http => HandleAsync(context, secret, http));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task HandleAsync(TriggerSourceContext context, string secret, HttpContext http)
    {
        if (!HttpMethods.IsPost(http.Request.Method)) { http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        if (http.Request.ContentLength > MaxBodyBytes) { http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }

        using MemoryStream buffer = new();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
        if (buffer.Length > MaxBodyBytes) { http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
        byte[] body = buffer.ToArray();

        string? signature = http.Request.Headers["X-Harness-Signature"].FirstOrDefault() ?? http.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!Verify(secret, body, signature)) { http.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }

        JsonObject data;
        try { data = JsonNode.Parse(body) as JsonObject ?? new JsonObject { ["body"] = Encoding.UTF8.GetString(body) }; }
        catch (JsonException) { data = new JsonObject { ["body"] = Encoding.UTF8.GetString(body) }; }

        string eventId = http.Request.Headers["X-Harness-Event-Id"].FirstOrDefault()
            ?? http.Request.Headers["X-GitHub-Delivery"].FirstOrDefault()
            ?? Convert.ToHexStringLower(SHA256.HashData(body))[..32];
        string? sender = data["sender"]?.GetValueKind() == JsonValueKind.String ? data["sender"]!.GetValue<string>() : null;
        await context.EmitAsync(new TriggerEvent(eventId, context.TriggerId, DateTimeOffset.UtcNow, sender,
            data["text"]?.GetValueKind() == JsonValueKind.String ? data["text"]!.GetValue<string>() : null, [], data, null), http.RequestAborted);
        http.Response.StatusCode = StatusCodes.Status202Accepted;
    }

    public static string Sign(string secret, byte[] body) => "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    public static bool Verify(string secret, byte[] body, string? signature)
    {
        if (signature is null) return false;
        byte[] expected = Encoding.ASCII.GetBytes(Sign(secret, body));
        byte[] given = Encoding.ASCII.GetBytes(signature.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
