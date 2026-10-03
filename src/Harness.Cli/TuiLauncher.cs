using System.CommandLine;
using System.Text.Json.Nodes;
using Harness.Client;
using Harness.Core.Config;
using Harness.Tui;
using Harness.Tui.State;

namespace Harness.Cli;

/// <summary>Opens the full-screen TUI for <c>harness</c>, <c>harness chat</c>, <c>runs watch</c> and <c>runs attach</c> in a terminal.</summary>
internal static class TuiLauncher
{
    /// <summary>True when the command should open the TUI rather than print line-oriented output.</summary>
    public static bool Wanted(ParseResult p) => !p.GetValue(CliContext.Json) && TuiApp.CanRun(p.GetValue(CliContext.Plain));

    public static Task<int> RunAsync(ParseResult p, Func<TuiOptionsBuilder, TuiOptionsBuilder> configure, CancellationToken ct) => HarnessCli.Guard(async () =>
    {
        using HarnessClient client = CliContext.Connect(p);
        (string? theme, Keymap keymap) = LoadConfig(CliContext.Paths(p).TuiFile);
        TuiOptionsBuilder b = configure(new TuiOptionsBuilder());
        TuiOptions options = new()
        {
            Start = b.Start, SessionId = b.SessionId, RunId = b.RunId, Continue = b.Continue,
            NewSession = b.NewSession ?? DefaultSession(p),
            Theme = Harness.Tui.Views.ThemeName.Resolve(theme), Keymap = keymap,
        };
        return await TuiApp.RunAsync(client, options, ct);
    });

    /// <summary>A new session in the current folder locally; remotely the daemon's workspaces are offered instead.</summary>
    public static CreateSessionRequest DefaultSession(ParseResult p, string? agent = null, string? workspaceName = null, string? model = null) =>
        workspaceName is not null || CliContext.IsRemote(p)
            ? new CreateSessionRequest(agent, null, workspaceName, Model: model)
            : new CreateSessionRequest(agent, Directory.GetCurrentDirectory(), null, Model: model);

    /// <summary>
    /// <c>~/.harness/tui.yaml</c>: <c>theme: dark | light | none</c> and <c>keys:</c>, a map from command name to one key or
    /// a list of keys (<c>palette: ["Ctrl+K", "Ctrl+P"]</c>). Unknown settings and commands are errors.
    /// </summary>
    internal static (string? Theme, Keymap Keymap) LoadConfig(string path)
    {
        Keymap keymap = Keymap.Default();
        if (!File.Exists(path)) return (null, keymap);
        if (Yaml.ToJson(File.ReadAllText(path)) is not JsonObject root) return (null, keymap);
        string? theme = null;
        foreach ((string key, JsonNode? value) in root)
        {
            switch (key)
            {
                case "theme":
                    theme = value?.ToString();
                    if (theme is not ("dark" or "light" or "none")) throw new ConfigException($"{path}: theme must be dark, light or none.");
                    break;
                case "keys" when value is JsonObject keys:
                    Dictionary<string, IReadOnlyList<string>> overrides = [];
                    foreach ((string command, JsonNode? binding) in keys)
                        overrides[command] = binding switch
                        {
                            JsonArray list => [.. list.Select(k => k?.ToString() ?? "")],
                            null => [],
                            _ => [binding.ToString()],
                        };
                    try { keymap.Override(overrides); }
                    catch (ArgumentException ex) { throw new ConfigException($"{path}: {ex.Message}"); }
                    break;
                case "keys":
                    break;
                default:
                    throw new ConfigException($"{path}: unknown setting '{key}' (expected theme or keys).");
            }
        }
        return (theme, keymap);
    }
}

internal sealed record TuiOptionsBuilder
{
    public Screen Start { get; init; } = Screen.Session;
    public string? SessionId { get; init; }
    public string? RunId { get; init; }
    public bool Continue { get; init; }
    public CreateSessionRequest? NewSession { get; init; }
}
