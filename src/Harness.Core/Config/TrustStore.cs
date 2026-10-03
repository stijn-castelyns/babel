using System.Security.Cryptography;
using System.Text.Json;

namespace Harness.Core.Config;

/// <summary>
/// Records folders whose <c>.harness/</c> configuration the user trusted, with a hash of that configuration.
/// Folder config that adds executable surface or loosens approvals is ignored until trusted; a changed hash needs trusting again.
/// </summary>
public sealed class TrustStore(HarnessPaths paths)
{
    private static readonly string[] TrustedFiles = ["config.yaml", "mcp.json"];

    public bool IsTrusted(string folder)
    {
        folder = Path.GetFullPath(folder);
        return Read().TryGetValue(folder, out string? hash) && hash == HashConfig(folder);
    }

    public string Trust(string folder)
    {
        folder = Path.GetFullPath(folder);
        Dictionary<string, string> all = Read();
        string hash = HashConfig(folder);
        all[folder] = hash;
        Directory.CreateDirectory(paths.Home);
        File.WriteAllText(paths.TrustFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        return hash;
    }

    public bool Revoke(string folder)
    {
        Dictionary<string, string> all = Read();
        if (!all.Remove(Path.GetFullPath(folder))) return false;
        File.WriteAllText(paths.TrustFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    /// <summary>Hash of the folder's executable-surface configuration, plus every skill file under <c>.harness/skills</c>.</summary>
    public static string HashConfig(string folder)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string dir = Path.Combine(folder, ".harness");
        IEnumerable<string> files = TrustedFiles.Select(f => Path.Combine(dir, f)).Where(File.Exists);
        string skills = Path.Combine(dir, "skills");
        if (Directory.Exists(skills))
            files = files.Concat(Directory.EnumerateFiles(skills, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (string file in files)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file) + "\0"));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private Dictionary<string, string> Read() =>
        File.Exists(paths.TrustFile)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(paths.TrustFile)) ?? []
            : [];
}
