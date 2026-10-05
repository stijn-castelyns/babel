using System.ComponentModel;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Harness.Core.Config;

namespace Harness.Tools;

/// <summary>
/// <c>web_search</c> and <c>web_fetch</c>. Search goes to the configured SearXNG instance, else to DuckDuckGo's HTML page;
/// neither needs an API key. Fetch reaches public addresses only and reports a redirect to another host instead of
/// following it, so an approval or allowlist entry for one site never lets a run read another.
/// </summary>
public sealed class WebTools
{
    public const int DefaultResults = 8;
    public const int MaxResults = 20;
    public const int MaxFetchBytes = 5 * 1024 * 1024;
    public const int MaxRedirects = 5;
    public const int ErrorBodyChars = 2_000;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ISearchEngine _engine;
    private readonly HttpClient _fetch;

    public WebTools(WebToolsConfig config) : this(
        string.IsNullOrWhiteSpace(config.Searxng) ? new DuckDuckGoSearch(WebHttp.Search) : new SearxngSearch(WebHttp.Search, config.Searxng),
        WebHttp.Fetch)
    {
    }

    internal WebTools(ISearchEngine engine, HttpClient fetch)
    {
        _engine = engine;
        _fetch = fetch;
    }

    /// <summary>Skips the public-address check; tests serve pages from loopback.</summary>
    internal bool AllowPrivateAddresses { get; init; }

    [Description("Search the web. Returns titles, URLs and snippets; read a result with web_fetch. Narrow a search with site:example.com in the query.")]
    public async Task<string> WebSearch(
        [Description("The search query")] string query,
        [Description("How many results to return (default 8, max 20)")] int count = DefaultResults,
        [Description("Only results from the last day, week, month or year")] string? recency = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        recency = string.IsNullOrWhiteSpace(recency) ? null : recency.Trim().ToLowerInvariant();
        if (recency is not (null or "day" or "week" or "month" or "year")) return "Error: recency must be day, week, month or year.";
        count = Math.Clamp(count, 1, MaxResults);

        SearchResults results;
        try { results = await _engine.SearchAsync(query, recency, cancellationToken); }
        catch (SearchException ex) { return $"Error: {ex.Message}"; }
        catch (HttpRequestException ex) { return $"Error: {_engine.Name} search failed: {ex.Message}"; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return $"Error: {_engine.Name} did not answer in time."; }

        IReadOnlyList<SearchHit> hits = [.. results.Hits.Take(count)];
        StringBuilder sb = new();
        sb.Append(hits.Count == 0 ? "no results" : hits.Count == 1 ? "1 result" : $"{hits.Count} results");
        sb.Append($" for \"{query}\"{(recency is null ? "" : $" from the last {recency}")} · {_engine.Name}");
        sb.Append(hits.Count == 0 ? " · try other words, or no recency" : " · read a page with web_fetch").Append('\n');
        foreach (string answer in results.Answers) sb.Append("answer: ").Append(answer).Append('\n');
        for (int i = 0; i < hits.Count; i++)
        {
            sb.Append('\n').Append(i + 1).Append(". ").Append(hits[i].Title).Append('\n');
            sb.Append("   ").Append(hits[i].Url).Append('\n');
            if (hits[i].Snippet.Length > 0) sb.Append("   ").Append(hits[i].Snippet).Append('\n');
        }
        return sb.ToString();
    }

