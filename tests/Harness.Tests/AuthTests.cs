using System.Net;
using Harness.Client;
using Harness.Core.Agents;
using Harness.Server;
using Harness.Server.Auth;
using Harness.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>Scoped tokens and device pairing on the API listener; the local socket keeps full access.</summary>
public class AuthTests
{
    private static int FreePort()
    {
        using System.Net.Sockets.TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static async Task<HttpStatusCode> StatusOf(Func<Task> call)
    {
        try
        {
            await call();
            return HttpStatusCode.OK;
        }
        catch (HarnessApiException ex)
        {
            return ex.Status;
        }
    }

    [Theory]
    [InlineData("GET", "/api/sessions", "read")]
    [InlineData("GET", "/api/runs/r_1/events", "read")]
    [InlineData("POST", "/api/sessions/s_1/messages", "run")]
    [InlineData("PATCH", "/api/triggers/nightly", "run")]
    [InlineData("POST", "/api/runs/r_1/approvals/q_1", "approve")]
    [InlineData("POST", "/api/retention/sweep", "admin")]
    [InlineData("POST", "/api/sandboxes/workspace/test", "admin")]
    [InlineData("POST", "/api/tokens", AccessPolicy.LocalOnly)]
    [InlineData("GET", "/api/tokens", AccessPolicy.LocalOnly)]
    [InlineData("POST", "/api/pairings/ABCD-EFGH", AccessPolicy.LocalOnly)]
    [InlineData("DELETE", "/api/tokens/self", AccessPolicy.Authenticated)]
    [InlineData("POST", "/api/pair", AccessPolicy.Anonymous)]
    [InlineData("POST", "/api/pair/token", AccessPolicy.Anonymous)]
    [InlineData("GET", "/api/pair", "read")]
    public void Every_route_needs_its_scope(string method, string path, string scope) =>
        Assert.Equal(scope, AccessPolicy.Required(method, new PathString(path)));

    [Fact]
    public async Task Pairing_mints_a_scoped_token_once_and_scopes_are_enforced()
    {
        await using TestHome home = new();
        int port = FreePort();
        File.AppendAllText(home.Paths.ConfigFile, $"\nlisteners:\n  api: http://127.0.0.1:{port}\n");
        new Harness.Core.Config.ConfigCatalog(home.Paths).SetWorkspace("ws", home.Workspace);
        ScriptedChatClient model = new((m, _) => ScriptedChatClient.LastResults(m).Count == 0
            ? ScriptedChatClient.Call("shell", new() { ["command"] = "echo hi" })
            : ScriptedChatClient.Text("done"));
        await using WebApplication app = HarnessServer.Build(home.Paths);
        app.Services.GetRequiredService<AgentFactory>().ChatClientOverride = _ => model;
        await app.StartAsync();
        try
        {
            Uri url = new($"http://127.0.0.1:{port}/");
            using HarnessClient local = HarnessClient.ForSocket(home.Paths.Socket);
            using HarnessClient anonymous = HarnessClient.ForUrl(url, null);
            Assert.Equal(HttpStatusCode.Unauthorized, await StatusOf(() => anonymous.SessionsAsync()));

            // The device asks; the person approves on the machine with the scopes it should get.
            PairStartResponse start = await anonymous.StartPairingAsync("laptop");
            Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", start.UserCode);
            Assert.Equal("pending", (await anonymous.PollPairingAsync(start.DeviceCode)).Status);
            Assert.Equal("laptop", Assert.Single(await local.PairingsAsync()).Name);
            Assert.Equal(HttpStatusCode.NotFound, await StatusOf(() => local.DecidePairingAsync("ZZZZ-ZZZZ", new ApprovePairingRequest())));
            await local.DecidePairingAsync(start.UserCode.ToLowerInvariant().Replace("-", ""), new ApprovePairingRequest(true, [ApiScopes.Read, ApiScopes.Run]));

            PairPollResponse approved = await anonymous.PollPairingAsync(start.DeviceCode);
            Assert.Equal("approved", approved.Status);
            Assert.Equal(["read", "run"], approved.Scopes);
            Assert.Equal("expired", (await anonymous.PollPairingAsync(start.DeviceCode)).Status);   // claimed once
            Assert.Equal("expired", (await anonymous.PollPairingAsync("not-a-device-code")).Status);

            using HarnessClient laptop = HarnessClient.ForUrl(url, approved.Token);
            WhoAmIDto me = await laptop.WhoAmIAsync();
            Assert.Equal("laptop", me.Name);
            Assert.False(me.Local);

            // run: start work. approve: missing, so the approval is refused, and admin and local-only routes too.
            SessionDto session = await laptop.CreateSessionAsync(new CreateSessionRequest(WorkspaceName: "ws"));
            SendMessageResponse sent = await laptop.SendAsync(session.Id, "go");
            ApprovalDto pending = await WaitForApproval(laptop, sent.RunId);
            Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(() => laptop.DecideAsync(sent.RunId, pending.RequestId, new ApprovalDecisionRequest(true))));
            Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(() => laptop.PruneAsync(dryRun: true)));
            Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(() => laptop.TokensAsync()));

