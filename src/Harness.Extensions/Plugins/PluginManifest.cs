using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Extensions.Plugins;

/// <summary><c>plugin.json</c> in a plugin folder.</summary>
public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "0.0.0";
    /// <summary>File name of the assembly that contains the <c>IHarnessPlugin</c> implementation.</summary>
    public string EntryAssembly { get; set; } = "";
    /// <summary>Supported SDK major version range, for example <c>1</c> or <c>1-2</c>.</summary>
    public string Sdk { get; set; } = "1";
    public List<string> Permissions { get; set; } = [];
    /// <summary>SHA-256 of the entry assembly recorded at install time; loading fails if the file changed.</summary>
    public string? Sha256 { get; set; }

    [JsonIgnore]
    public string Directory { get; set; } = "";

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static PluginManifest Load(string dir)
    {
        string file = Path.Combine(dir, "plugin.json");
        PluginManifest manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), Json)
            ?? throw new InvalidDataException($"{file} is empty.");
        if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidDataException($"{file}: 'id' is required.");
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly)) throw new InvalidDataException($"{file}: 'entryAssembly' is required.");
        manifest.Directory = dir;
        return manifest;
    }

    public bool SupportsSdk(int major)
    {
        string[] parts = Sdk.Split('-', StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            1 => int.TryParse(parts[0], out int only) && only == major,
            2 => int.TryParse(parts[0], out int lo) && int.TryParse(parts[1], out int hi) && major >= lo && major <= hi,
            _ => false,
        };
    }
}
