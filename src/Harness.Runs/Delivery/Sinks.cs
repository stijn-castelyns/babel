using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Harness.Core;
using Harness.Sdk;

namespace Harness.Runs.Delivery;

/// <summary><c>{ type: file, path: "~/reports/deps-{date}.md", from: output.summary, append: false }</c>.</summary>
public sealed class FileSink(HarnessPaths paths) : IOutputSink
{
    public string Type => "file";

    public async Task DeliverAsync(RunResult result, SinkOptions options, CancellationToken cancellationToken)
    {
        string path = options.Settings["path"]?.ToString() ?? throw new InvalidOperationException("the file sink needs a path.");
        string target = paths.Resolve(path);   // ~ expands; relative paths are relative to the harness home
        string content = SinkContent.Select(result, options.Settings["from"]?.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (options.Settings["append"]?.ToString() == "true") await File.AppendAllTextAsync(target, content + "\n", cancellationToken);
        else await File.WriteAllTextAsync(target, content, cancellationToken);
    }
}

/// <summary>
/// <c>{ type: webhook, url: secret:teams-webhook, secret: secret:hook-key, headers: {...}, body: {...}, from: ... }</c>. POSTs the run's
/// result as JSON (or <c>body:</c>, rendered with the run's variables). With <c>secret:</c> the body is signed like incoming webhooks:
/// <c>X-Harness-Signature: sha256=&lt;hex HMAC&gt;</c>.
/// </summary>
public sealed class WebhookSink : IOutputSink
{
    private static readonly HttpClient Http = new();

    public string Type => "webhook";

    public async Task DeliverAsync(RunResult result, SinkOptions options, CancellationToken cancellationToken)
    {
        JsonObject s = options.Settings;
        string url = s["url"]?.ToString() ?? throw new InvalidOperationException("the webhook sink needs a url.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("the webhook url must be an absolute http(s) URL.");

        JsonNode body = s["body"]?.DeepClone() ?? new JsonObject
        {
            ["runId"] = result.RunId,
            ["sessionId"] = result.SessionId,
            ["triggerId"] = result.TriggerId,
            ["state"] = result.State,
            ["text"] = result.Text,
            ["output"] = result.Output?.DeepClone(),
            ["content"] = s["from"] is JsonValue from ? SinkContent.Select(result, from.ToString()) : null,
            ["files"] = new JsonArray([.. result.Files.Select(f => (JsonNode)Path.GetFileName(f))]),
            ["error"] = result.Error,
            ["inputTokens"] = result.InputTokens,
            ["outputTokens"] = result.OutputTokens,
        };
        byte[] payload = Encoding.UTF8.GetBytes(body.ToJsonString());

        using HttpRequestMessage request = new(new HttpMethod(s["method"]?.ToString() ?? "POST"), uri) { Content = new ByteArrayContent(payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        if (s["secret"]?.ToString() is { Length: > 0 } secret)
            request.Headers.Add("X-Harness-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)));
        if (s["headers"] is JsonObject headers)
            foreach ((string name, JsonNode? value) in headers)
                request.Headers.TryAddWithoutValidation(name, value?.ToString());

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}{(text.Length > 0 ? ": " + (text.Length > 300 ? text[..300] + "…" : text) : "")}");
        }
    }
}
