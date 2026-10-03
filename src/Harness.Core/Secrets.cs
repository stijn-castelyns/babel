using System.Text.Json;

namespace Harness.Core;

/// <summary>
/// Resolves <c>secret:&lt;name&gt;</c> and <c>env:&lt;NAME&gt;</c> references. Secrets live in <c>secrets.json</c>
/// with mode 0600 for now; the OS credential stores (DPAPI, Keychain, libsecret) slot in behind this class later.
/// </summary>
public sealed class SecretStore(HarnessPaths paths)
{
    private readonly Lock _gate = new();

    public string? Resolve(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return reference;
        if (reference.StartsWith("env:", StringComparison.Ordinal))
            return Environment.GetEnvironmentVariable(reference[4..]);
        if (reference.StartsWith("secret:", StringComparison.Ordinal))
            return Get(reference[7..]);
        return reference;
    }

    public string Require(string reference, string what) =>
        Resolve(reference) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{what}: '{reference}' did not resolve to a value.");

    public static bool IsReference(string? value) =>
        value is not null && (value.StartsWith("env:", StringComparison.Ordinal) || value.StartsWith("secret:", StringComparison.Ordinal));

    public string? Get(string name) => ReadAll().GetValueOrDefault(name);

    public IReadOnlyCollection<string> Names() => ReadAll().Keys;

    /// <summary>All stored secret values, used to mask them in tool results and events.</summary>
    public IReadOnlyCollection<string> Values() => ReadAll().Values;

    public void Set(string name, string value)
    {
        lock (_gate)
        {
            Dictionary<string, string> all = ReadAll();
            all[name] = value;
            Write(all);
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            Dictionary<string, string> all = ReadAll();
            bool removed = all.Remove(name);
            if (removed) Write(all);
            return removed;
        }
    }

    private Dictionary<string, string> ReadAll()
    {
        if (!File.Exists(paths.SecretsFile)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(paths.SecretsFile)) ?? [];
    }

    private void Write(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(paths.Home);
        string tmp = paths.SecretsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, paths.SecretsFile, overwrite: true);
    }
}