    [Description("Fetch a web page and return its main content as Markdown-like text, links included. Only public http and https addresses; a redirect to another host is reported, not followed.")]
    public async Task<string> WebFetch(
        [Description("The http or https URL")] string url,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            return "Error: url must be an absolute http or https URL.";
        try
        {
            for (int hop = 0; ; hop++)
            {
                if (!AllowPrivateAddresses && await WebHttp.RefusalAsync(uri, cancellationToken) is { } refusal) return $"Error: {refusal}";
                using HttpRequestMessage request = new(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5");
                using HttpResponseMessage response = await _fetch.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (!Uri.TryCreate(uri, location, out Uri? next) || next.Scheme is not ("http" or "https"))
                        return $"Error: {uri} redirects to '{location}', which is not an http or https URL.";
                    if (!SameSite(uri, next))
                        return $"{uri.AbsoluteUri} · redirects to {next.AbsoluteUri} · another host: call web_fetch with that URL to follow it";
                    if (hop == MaxRedirects) return $"Error: {url} redirected more than {MaxRedirects} times.";
                    uri = next;
                    continue;
                }
                return await RenderAsync(uri, response, cancellationToken);
            }
        }
        catch (HttpRequestException ex) { return $"Error: could not fetch {uri.AbsoluteUri}: {ex.Message}"; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return $"Error: {uri.AbsoluteUri} did not answer in time."; }
    }

    private static async Task<string> RenderAsync(Uri uri, HttpResponseMessage response, CancellationToken ct)
    {
        (byte[] body, bool cut) = await ReadLimitedAsync(response.Content, ct);
        MediaTypeHeaderValue? type = response.Content.Headers.ContentType;
        string media = type?.MediaType?.ToLowerInvariant() ?? "";
        StringBuilder header = new(uri.AbsoluteUri);
        if (!response.IsSuccessStatusCode) header.Append($" · HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        string? title = null;
        string text;
        if (media is "text/html" or "application/xhtml+xml" || media.Length == 0 && LooksLikeHtml(body))
            (title, text) = HtmlText.Convert(Decode(body, type), uri);
        else if (IsText(media) || media.Length == 0 && !FileTools.IsBinary(body))
            text = Decode(body, type).Trim();
        else
            return header.Append($" · {(media.Length == 0 ? "binary" : media)}, {body.Length.ToString("N0", Inv)}{(cut ? "+" : "")} bytes · not text, not shown").ToString();

        if (!response.IsSuccessStatusCode && text.Length > ErrorBodyChars) text = text[..ErrorBodyChars] + "\n…";
        if (title is not null) header.Append(" · ").Append(title);
        header.Append(" · ").Append(text.Length.ToString("N0", Inv)).Append(" chars");
        if (cut) header.Append($" · page cut at {MaxFetchBytes / 1024 / 1024} MB");
        return header.Append('\n').Append(text.Length == 0 ? "(no text)" : text).ToString();
    }

    private static async Task<(byte[] Body, bool Cut)> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using Stream stream = await content.ReadAsStreamAsync(ct);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            int keep = Math.Min(read, MaxFetchBytes - (int)buffer.Length);
            buffer.Write(chunk, 0, keep);
            if (keep < read) return (buffer.ToArray(), true);
        }
        return (buffer.ToArray(), false);
    }

    private static string Decode(byte[] body, MediaTypeHeaderValue? type)
    {
        Encoding encoding = Encoding.UTF8;
        if (type?.CharSet is { Length: > 0 } charset)
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { }
        return encoding.GetString(body);
    }

    private static bool IsText(string media) =>
        media.StartsWith("text/", StringComparison.Ordinal) || media.EndsWith("+json", StringComparison.Ordinal) || media.EndsWith("+xml", StringComparison.Ordinal)
        || media is "application/json" or "application/xml" or "application/javascript" or "application/x-yaml" or "application/yaml" or "application/toml";

    private static bool LooksLikeHtml(byte[] body)
    {
        string start = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 512)).TrimStart().ToLowerInvariant();
        return start.StartsWith("<!doctype html", StringComparison.Ordinal) || start.StartsWith("<html", StringComparison.Ordinal);
    }

    /// <summary>The same host, give or take <c>www.</c>: <c>example.com</c> → <c>www.example.com</c> is followed.</summary>
    private static bool SameSite(Uri from, Uri to) =>
        string.Equals(StripWww(from.IdnHost), StripWww(to.IdnHost), StringComparison.OrdinalIgnoreCase);

    private static string StripWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
