using System.Net;
using System.Net.Http.Json;
using Harness.Client;
using Harness.Server;
using Harness.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;

namespace Harness.Tests;

/// <summary>
/// Browser sign-in on the API listener, driven with a cookie-keeping HttpClient. Passkey ceremonies need a real
/// authenticator and are checked end to end with Chromium (tests/e2e/passkey.mjs); everything around them is here.
/// </summary>
public class IdentityTests
{
    private static int FreePort()
    {
        using System.Net.Sockets.TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static HttpClient Browser(string baseUrl) =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) { BaseAddress = new Uri(baseUrl) };

    private static async Task<(HttpStatusCode Status, string Body)> Post(HttpClient http, string path, object body, string? origin = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: HarnessClient.Json) };
        if (origin is not null) request.Headers.Add("Origin", origin);
        using HttpResponseMessage response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Password_sessions_read_and_run_but_need_a_passkey_step_up_for_more()
    {
        await using TestHome home = new();
        int port = FreePort();
        string baseUrl = $"http://127.0.0.1:{port}";
        File.AppendAllText(home.Paths.ConfigFile, $"\nlisteners:\n  api: {baseUrl}\n");
        await using WebApplication app = HarnessServer.Build(home.Paths);
        await app.StartAsync();
        try
        {
            using HarnessClient local = HarnessClient.ForSocket(home.Paths.Socket);
            string code = (await local.NewSetupCodeAsync()).Code;
            using HttpClient browser = Browser(baseUrl);

            Assert.Equal(HttpStatusCode.Forbidden, (await Post(browser, "/auth/setup", new { code = "guess", userName = "sam", password = "correct horse battery" })).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(browser, "/auth/setup", new { code, userName = "sam", password = "short" })).Status);
            Assert.Equal(HttpStatusCode.OK, (await Post(browser, "/auth/setup", new { code, userName = "sam", password = "correct horse battery" }, origin: baseUrl)).Status);
            Assert.Equal(HttpStatusCode.Conflict, (await Assert.ThrowsAsync<HarnessApiException>(() => local.NewSetupCodeAsync())).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Post(Browser(baseUrl), "/auth/setup", new { code, userName = "eve", password = "correct horse battery" })).Status);

            string status = await browser.GetStringAsync("/auth/status");
            Assert.Contains("\"role\":\"owner\"", status);
            Assert.Contains("\"method\":\"password\"", status);
            Assert.Contains("\"steppedUp\":false", status);

            // A password session can read and start work…
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/sessions")).StatusCode);
            new Harness.Core.Config.ConfigCatalog(home.Paths).SetWorkspace("ws", home.Workspace);
            Assert.Equal(HttpStatusCode.Created, (await Post(browser, "/api/sessions", new { workspaceName = "ws" }, origin: baseUrl)).Status);
            // …but approvals, trigger fires, admin calls and a second passkey need a fresh passkey.
            foreach (string path in new[] { "/api/runs/r_x/approvals/q_x", "/api/triggers/nightly/fire", "/api/retention/sweep" })
            {
                (HttpStatusCode s, string body) = await Post(browser, path, new { approved = true, dryRun = true });
                Assert.Equal(HttpStatusCode.Forbidden, s);
                Assert.Contains("step-up required", body);
            }
            // Token management in the browser: the owner after a step-up; minting stays on the machine.
            Assert.Contains("step-up required", await (await browser.GetAsync("/api/tokens")).Content.ReadAsStringAsync());
            Assert.Contains("local socket", (await Post(browser, "/api/tokens", new { name = "x" }, origin: baseUrl)).Body);
            // A page on another origin cannot use the cookie to change anything.
            Assert.Equal(HttpStatusCode.Forbidden, (await Post(browser, "/api/sessions", new { workspaceName = "ws" }, origin: "http://evil.example")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Post(browser, "/auth/signout", new { }, origin: "http://evil.example")).Status);
            // Passkey options are served; registration itself needs the browser's authenticator.
            HttpResponseMessage options = await browser.PostAsync("/auth/passkey/creation-options", null);
            Assert.Equal(HttpStatusCode.OK, options.StatusCode);
            string json = await options.Content.ReadAsStringAsync();
            Assert.Contains("\"challenge\"", json);
            Assert.Contains("\"name\":\"sam\"", json);

            // Wrong passwords lock the account; recovery happens on the machine and ends existing sessions.
            using HttpClient attacker = Browser(baseUrl);
            for (int i = 0; i < 4; i++)
                Assert.Equal(HttpStatusCode.Unauthorized, (await Post(attacker, "/auth/password", new { userName = "sam", password = "wrong password!!" })).Status);
            Assert.Equal((HttpStatusCode)429, (await Post(attacker, "/auth/password", new { userName = "sam", password = "wrong password!!" })).Status);
            Assert.Equal((HttpStatusCode)429, (await Post(attacker, "/auth/password", new { userName = "sam", password = "correct horse battery" })).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(attacker, "/auth/password", new { userName = "nobody", password = "x" })).Status);
            Assert.True(Assert.Single(await local.UsersAsync()).LockedOut);

            TemporaryPasswordDto reset = await local.ResetPasswordAsync("sam");
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/sessions")).StatusCode);   // old cookie is dead
            Assert.Equal(HttpStatusCode.OK, (await Post(attacker, "/auth/password", new { userName = "sam", password = reset.Password })).Status);
            Assert.Equal(HttpStatusCode.OK, (await attacker.GetAsync("/api/sessions")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Post(attacker, "/auth/signout", new { })).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync("/api/sessions")).StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Behind_a_local_tls_proxy_the_forwarded_scheme_counts()
    {
        await using TestHome misconfigured = new();
        File.AppendAllText(misconfigured.Paths.ConfigFile, "\nlisteners:\n  api: http://127.0.0.1:7443\n  behindProxy: true\n");
        Assert.Contains("publicHost", Assert.Throws<Harness.Core.Config.ConfigException>(() => HarnessServer.Build(misconfigured.Paths)).Message);

        await using TestHome home = new();
        int port = FreePort();
        string baseUrl = $"http://127.0.0.1:{port}";
        string page = $"https://127.0.0.1:{port}";   // what the browser sees: the proxy keeps the Host header
        File.AppendAllText(home.Paths.ConfigFile, $"\nlisteners:\n  api: {baseUrl}\n  publicHost: box.tailnet.ts.net\n  behindProxy: true\n");
        await using WebApplication app = HarnessServer.Build(home.Paths);
        await app.StartAsync();
        try
        {
            using HarnessClient local = HarnessClient.ForSocket(home.Paths.Socket);
            string code = (await local.NewSetupCodeAsync()).Code;
            using HttpClient proxy = new(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(baseUrl) };
            async Task<HttpResponseMessage> Setup(string? forwardedProto)
            {
                HttpRequestMessage request = new(HttpMethod.Post, "/auth/setup")
                {
                    Content = JsonContent.Create(new { code, userName = "sam", password = "correct horse battery" }, options: HarnessClient.Json),
                };
                request.Headers.Add("Origin", page);
                request.Headers.Add("X-Forwarded-For", "100.64.0.7");
                if (forwardedProto is not null) request.Headers.Add("X-Forwarded-Proto", forwardedProto);
                return await proxy.SendAsync(request);
            }

            // Without the proxy's header, the https page looks like another origin.
            Assert.Equal(HttpStatusCode.Forbidden, (await Setup(null)).StatusCode);
            using HttpResponseMessage ok = await Setup("https");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains("secure", ok.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("harness=", StringComparison.Ordinal)));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task The_web_app_is_served_from_the_binary_with_a_strict_policy()
    {
        await using TestHome home = new();
        int port = FreePort();
        string baseUrl = $"http://127.0.0.1:{port}";
        File.AppendAllText(home.Paths.ConfigFile, $"\nlisteners:\n  api: {baseUrl}\n");
        await using WebApplication app = HarnessServer.Build(home.Paths);
        await app.StartAsync();
        try
        {
            using HttpClient anonymous = new() { BaseAddress = new Uri(baseUrl) };
            foreach ((string path, string type) in new[]
            {
                ("/", "text/html"), ("/setup", "text/html"), ("/app.js", "text/javascript"), ("/style.css", "text/css"),
                ("/sw.js", "text/javascript"), ("/manifest.webmanifest", "application/manifest+json"), ("/icon.svg", "image/svg+xml"),
            })
            {
                using HttpResponseMessage r = await anonymous.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.StartsWith(type, r.Content.Headers.ContentType!.ToString());
                Assert.Contains("script-src 'self'", r.Headers.GetValues("Content-Security-Policy").Single());
                Assert.Contains("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
                Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
            }
            Assert.Contains("\"display\": \"standalone\"", await anonymous.GetStringAsync("/manifest.webmanifest"));
            Assert.Contains("/api/", await anonymous.GetStringAsync("/sw.js"));   // never caches API responses
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/nope.js")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sessions")).StatusCode);
            Assert.Contains("\"setupNeeded\":true", await anonymous.GetStringAsync("/auth/status"));
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
