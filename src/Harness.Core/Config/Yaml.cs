using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Harness.Core.Config;

public static class Yaml
{
    private static readonly IDeserializer Strict = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer Untyped = new DeserializerBuilder()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    private static readonly ISerializer Writer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>Deserializes YAML, rejecting unknown keys so typos surface as errors.</summary>
    public static T Parse<T>(string yaml) where T : new() =>
        string.IsNullOrWhiteSpace(yaml) ? new T() : Strict.Deserialize<T>(yaml) ?? new T();

    public static T Load<T>(string path) where T : new()
    {
        try { return Parse<T>(File.ReadAllText(path)); }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ConfigException($"{path}:{ex.Start.Line}:{ex.Start.Column}: {ex.InnerException?.Message ?? ex.Message}", ex);
        }
    }

    public static string Serialize<T>(T value) => Writer.Serialize(value);

    /// <summary>Parses YAML into a JSON tree, for free-form sections such as trigger sources.</summary>
    public static JsonNode? ToJson(string yaml) => ToJsonNode(Untyped.Deserialize<object?>(yaml));

    public static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        IDictionary<object, object?> map => new JsonObject(map.Select(kv => KeyValuePair.Create(kv.Key.ToString()!, ToJsonNode(kv.Value)))),
        IDictionary<string, object?> map => new JsonObject(map.Select(kv => KeyValuePair.Create(kv.Key, ToJsonNode(kv.Value)))),
        IEnumerable<object?> list when value is not string => new JsonArray([.. list.Select(ToJsonNode)]),
        _ => JsonValue.Create(JsonSerializer.SerializeToElement(value)),
    };
}

public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);
