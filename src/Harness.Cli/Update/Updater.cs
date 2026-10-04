using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Harness.Cli.Update;

/// <summary>A published release: its version and the API URLs of the files <c>harness update</c> needs.</summary>
internal sealed record Release(Version Version, string Tag, Uri Binary, Uri Checksums, Uri? Signature);

/// <summary>
/// Finds, downloads and verifies the releases <c>.github/workflows/release.yml</c> publishes: one single-file executable per
/// platform (<c>harness-linux-x64</c>, …), <c>SHA256SUMS</c> listing them under a <c># release v1.2.3</c> line, and
/// <c>SHA256SUMS.sig</c>, an ECDSA P-256 signature over that file. A build that knows a signing key installs only releases
/// signed with it, and the signed release line stops an older signed release from being passed off under a newer tag.
/// Without a key only the checksums are checked, which catches a broken download but not a tampered release.
/// </summary>
internal sealed class Updater(HttpClient http, string repository, string? token, byte[]? publicKey, Uri? apiBase = null)
{
    public const string ChecksumsFile = "SHA256SUMS", SignatureFile = "SHA256SUMS.sig";
    private readonly Uri _api = apiBase ?? new Uri("https://api.github.com/");

    public bool VerifiesSignatures => publicKey is not null;

    /// <summary>The release asset for this machine, for example <c>harness-linux-x64</c>.</summary>
    public static string AssetName()
    {
        string os = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win"
            : throw new CliException("Releases are published for Linux, macOS and Windows only.");
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => throw new CliException($"Releases are published for x64 and arm64, not {other}."),
        };
        return $"harness-{os}-{arch}{(os == "win" ? ".exe" : "")}";
    }

    /// <summary>A value the release build recorded (<c>HarnessUpdateRepository</c>, <c>HarnessUpdatePublicKey</c>).</summary>
    public static string? Metadata(string key) =>
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;

    /// <summary>This build's version (<c>1.0.0</c> for local builds, so any release is newer).</summary>
    public static Version CurrentVersion() =>
        Parse(typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0);

    /// <summary>Reads <c>v1.2.3</c>, <c>1.2.3+commit</c> or <c>1.2.3.0</c> as a three-part version.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(core, out Version? v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    /// <summary>The latest published release, or null when the repository has none (a private one also answers 404 without a token).</summary>
    public async Task<Release?> LatestAsync(string asset, CancellationToken ct)
    {
        using HttpRequestMessage request = Request(new Uri(_api, $"repos/{repository}/releases/latest"), "application/vnd.github+json");
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccess(response, ct);
        JsonNode json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? throw new CliException("GitHub sent an empty release.");
        string tag = json["tag_name"]?.GetValue<string>() ?? throw new CliException("GitHub sent a release without a tag.");
        Version version = Parse(tag) ?? throw new CliException($"Release tag '{tag}' is not a version.");
        Dictionary<string, Uri> assets = [];
        foreach (JsonNode? a in json["assets"]?.AsArray() ?? [])
            if (a?["name"]?.GetValue<string>() is { } name && a["url"]?.GetValue<string>() is { } url) assets[name] = new Uri(url);
        return new Release(version, tag,
            assets.GetValueOrDefault(asset) ?? throw new CliException($"Release {tag} has no {asset}."),
            assets.GetValueOrDefault(ChecksumsFile) ?? throw new CliException($"Release {tag} has no {ChecksumsFile}."),
            assets.GetValueOrDefault(SignatureFile));
    }

    /// <summary>Downloads the release's executable to <paramref name="path"/> once its checksum (and signature) check out; a file that fails is deleted.</summary>
    public async Task DownloadAsync(Release release, string asset, string path, CancellationToken ct)
    {
        byte[] sums = await GetBytes(release.Checksums, ct);
        if (publicKey is not null)
        {
            if (release.Signature is null) throw new CliException($"Release {release.Tag} is not signed, and this build only installs signed releases.");
            byte[] signature = await GetBytes(release.Signature, ct);
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            if (!key.VerifyData(sums, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new CliException($"The signature on release {release.Tag} does not match this build's signing key; not installing it.");
        }
        string[] lines = Encoding.UTF8.GetString(sums).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!lines.Contains($"# release {release.Tag}"))
            throw new CliException($"{ChecksumsFile} of release {release.Tag} belongs to another release; not installing it.");
        string expected = lines.Select(l => l.Split(' ', 2, StringSplitOptions.TrimEntries))
            .Where(f => f.Length == 2 && f[1].TrimStart('*') == asset).Select(f => f[0].ToLowerInvariant()).FirstOrDefault()
            ?? throw new CliException($"{ChecksumsFile} of release {release.Tag} does not list {asset}.");

        using (HttpRequestMessage request = Request(release.Binary, "application/octet-stream"))
        using (HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            await EnsureSuccess(response, ct);
            await using FileStream file = new(path, FileMode.Create, FileAccess.Write);
            await response.Content.CopyToAsync(file, ct);
        }
        string actual;
        await using (FileStream file = File.OpenRead(path)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (actual != expected)
        {
            File.Delete(path);
            throw new CliException($"The download of {asset} from release {release.Tag} does not match its checksum; not installing it.");
        }
    }

    private async Task<byte[]> GetBytes(Uri uri, CancellationToken ct)
    {
        using HttpRequestMessage request = Request(uri, "application/octet-stream");
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // Asset API URLs answer with a redirect to storage when asked for octet-stream; HttpClient drops the token on redirects.
    private HttpRequestMessage Request(Uri uri, string accept)
    {
        HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("harness", CurrentVersion().ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync(ct);
        string hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? token is null ? " (a private repository needs update.token)" : " (check update.token)" : "";
        throw new CliException($"GitHub answered {(int)response.StatusCode} for {response.RequestMessage?.RequestUri}{hint}: {body[..Math.Min(body.Length, 200)]}");
    }
}
