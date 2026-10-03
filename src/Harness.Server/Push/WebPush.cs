using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Harness.Server.Push;

/// <summary>A browser's push subscription: where to post, and the keys to encrypt for (base64url, as the browser gives them).</summary>
public sealed record PushSubscription(string Endpoint, string P256dh, string Auth);

/// <summary>
/// Web Push without a library: payloads encrypted as RFC 8291 (<c>aes128gcm</c>) and requests signed with VAPID
/// (RFC 8292, ES256). Keys are P-256; the application server key pair is kept by <see cref="VapidKeys"/>.
/// </summary>
public static class WebPushCrypto
{
    private const int RecordSize = 4096;

    public static string B64(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64(string s)
    {
        string t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4));
    }

    /// <summary>The uncompressed point (0x04 || X || Y) of a P-256 key.</summary>
    public static byte[] PublicPoint(ECParameters p) => [0x04, .. p.Q.X!, .. p.Q.Y!];

    public static ECParameters FromPoint(byte[] point)
    {
        if (point.Length != 65 || point[0] != 0x04) throw new ArgumentException("Expected an uncompressed P-256 public key.");
        return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..65] } };
    }

    /// <summary>Encrypts one payload for a subscription: the whole <c>aes128gcm</c> body, header included.</summary>
    public static byte[] Encrypt(byte[] payload, byte[] uaPublic, byte[] authSecret, ECDiffieHellman? senderKey = null, byte[]? salt = null)
    {
        if (payload.Length > RecordSize - 16 - 1 - 86) throw new ArgumentException("A push payload must fit one 4 KB record.");
        using ECDiffieHellman sender = senderKey ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        salt ??= RandomNumberGenerator.GetBytes(16);
        byte[] asPublic = PublicPoint(sender.ExportParameters(false));
        using ECDiffieHellman ua = ECDiffieHellman.Create(FromPoint(uaPublic));
        byte[] ecdhSecret = sender.DeriveRawSecretAgreement(ua.PublicKey);

        // RFC 8291 §3.4: the input keying material mixes in the auth secret and both public keys.
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic];
        byte[] ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        // RFC 8188: the content encryption key and nonce come from the salt.
        byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        byte[] cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        byte[] nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        byte[] plain = [.. payload, 0x02];   // a single, last record, unpadded
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (AesGcm aes = new(cek, 16)) aes.Encrypt(nonce, plain, cipher, tag);

        byte[] header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return [.. header, .. cipher, .. tag];
    }

    /// <summary>The VAPID <c>Authorization</c> header value for an endpoint: a 12-hour ES256 JWT plus the public key.</summary>
    public static string VapidAuthorization(string endpoint, ECDsa key, string subject, DateTimeOffset now)
    {
        Uri uri = new(endpoint);
        string header = B64(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
        string claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = uri.GetLeftPart(UriPartial.Authority), ["exp"] = now.AddHours(12).ToUnixTimeSeconds(), ["sub"] = subject,
        }));
        string signed = header + "." + claims;
        string signature = B64(key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        return $"vapid t={signed}.{signature}, k={B64(PublicPoint(key.ExportParameters(false)))}";
    }
}

/// <summary>The application server key pair, created once and kept in the secret store.</summary>
public sealed class VapidKeys
{
    private const string SecretName = "push-vapid-key";
    private readonly ECParameters _parameters;

    public VapidKeys(Core.SecretStore secrets)
    {
        if (secrets.Get(SecretName) is { } stored)
        {
            byte[] d = WebPushCrypto.FromB64(stored);
            using ECDsa derived = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
            _parameters = derived.ExportParameters(true);
        }
        else
        {
            using ECDsa created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _parameters = created.ExportParameters(true);
            secrets.Set(SecretName, WebPushCrypto.B64(_parameters.D!));
        }
    }

    /// <summary>The public key the browser subscribes with (<c>applicationServerKey</c>).</summary>
    public string PublicKey => WebPushCrypto.B64(WebPushCrypto.PublicPoint(_parameters));

    public ECDsa Signer() => ECDsa.Create(_parameters);
}

/// <summary>Sends notifications to every stored subscription and forgets the ones the push service says are gone.</summary>
public sealed class PushSender(Auth.AuthStore store, VapidKeys keys, HttpClient http, string subject, ILogger<PushSender> log)
{
    public async Task<int> SendAsync(object message, bool urgent, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, Harness.Client.HarnessClient.Json);
        int delivered = 0;
        using ECDsa signer = keys.Signer();
        foreach (PushSubscription s in store.PushSubscriptions())
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Post, s.Endpoint)
                {
                    Content = new ByteArrayContent(WebPushCrypto.Encrypt(payload, WebPushCrypto.FromB64(s.P256dh), WebPushCrypto.FromB64(s.Auth))),
                };
                request.Content.Headers.ContentType = new("application/octet-stream");
                request.Content.Headers.ContentEncoding.Add("aes128gcm");
                request.Headers.TryAddWithoutValidation("Authorization", WebPushCrypto.VapidAuthorization(s.Endpoint, signer, subject, DateTimeOffset.UtcNow));
                request.Headers.Add("TTL", urgent ? "3600" : "86400");
                request.Headers.Add("Urgency", urgent ? "high" : "normal");
                using HttpResponseMessage response = await http.SendAsync(request, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) store.RemovePushSubscription(s.Endpoint);
                else if (response.IsSuccessStatusCode) delivered++;
                else log.LogWarning("Push to {Host} failed: {Status}", new Uri(s.Endpoint).Host, (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or ArgumentException or CryptographicException)
            {
                log.LogWarning("Push to {Host} failed: {Error}", new Uri(s.Endpoint).Host, ex.Message);
            }
        }
        return delivered;
    }
}
