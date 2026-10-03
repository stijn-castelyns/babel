using Harness.Core.Config;
using OllamaSharp;
using OllamaSharp.Models;

namespace Harness.Core.Models;

/// <summary>Checks model profiles for problems that make a coding agent misbehave, such as a small Ollama context window.</summary>
public sealed class ModelDoctor(ConfigCatalog catalog, ChatClientFactory factory)
{
    public const int RecommendedContext = 32_768;

    public sealed record Finding(string Profile, string Level, string Message);

    public async Task<IReadOnlyList<Finding>> CheckAsync(string? only, CancellationToken ct)
    {
        List<Finding> findings = [];
        foreach ((string name, ModelProfile profile) in catalog.Config.Models)
        {
            if (only is not null && name != only) continue;
            try
            {
                using var _ = factory.Create(profile);
                findings.Add(new(name, "ok", $"{profile.Provider} client builds (Chat Completions)."));
            }
            catch (Exception ex)
            {
                findings.Add(new(name, "error", ex.Message));
                continue;
            }

            if (profile.Provider == "ollama")
                findings.AddRange(await CheckOllamaAsync(name, profile, ct));
        }
        if (findings.Count == 0) findings.Add(new(only ?? "*", "warn", "No model profiles found in config.yaml."));
        return findings;
    }

    private static async Task<IEnumerable<Finding>> CheckOllamaAsync(string name, ModelProfile profile, CancellationToken ct)
    {
        List<Finding> findings = [];
        try
        {
            using OllamaApiClient ollama = new(new Uri(profile.Endpoint));
            ShowModelResponse show = await ollama.ShowModelAsync(new ShowModelRequest { Model = profile.Model! }, ct);
            int? numCtx = ParseNumCtx(show.Parameters);
            int? trained = show.Info?.ExtraInfo?.Where(kv => kv.Key.EndsWith(".context_length", StringComparison.Ordinal))
                .Select(kv => int.TryParse(kv.Value?.ToString(), out int v) ? v : (int?)null).FirstOrDefault(v => v is not null);
            int effective = profile.ContextWindow ?? numCtx ?? 4096;
            string detail = $"num_ctx={numCtx?.ToString() ?? "default"}, model max={trained?.ToString() ?? "?"}";
            findings.Add(effective < RecommendedContext
                ? new(name, "warn", $"Context window {effective} is small for a coding agent ({detail}). Create a model from a Modelfile with 'PARAMETER num_ctx {RecommendedContext}' or more.")
                : new(name, "ok", $"Context window {effective} ({detail})."));
        }
        catch (Exception ex)
        {
            findings.Add(new(name, "error", $"Could not query Ollama at {profile.Endpoint}: {ex.Message}"));
        }
        return findings;
    }

    internal static int? ParseNumCtx(string? parameters)
    {
        foreach (string line in (parameters ?? "").Split('\n'))
        {
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "num_ctx" && int.TryParse(parts[1], out int value)) return value;
        }
        return null;
    }
}
