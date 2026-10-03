using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Client;
using Harness.Core.Agents;
using Harness.Server;
using Harness.Server.Auth;
using Harness.Server.Push;
using Harness.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Tests;

public class PushTests
{
    // Produced by Node's http_ece (the library behind web-push) from the same fixed keys and salt.
    private const string UaPrivate = "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc";
    private const string UaPublic = "BB4YUy_UdUwC8wQdnHXOszuD_9gax85P6ILMscmLxYlupGwxHE4v9A3ZajZT5uRURdMt_khuztdcepDGoYiBwKM";
    private const string AsPrivate = "CQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQk";
    private const string Auth = "AwMDAwMDAwMDAwMDAwMDAw";
    private const string Salt = "BQUFBQUFBQUFBQUFBQUFBQ";
    private const string Payload = "{\"title\":\"Approval needed: shell\",\"body\":\"git push\"}";
    private const string Expected = "BQUFBQUFBQUFBQUFBQUFBQAAEABBBHE1-k_ZOgnc6Yu_aBtL_PUOfA1jVOYq-wv_KjQpYXhl7UwfAt25Aj7lalV-UV1qncZsEfIglg3llDNN9Yh3ZyQ90jNoSEkYrtQ3I5pveodBlLr0uaQj7vmnhvqHPeGVWFPlT6rTZELJXozqU2a4eRtvK-btsiwfEqBkUmpROgWouHnGpsU";

    [Fact]
    public void Payload_encryption_matches_an_independent_implementation()
    {
        using ECDiffieHellman sender = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = WebPushCrypto.FromB64(AsPrivate) });
        byte[] body = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes(Payload), WebPushCrypto.FromB64(UaPublic), WebPushCrypto.FromB64(Auth),
            sender, WebPushCrypto.FromB64(Salt));
        Assert.Equal(Expected, WebPushCrypto.B64(body));
    }

    /// <summary>What the browser does with a push message (RFC 8291 from the receiving side), for checking delivered bodies.</summary>
    private static string Decrypt(byte[] body, string uaPrivate, string auth)
    {
        byte[] salt = body[..16];
        int idLength = body[20];
        byte[] asPublic = body[21..(21 + idLength)];
        using ECDiffieHellman ua = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = WebPushCrypto.FromB64(uaPrivate) });
        byte[] uaPublic = WebPushCrypto.PublicPoint(ua.ExportParameters(false));
        using ECDiffieHellman peer = ECDiffieHellman.Create(WebPushCrypto.FromPoint(asPublic));
        byte[] secret = ua.DeriveRawSecretAgreement(peer.PublicKey);
        byte[] ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, WebPushCrypto.FromB64(auth), [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic]);
        byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        byte[] cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        byte[] nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());
        byte[] record = body[(21 + idLength)..];
        byte[] plain = new byte[record.Length - 16];
        using (AesGcm aes = new(cek, 16)) aes.Decrypt(nonce, record[..^16], record[^16..], plain);
        Assert.Equal(0x02, plain[^1]);
        return Encoding.UTF8.GetString(plain[..^1]);
    }

    [Fact]
    public void Vapid_tokens_are_es256_signed_for_the_push_service()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DateTimeOffset now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        string header = WebPushCrypto.VapidAuthorization("https://fcm.googleapis.com/fcm/send/abc", key, "https://box.example", now);
        Assert.Matches("^vapid t=[^.]+\\.[^.]+\\.[^,]+, k=[A-Za-z0-9_-]+$", header);
        string jwt = header["vapid t=".Length..header.IndexOf(',')];
        string[] parts = jwt.Split('.');
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), WebPushCrypto.FromB64(parts[2]), HashAlgorithmName.SHA256));
        using JsonDocument claims = JsonDocument.Parse(WebPushCrypto.FromB64(parts[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("https://box.example", claims.RootElement.GetProperty("sub").GetString());
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
        Assert.Equal(WebPushCrypto.B64(WebPushCrypto.PublicPoint(key.ExportParameters(false))), header[(header.IndexOf("k=") + 2)..]);
    }

    /// <summary>Stands in for push services: records what was sent and answers with a chosen status.</summary>
    private sealed class FakePushService : HttpMessageHandler
    {
        public List<(string Endpoint, byte[] Body, HttpRequestMessage Request)> Sent { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            byte[] body = await request.Content!.ReadAsByteArrayAsync(ct);
            lock (Sent) Sent.Add((request.RequestUri!.ToString(), body, request));
            return new HttpResponseMessage(Status);
        }
    }

    [Fact]
    public async Task Waiting_approvals_notify_subscribed_devices_and_gone_subscriptions_are_dropped()
    {
        await using TestHome home = new();
        ScriptedChatClient model = new((m, _) => ScriptedChatClient.LastResults(m).Count == 0
            ? ScriptedChatClient.Call("shell", new() { ["command"] = "git push" })
            : ScriptedChatClient.Text("pushed"));
        FakePushService service = new();
        await using WebApplication app = HarnessServer.Build(home.Paths, services => services.AddSingleton(sp => new PushSender(
            sp.GetRequiredService<AuthStore>(), sp.GetRequiredService<VapidKeys>(), new HttpClient(service), "mailto:harness@localhost",
            NullLogger<PushSender>.Instance)));
        app.Services.GetRequiredService<AgentFactory>().ChatClientOverride = _ => model;
        await app.StartAsync();
        try
        {
            using HarnessClient client = HarnessClient.ForSocket(home.Paths.Socket);
            AuthStore store = app.Services.GetRequiredService<AuthStore>();
            Assert.Throws<ArgumentException>(() => store.AddPushSubscription(new PushSubscription("http://insecure.example/x", UaPublic, Auth), "sam"));
            store.AddPushSubscription(new PushSubscription("https://push.example/phone", UaPublic, Auth), "sam");
            Assert.Equal(WebPushCrypto.B64(WebPushCrypto.PublicPoint(app.Services.GetRequiredService<VapidKeys>().Signer().ExportParameters(false))),
                app.Services.GetRequiredService<VapidKeys>().PublicKey);

            SessionDto session = await client.CreateSessionAsync(new CreateSessionRequest(Workspace: home.Workspace));
            SendMessageResponse sent = await client.SendAsync(session.Id, "push it");
            for (int i = 0; i < 100 && service.Sent.Count == 0; i++) await Task.Delay(50);
            (string endpoint, byte[] body, HttpRequestMessage request) = Assert.Single(service.Sent);
            Assert.Equal("https://push.example/phone", endpoint);
            Assert.Equal("aes128gcm", Assert.Single(request.Content!.Headers.ContentEncoding));
            Assert.Equal("high", request.Headers.GetValues("Urgency").Single());
            Assert.StartsWith("vapid t=", request.Headers.GetValues("Authorization").Single());
            using JsonDocument message = JsonDocument.Parse(Decrypt(body, UaPrivate, Auth));
            Assert.Equal("Approval needed: shell", message.RootElement.GetProperty("title").GetString());
            Assert.Equal("git push", message.RootElement.GetProperty("body").GetString());
            Assert.Equal($"/#/run/{sent.RunId}", message.RootElement.GetProperty("url").GetString());

            // The push service says the subscription is gone: it is forgotten.
            service.Status = HttpStatusCode.Gone;
            ApprovalDto pending = Assert.Single(await client.ApprovalsAsync());
            await client.DecideAsync(sent.RunId, pending.RequestId, new ApprovalDecisionRequest(true));
            Assert.Equal(0, await app.Services.GetRequiredService<PushSender>().SendAsync(new { title = "x" }, false, CancellationToken.None));
            Assert.Empty(store.PushSubscriptions());
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
