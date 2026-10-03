using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Extensions.Mcp;

/// <summary>The common <c>mcpServers</c> shape, so existing configs can be copied in.</summary>
public sealed class McpConfigFile
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = [];

    public static McpConfigFile Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<McpConfigFile>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new()
            : new();
}

public sealed class McpServerConfig
{
    /// <summary>stdio: the executable.</summary>
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    /// <summary>Environment for stdio servers; values may be <c>secret:</c> or <c>env:</c> references.</summary>
    public Dictionary<string, string> Env { get; set; } = [];
    /// <summary>Streamable HTTP: the endpoint.</summary>
    public string? Url { get; set; }
    /// <summary>HTTP headers; values may be <c>secret:</c> or <c>env:</c> references.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];
    /// <summary>Only these tools are exposed (empty means all).</summary>
    public List<string> Allow { get; set; } = [];
    public List<string> Deny { get; set; } = [];

    [JsonIgnore]
    public bool IsRemote => !string.IsNullOrEmpty(Url);
}