            // An admin token still cannot mint tokens remotely; minting happens on the machine.
            CreatedTokenDto phone = await local.CreateTokenAsync(new CreateTokenRequest("phone", [ApiScopes.Read, ApiScopes.Approve, ApiScopes.Admin]));
            Assert.StartsWith("hst_t_", phone.Token);
            using HarnessClient phoneClient = HarnessClient.ForUrl(url, phone.Token);
            Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(() => phoneClient.CreateTokenAsync(new CreateTokenRequest("x"))));
            Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(() => phoneClient.SendAsync(session.Id, "no run scope")));
            await phoneClient.DecideAsync(sent.RunId, pending.RequestId, new ApprovalDecisionRequest(true, DecidedBy: "someone else"));
            RunDto done = await WaitFinished(local, sent.RunId);
            Assert.Equal("succeeded", done.State);
            List<EventDto> events = [];
            await foreach (EventDto e in local.RunEventsAsync(sent.RunId)) events.Add(e);
            Assert.Contains(events, e => e.Type == "APPROVAL_RESOLVED" && e.Data["decidedBy"]!.GetValue<string>() == "phone");   // not what the client claimed

            // A tampered secret, a revoked token and a logged-out device are all rejected.
            using HarnessClient tampered = HarnessClient.ForUrl(url, phone.Token[..^2] + "xx");
            Assert.Equal(HttpStatusCode.Unauthorized, await StatusOf(() => tampered.SessionsAsync()));
            await local.RevokeTokenAsync(phone.Info.Id);
            Assert.Equal(HttpStatusCode.Unauthorized, await StatusOf(() => phoneClient.SessionsAsync()));
            await laptop.RevokeSelfAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, await StatusOf(() => laptop.SessionsAsync()));
            IReadOnlyList<TokenDto> tokens = await local.TokensAsync();
            Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
            Assert.Contains(tokens, t => t.Origin == $"pairing {start.UserCode}" && t.LastUsedAt is not null);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<ApprovalDto> WaitForApproval(HarnessClient c, string runId)
    {
        for (int i = 0; i < 100; i++)
        {
            if ((await c.ApprovalsAsync()).FirstOrDefault(a => a.RunId == runId) is { } a) return a;
            await Task.Delay(50);
        }
        throw new TimeoutException("no approval");
    }

    private static async Task<RunDto> WaitFinished(HarnessClient c, string runId)
    {
        for (int i = 0; i < 100; i++)
        {
            RunDto r = await c.RunAsync(runId);
            if (r.FinishedAt is not null) return r;
            await Task.Delay(50);
        }
        throw new TimeoutException("run did not finish");
    }

    [Fact]
    public void Tokens_expire_and_pairings_time_out()
    {
        string root = Path.Combine(Path.GetTempPath(), "harness-tests", Guid.NewGuid().ToString("N")[..8]);
        Harness.Core.HarnessPaths paths = new(root);
        FakeClock clock = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        AuthStore store = new(paths, clock);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.AuthDatabase));

        CreatedTokenDto t = store.CreateToken("ci", [ApiScopes.Read], expiresInDays: 1);
        Assert.NotNull(store.Validate(t.Token));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(store.Validate(t.Token));
        Assert.Null(store.Validate("hst_nonsense"));
        Assert.Null(store.Validate("something-else"));

        PairStartResponse p = store.StartPairing("old", null);
        clock.Advance(AuthStore.PairingLifetime);
        Assert.Equal("expired", store.Poll(p.DeviceCode).Status);
        Assert.False(store.DecidePairing(p.UserCode, true, null, null));
        Assert.Empty(store.Pairings());

        PairStartResponse q = store.StartPairing(null, "10.0.0.2");
        Assert.True(store.DecidePairing(q.UserCode, false, null, null));
        Assert.Equal("denied", store.Poll(q.DeviceCode).Status);

        Assert.Throws<ArgumentException>(() => ApiScopes.Parse("read,root"));
        Assert.Equal(["read", "admin"], ApiScopes.Parse("admin, read"));
    }

    [Fact]
    public void Pairing_requests_are_rate_limited_per_address()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        AddressLimiter limiter = new(2, TimeSpan.FromMinutes(1), clock);
        IPAddress a = IPAddress.Parse("10.0.0.1"), b = IPAddress.Parse("10.0.0.2");
        Assert.True(limiter.Allow(a));
        Assert.True(limiter.Allow(a));
        Assert.False(limiter.Allow(a));
        Assert.True(limiter.Allow(b));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(limiter.Allow(a));
    }

    [Fact]
    public void Login_credentials_are_private_and_keyed_by_daemon()
    {
        string root = Path.Combine(Path.GetTempPath(), "harness-tests", Guid.NewGuid().ToString("N")[..8]);
        Harness.Core.HarnessPaths paths = new(root);
        Harness.Cli.Credentials store = new(paths);
        store.Set(new Uri("https://Box.example:7443/api/"), "hst_t_1_secret", "laptop");
        Assert.Equal("hst_t_1_secret", store.TokenFor(new Uri("https://box.example:7443/")));
        Assert.Null(store.TokenFor(new Uri("https://box.example:7444/")));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.CredentialsFile));
        Assert.True(store.Remove(new Uri("https://box.example:7443")));
        Assert.Null(store.TokenFor(new Uri("https://box.example:7443/")));
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
