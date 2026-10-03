using System.CommandLine;
using Harness.Cli.Commands;
using Harness.Client;

namespace Harness.Cli;

/// <summary>Builds the <c>harness</c> command tree. The entry project adds <c>serve</c>, which needs the server.</summary>
public static class HarnessCli
{
    public static RootCommand Create()
    {
        RootCommand root = new("harness: a self-hosted coding agent harness. Run 'harness serve' to start the daemon; every other command is a client of it.");
        root.Options.Add(CliContext.Home);
        root.Options.Add(CliContext.Remote);
        root.Options.Add(CliContext.Token);
        root.Options.Add(CliContext.Json);
        root.Options.Add(CliContext.Plain);

        Command chat = ChatCommand.Create();
        root.Subcommands.Add(chat);
        root.Subcommands.Add(SessionCommands.Create());
        root.Subcommands.Add(RunCommands.Create());
        foreach (Command c in RunCommands.ApprovalCommands()) root.Subcommands.Add(c);
        root.Subcommands.Add(TriggerCommands.Create());
        foreach (Command c in LocalCommands.Create()) root.Subcommands.Add(c);

        // 'harness' with no arguments opens the full-screen TUI; piped, with --plain or on a dumb terminal, line-mode chat.
        root.SetAction((p, ct) => TuiLauncher.Wanted(p) ? TuiLauncher.RunAsync(p, b => b, ct) : ChatCommand.RunAsync(p, null, ct));
        return root;
    }

    /// <summary>Runs a command body, turning expected failures into one-line errors and exit code 1.</summary>
    internal static async Task<int> Guard(Func<Task<int>> body)
    {
        try { return await body(); }
        catch (CliException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
        catch (HarnessApiException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
        catch (HttpRequestException ex) { Console.Error.WriteLine($"error: cannot reach the daemon: {ex.Message}"); return 1; }
        catch (Harness.Core.Config.ConfigException ex) { Console.Error.WriteLine($"config error: {ex.Message}"); return 1; }
        catch (OperationCanceledException) { return 130; }
    }
}
