using System.Net;
using System.Net.Sockets;
using System.Text;
using Harness.Core.Agents;
using Harness.Tools;

namespace Harness.Tests;

public sealed class WebToolsTests
{
    [Fact]
    public void Html_becomes_markdown_like_text_of_the_main_content()
    {
        (string? title, string text) = HtmlText.Convert("""
            <!doctype html><html><head><title> Getting   started </title><style>p { color: red }</style></head>
            <body>
              <header><a href="/">Site</a> <nav><a href="/a">A</a></nav></header>
              <main>
                <h1>Install</h1>
                <p>Run the   <code>dotnet  tool</code> command,
                   then read the <a href="guide/next.html">next step</a>.</p>
                <ul><li>one</li><li>two<ol start="3"><li>three</li><li>four</li></ol></li></ul>
                <pre>dotnet build
              --no-restore</pre>
                <table><tr><th>Key</th><th>Value</th></tr><tr><td>a</td><td>1</td></tr></table>
                <p hidden>secret</p><script>alert(1)</script>
                <a href="/x"><img src="x.png"></a><a href="#top">Top</a> <a href="javascript:void(0)">Nope</a>
              </main>
              <footer>Copyright</footer>
            </body></html>
            """, new Uri("https://docs.example.com/start/index.html"));

        Assert.Equal("Getting started", title);
        Assert.Equal("""
            # Install

            Run the `dotnet tool` command, then read the [next step](https://docs.example.com/start/guide/next.html).

            - one
            - two
              3. three
              4. four

            ```
            dotnet build
              --no-restore
            ```

            Key | Value
            a | 1

            Top Nope
            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Html_without_main_keeps_article_headers_but_drops_the_page_header()
    {
        (_, string text) = HtmlText.Convert("""
            <body><header>Logo</header><div><section><header><h2>Title</h2></header><p>Body</p></section></div><aside>Ads</aside></body>
            """, null);
        Assert.Equal("## Title\n\nBody", text);
    }

    [Fact]
    public void DuckDuckGo_results_are_parsed_unwrapped_and_ads_skipped()
    {
        SearchResults results = DuckDuckGoSearch.Parse("""
            <div class="serp__results"><div class="results">
              <div class="result results_links results_links_deep result--ad">
                <h2 class="result__title"><a class="result__a" href="https://duckduckgo.com/y.js?ad_domain=x">Buy now</a></h2>
              </div>
              <div class="result results_links results_links_deep web-result ">
                <h2 class="result__title"><a rel="nofollow" class="result__a" href="https://github.com/microsoft/agent-framework">GitHub - <b>agent</b>-framework</a></h2>
                <a class="result__snippet" href="https://github.com/microsoft/agent-framework">A <b>framework</b> for building
                  AI agents.</a>
              </div>
              <div class="result results_links results_links_deep web-result ">
                <h2 class="result__title"><a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Flearn.microsoft.com%2Fen-us%2Fagent-framework%2F&amp;rut=abc">Overview</a></h2>
              </div>
            </div></div>
            """);

        Assert.Equal(
        [
            new SearchHit("GitHub - agent-framework", "https://github.com/microsoft/agent-framework", "A framework for building AI agents."),
            new SearchHit("Overview", "https://learn.microsoft.com/en-us/agent-framework/", ""),
        ], results.Hits);
    }

    [Fact]
    public async Task Search_results_list_title_url_and_snippet_under_a_header()
    {
        StubEngine engine = new(new SearchResults(
            [.. Enumerable.Range(1, 12).Select(i => new SearchHit($"Page {i}", $"https://example.com/{i}", i == 1 ? "First." : ""))], ["42"]));
        WebTools tools = new(engine, new HttpClient(new StubHandler()));

        string result = await tools.WebSearch("meaning of life", count: 2, recency: "Week");

        Assert.Equal("""
            2 results for "meaning of life" from the last week · stub · read a page with web_fetch
            answer: 42

            1. Page 1
               https://example.com/1
               First.

            2. Page 2
               https://example.com/2

            """.ReplaceLineEndings("\n"), result);
        Assert.Equal(("meaning of life", "week"), engine.Last);
        Assert.StartsWith("Error: recency must be", await tools.WebSearch("x", recency: "decade"));
    }

    [Fact]
    public async Task Searxng_is_asked_for_json_and_its_refusals_explained()
    {
        StubHandler handler = new();
        handler.Respond("http://searx.local/search?format=json&q=a%20b&time_range=month", HttpStatusCode.OK, "application/json", """
            { "results": [ { "url": "https://a.example/", "title": "A  page", "content": "About a." } ], "answers": [ { "answer": "yes" } ] }
            """);
        HttpClient http = new(handler);
        SearchResults results = await new SearxngSearch(http, "http://searx.local/").SearchAsync("a b", "month", default);
        Assert.Equal([new SearchHit("A page", "https://a.example/", "About a.")], results.Hits);
        Assert.Equal(["yes"], results.Answers);

        handler.Respond("http://searx.local/search?format=json&q=x", HttpStatusCode.Forbidden, "text/html", "");
        WebTools tools = new(new SearxngSearch(http, "http://searx.local"), http);
        Assert.Equal("Error: SearXNG refused the JSON format; add json to search.formats in its settings.yml.", await tools.WebSearch("x"));
    }

    [Fact]
    public async Task Fetch_renders_pages_follows_same_site_redirects_and_reports_others()
    {
        StubHandler handler = new();
        handler.Redirect("http://example.com/old", "https://www.example.com/new");
        handler.Respond("https://www.example.com/new", HttpStatusCode.OK, "text/html; charset=utf-8",
            "<title>New</title><body><p>Moved here. <a href=\"/more\">More</a></p></body>");
        handler.Redirect("https://www.example.com/away", "https://elsewhere.example/");
        handler.Respond("https://example.com/data.json", HttpStatusCode.OK, "application/json", "{\"a\":1}");
        handler.Respond("https://example.com/logo.png", HttpStatusCode.OK, "image/png", "\u0089PNG");
        handler.Respond("https://example.com/gone", HttpStatusCode.NotFound, "text/html", "<p>No such page.</p>");
        WebTools tools = new(new StubEngine(new([], [])), new HttpClient(handler)) { AllowPrivateAddresses = true };

        Assert.Equal("https://www.example.com/new · New · 48 chars\nMoved here. [More](https://www.example.com/more)",
            await tools.WebFetch("http://example.com/old"));
        Assert.Equal("https://www.example.com/away · redirects to https://elsewhere.example/ · another host: call web_fetch with that URL to follow it",
            await tools.WebFetch("https://www.example.com/away"));
        Assert.Equal("https://example.com/data.json · 7 chars\n{\"a\":1}", await tools.WebFetch("https://example.com/data.json"));
        Assert.Equal("https://example.com/logo.png · image/png, 5 bytes · not text, not shown", await tools.WebFetch("https://example.com/logo.png"));
        Assert.Equal("https://example.com/gone · HTTP 404 Not Found · 13 chars\nNo such page.",await tools.WebFetch("https://example.com/gone"));
        Assert.Equal("Error: url must be an absolute http or https URL.", await tools.WebFetch("file:///etc/passwd"));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("100.128.0.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.100.100.100", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("fd7a:115c:a1e0::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("2002:a00:1::", false)]
    public void Only_public_addresses_are_public(string address, bool expected) =>
        Assert.Equal(expected, PublicAddress.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task Fetch_refuses_private_addresses_before_and_at_connect()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        WebTools tools = new(new Harness.Core.Config.WebToolsConfig());
        Assert.Equal($"Error: 127.0.0.1 is 127.0.0.1, which is not a public address; web_fetch only reaches the public internet.",
            await tools.WebFetch($"http://127.0.0.1:{port}/"));
        Assert.Contains("not a public address", await tools.WebFetch($"http://localhost:{port}/"));

        // The connection itself is guarded too, so a name that resolves differently the second time gets nowhere.
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => WebHttp.Fetch.GetAsync($"http://127.0.0.1:{port}/"));
        Assert.Contains("not a public address", ex.Message);
        Assert.False(listener.Pending());
    }

    [Fact]
    public void Approvals_show_the_url_of_a_fetch()
    {
        Dictionary<string, object?> arguments = new() { ["url"] = "https://example.com/" };
        Assert.Equal("https://example.com/", ApprovalPolicy.MainArgument("web_fetch", arguments));
    }

    private sealed class StubEngine(SearchResults results) : ISearchEngine
    {
        public (string Query, string? Recency) Last { get; private set; }
        public string Name => "stub";

        public Task<SearchResults> SearchAsync(string query, string? recency, CancellationToken ct)
        {
            Last = (query, recency);
            return Task.FromResult(results);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];

        public void Respond(string url, HttpStatusCode status, string contentType, string body) => _routes[url] = () =>
        {
            ByteArrayContent content = new(Encoding.UTF8.GetBytes(body));
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return new HttpResponseMessage(status) { Content = content };
        };

        public void Redirect(string url, string location) => _routes[url] = () =>
            new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) }, Content = new ByteArrayContent([]) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_routes.TryGetValue(request.RequestUri!.AbsoluteUri, out Func<HttpResponseMessage>? route)
                ? route()
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
    }
}
