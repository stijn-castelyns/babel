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
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/tokens")).StatusCode);   // local only, whoever asks
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
}
