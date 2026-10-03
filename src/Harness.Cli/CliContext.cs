using System.CommandLine;
using Harness.Client;
using Harness.Core;
using Harness.Core.Config;

namespace Harness.Cli;

/// <summary>Global options and how the CLI reaches the daemon.</summary>
public static class CliContext
{
    public static readonly Option<string?> Home = new("--home") { Description = "Harness home directory (default ~/.harness or HARNESS_HOME)", Recursive = true };
    public static readonly Option<string?> Remote = new("--remote") { Description = "Daemon URL for remote use (or HARNESS_URL)", Recursive = true };
    public static readonly Option<string?> Token = new("--token") { Description = "Bearer token for --remote (or HARNESS_TOKEN)", Recursive = true };
    public static readonly Option<bool> Json = new("--json") { Description = "Print JSON instead of tables", Recursive = true };
    public static readonly Option<bool> Plain = new("--plain") { Description = "Line-oriented output without colours or live views", Recursive = true };

    public static HarnessPaths Paths(ParseResult p) =>
        p.GetValue(Home) is { Length: > 0 } home ? new HarnessPaths(home) : HarnessPaths.Default();

    public static bool IsRemote(ParseResult p) => RemoteUrl(p) is not null;

    private static string? RemoteUrl(ParseResult p) => p.GetValue(Remote) ?? Environment.GetEnvironmentVariable("HARNESS_URL");

    public static HarnessClient Connect(ParseResult p)
    {
        if (RemoteUrl(p) is { } url)
            return HarnessClient.ForUrl(new Uri(url.EndsWith('/') ? url : url + "/"), p.GetValue(Token) ?? Environment.GetEnvironmentVariable("HARNESS_TOKEN"));
        HarnessPaths paths = Paths(p);
        string socket = paths.Socket;
        try
        {
            if (new ConfigCatalog(paths).Config.Listeners.Socket is { Length: > 0 } configured) socket = HarnessPaths.ExpandHome(configured);
        }
        catch (ConfigException) { }   // a broken config.yaml should not stop the client from reporting the daemon's error
        if (!File.Exists(socket))
            throw new CliException($"The harness daemon is not running (no socket at {socket}). Start it with 'harness serve'.");
        return HarnessClient.ForSocket(socket);
    }
}

public sealed class CliException(string message) : Exception(message);
