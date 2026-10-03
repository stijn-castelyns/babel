using System.Text.Json;
using Harness.Core;

namespace Harness.Cli;

/// <summary>
/// Tokens from <c>harness login</c>, keyed by daemon URL, in a 0600 <c>credentials.json</c> in the harness home. The OS
/// keychain slots in behind this class, as it does for <see cref="SecretStore"/>.
/// </summary>
internal sealed class Credentials(HarnessPaths paths)
{
    public sealed record Entry(string Token, string? Name, DateTimeOffset SavedAt);

    public static string Key(Uri url) => url.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + "/";

    private Dictionary<string, Entry> Load() =>
        File.Exists(paths.CredentialsFile)
            ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(paths.CredentialsFile)) ?? []
            : [];

    private void Save(Dictionary<string, Entry> all)
    {
        Directory.CreateDirectory(paths.Home);
        string tmp = paths.CredentialsFile + ".tmp";
        File.WriteAllText(tmp, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, paths.CredentialsFile, overwrite: true);
    }

    public string? TokenFor(Uri url) => Load().GetValueOrDefault(Key(url))?.Token;

    public void Set(Uri url, string token, string? name)
    {
        Dictionary<string, Entry> all = Load();
        all[Key(url)] = new Entry(token, name, DateTimeOffset.UtcNow);
        Save(all);
    }

    public bool Remove(Uri url)
    {
        Dictionary<string, Entry> all = Load();
        if (!all.Remove(Key(url))) return false;
        Save(all);
        return true;
    }
}
