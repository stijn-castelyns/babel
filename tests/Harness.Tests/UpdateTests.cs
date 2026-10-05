using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Harness.Cli;
using Harness.Cli.Update;

namespace Harness.Tests;

/// <summary><c>harness update</c> against a fake GitHub releases API serving what release.yml publishes.</summary>
public class UpdateTests : IDisposable
{
    private const string Asset = "harness-linux-x64";
    private static readonly Uri Api = new("https://api.test/");
    private readonly string _dir = Directory.CreateTempSubdirectory("harness-update-").FullName;
    private readonly ECDsa _signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _signing.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Serves one release: the asset list, the files, and records the requests.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = [];
        public string Tag = "v1.0.7";
        public bool Exists = true;
        public readonly List<HttpRequestMessage> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/repos/me/harness/releases/latest")
            {
                if (!Exists) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                JsonArray assets = [.. Files.Keys.Select(n => (JsonNode)new JsonObject { ["name"] = n, ["url"] = $"https://api.test/assets/{n}" })];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new JsonObject { ["tag_name"] = Tag, ["assets"] = assets }.ToJsonString()),
                });
            }
            return Task.FromResult(path.StartsWith("/assets/", StringComparison.Ordinal) && Files.TryGetValue(path["/assets/".Length..], out byte[]? bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private FakeGitHub Publish(byte[] binary, string tag = "v1.0.7", string? sumsTag = null, ECDsa? signer = null, bool sign = true)
    {
        FakeGitHub github = new() { Tag = tag };
        github.Files[Asset] = binary;
        github.Files["harness-osx-arm64"] = [9, 9, 9];
        byte[] sums = Encoding.UTF8.GetBytes(
            $"# release {sumsTag ?? tag}\n{Convert.ToHexStringLower(SHA256.HashData([9, 9, 9]))}  harness-osx-arm64\n{Convert.ToHexStringLower(SHA256.HashData(binary))}  {Asset}\n");
        github.Files[Updater.ChecksumsFile] = sums;
        if (sign) github.Files[Updater.SignatureFile] = (signer ?? _signing).SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return github;
    }

    private Updater For(FakeGitHub github, bool withKey = true, string? token = null) =>
        new(new HttpClient(github), "me/harness", token, withKey ? _signing.ExportSubjectPublicKeyInfo() : null, Api);

    [Fact]
    public async Task A_signed_release_is_found_downloaded_and_verified()
    {
        byte[] binary = RandomNumberGenerator.GetBytes(4096);
        FakeGitHub github = Publish(binary);
        Updater updater = For(github, token: "ghp_test");
        string target = Path.Combine(_dir, "harness.new");

        Release release = (await updater.LatestAsync(Asset, CancellationToken.None))!;
        Assert.Equal(new Version(1, 0, 7), release.Version);
        await updater.DownloadAsync(release, Asset, target, CancellationToken.None);

        Assert.Equal(binary, File.ReadAllBytes(target));
        Assert.True(updater.VerifiesSignatures);
        Assert.All(github.Requests, r => Assert.Equal("ghp_test", r.Headers.Authorization?.Parameter));
        Assert.Contains(github.Requests, r => r.Headers.Accept.ToString() == "application/octet-stream");
    }

    [Fact]
    public async Task Releases_that_fail_verification_are_not_installed()
    {
        byte[] binary = RandomNumberGenerator.GetBytes(1024);
        string target = Path.Combine(_dir, "harness.new");

        async Task<string> Refused(FakeGitHub github, bool withKey = true)
        {
            Updater updater = For(github, withKey);
            Release release = (await updater.LatestAsync(Asset, CancellationToken.None))!;
            string message = (await Assert.ThrowsAsync<CliException>(() => updater.DownloadAsync(release, Asset, target, CancellationToken.None))).Message;
            Assert.False(File.Exists(target));
            return message;
        }

        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Contains("does not match this build's signing key", await Refused(Publish(binary, signer: other)));
        Assert.Contains("not signed", await Refused(Publish(binary, sign: false)));
        // An older signed release passed off under a newer tag.
        Assert.Contains("belongs to another release", await Refused(Publish(binary, tag: "v1.0.9", sumsTag: "v1.0.3")));
        // The binary changed after the checksums were signed.
        FakeGitHub tampered = Publish(binary);
        tampered.Files[Asset] = [.. binary, 0];
        Assert.Contains("does not match its checksum", await Refused(tampered));
        FakeGitHub unlisted = Publish(binary);
        unlisted.Files[Updater.ChecksumsFile] = Encoding.UTF8.GetBytes("# release v1.0.7\n");
        Assert.Contains("does not list", await Refused(unlisted, withKey: false));
    }

    [Fact]
    public async Task Without_a_signing_key_checksums_are_still_checked()
    {
        byte[] binary = RandomNumberGenerator.GetBytes(512);
        Updater updater = For(Publish(binary, sign: false), withKey: false);
        string target = Path.Combine(_dir, "harness.new");
        await updater.DownloadAsync((await updater.LatestAsync(Asset, CancellationToken.None))!, Asset, target, CancellationToken.None);
        Assert.Equal(binary, File.ReadAllBytes(target));
        Assert.False(updater.VerifiesSignatures);
    }

    [Fact]
    public async Task No_release_and_missing_assets_are_reported()
    {
        FakeGitHub none = Publish([1]);
        none.Exists = false;
        Assert.Null(await For(none).LatestAsync(Asset, CancellationToken.None));
        Assert.Contains("has no harness-win-x64.exe",
            (await Assert.ThrowsAsync<CliException>(() => For(Publish([1])).LatestAsync("harness-win-x64.exe", CancellationToken.None))).Message);
    }

    [Fact]
    public async Task Releases_signed_by_openssl_in_the_workflow_verify()
    {
        // Produced by release.yml's commands: sha256sum under a release line, then openssl dgst -sha256 -sign (P-256 key).
        const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEf36rxg6LiobBovXnTLwk5WlM6oQ9ab/iIwzcnuLzRwMFX9Q3FEXsiljyXcRcjJ9ve0gI0AS/M94aGu006lchjw==";
        const string Signature = "MEUCIQDgJj97KUV2ibjQ7wrG4MNyVPZv92p+q+27tH1SfA8NswIgBCEZI5zk5+i8guCemBRo2jAmwu9TLYU+LZFHNC0+AHw=";
        FakeGitHub github = new() { Tag = "v1.0.42" };
        github.Files[Asset] = Encoding.UTF8.GetBytes("harness-binary-bytes");
        github.Files[Updater.ChecksumsFile] = Encoding.UTF8.GetBytes(
            "# release v1.0.42\nc808837b1720717e3e2ba54f0c9c5089b1494261a5e884da603f6f2caa2c6b4d  harness-linux-x64\n");
        github.Files[Updater.SignatureFile] = Convert.FromBase64String(Signature);
        Updater updater = new(new HttpClient(github), "me/harness", null, Convert.FromBase64String(PublicKey), Api);
        string target = Path.Combine(_dir, "harness.new");
        await updater.DownloadAsync((await updater.LatestAsync(Asset, CancellationToken.None))!, Asset, target, CancellationToken.None);
        Assert.Equal("harness-binary-bytes", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("v1.0.12", "1.0.12")]
    [InlineData("1.0.12+3f2a9c", "1.0.12")]
    [InlineData("1.0.12.0", "1.0.12")]
    [InlineData("1.0", "1.0.0")]
    [InlineData("nightly", null)]
    public void Versions_from_tags_builds_and_the_daemon_compare(string text, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), Updater.Parse(text));
}
