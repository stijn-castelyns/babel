using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace Harness.Tools;

internal sealed record SearchHit(string Title, string Url, string Snippet);

internal sealed record SearchResults(IReadOnlyList<SearchHit> Hits, IReadOnlyList<string> Answers);

/// <summary>A search engine that needs no API key. A failure throws <see cref="SearchException"/> with what to do about it.</summary>
internal interface ISearchEngine
{
    string Name { get; }
    Task<SearchResults> SearchAsync(string query, string? recency, CancellationToken ct);
}

internal sealed class SearchException(string message) : Exception(message);

/// <summary>A self-hosted SearXNG instance, through its JSON API (<c>search.formats</c> must include <c>json</c>).</summary>
internal sealed class SearxngSearch(HttpClient http, string baseUrl) : ISearchEngine
{
    public string Name => "searxng";

    public async Task<SearchResults> SearchAsync(string query, string? recency, CancellationToken ct)
    {
        string url = baseUrl.TrimEnd('/') + "/search?format=json&q=" + Uri.EscapeDataString(query)
            + (recency is null ? "" : "&time_range=" + recency);
        using HttpResponseMessage response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new SearchException("SearXNG refused the JSON format; add json to search.formats in its settings.yml.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SearchException("SearXNG's limiter refused the request; turn server.limiter off for an instance only the harness uses.");
        if (!response.IsSuccessStatusCode)
            throw new SearchException($"SearXNG answered {(int)response.StatusCode} {response.ReasonPhrase}.");

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        List<SearchHit> hits = [];
        if (json.RootElement.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
            foreach (JsonElement r in results.EnumerateArray())
                if (Str(r, "url") is { } link)
                    hits.Add(new SearchHit(HtmlText.Collapse(Str(r, "title") ?? link), link, HtmlText.Collapse(Str(r, "content") ?? "")));

        // Older versions list answers as strings, newer ones as objects with an "answer".
        List<string> answers = [];
        if (json.RootElement.TryGetProperty("answers", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            foreach (JsonElement a in list.EnumerateArray())
                if ((a.ValueKind == JsonValueKind.String ? a.GetString() : Str(a, "answer")) is { Length: > 0 } text)
                    answers.Add(HtmlText.Collapse(text));
        return new SearchResults(hits, answers);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// DuckDuckGo's script-free HTML page, read like a browser would. No key or account, but it is a page rather than an API:
/// the markup can change, and busy addresses get a bot check instead of results.
/// </summary>
internal sealed class DuckDuckGoSearch(HttpClient http) : ISearchEngine
{
    public const string Endpoint = "https://html.duckduckgo.com/html/";

    public string Name => "duckduckgo";

    public async Task<SearchResults> SearchAsync(string query, string? recency, CancellationToken ct)
    {
        Dictionary<string, string> form = new() { ["q"] = query, ["kl"] = "wt-wt" };
        if (recency is not null) form["df"] = recency[..1];   // d, w, m, y
        using HttpResponseMessage response = await http.PostAsync(Endpoint, new FormUrlEncodedContent(form), ct);
        string html = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Accepted || html.Contains("anomaly-modal", StringComparison.Ordinal))
            throw new SearchException("DuckDuckGo answered with a bot check instead of results. Try again later, or point tools.web.searxng in config.yaml at a SearXNG instance.");
        if (!response.IsSuccessStatusCode)
            throw new SearchException($"DuckDuckGo answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        return Parse(html);
    }

    internal static SearchResults Parse(string html)
    {
        IHtmlDocument doc = HtmlText.Parse(html);
        List<SearchHit> hits = [];
        foreach (IElement result in doc.QuerySelectorAll("div.result"))
        {
            if (result.ClassList.Contains("result--ad")) continue;
            IElement? link = result.QuerySelector("a.result__a");
            if (link is null || Target(link.GetAttribute("href")) is not { } url) continue;
            string snippet = result.QuerySelector(".result__snippet")?.TextContent ?? "";
            hits.Add(new SearchHit(HtmlText.Collapse(link.TextContent), url, HtmlText.Collapse(snippet)));
        }
        return new SearchResults(hits, []);
    }

    /// <summary>The result's own URL: direct, or unwrapped from a <c>//duckduckgo.com/l/?uddg=…</c> redirect. Ads have neither.</summary>
    private static string? Target(string? href)
    {
        if (string.IsNullOrEmpty(href)) return null;
        if (!Uri.TryCreate(new Uri(Endpoint), href, out Uri? uri)) return null;
        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath != "/l/") return null;
            string? uddg = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                .FirstOrDefault(p => p.Length == 2 && p[0] == "uddg")?[1];
            if (uddg is null || !Uri.TryCreate(Uri.UnescapeDataString(uddg), UriKind.Absolute, out uri)) return null;
        }
        return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }
}
