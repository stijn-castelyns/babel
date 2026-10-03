using System.CommandLine;
using Harness.Cli;
using Harness.Server;
using Microsoft.Extensions.Hosting;

// One binary carries every role: 'harness serve' is the daemon, every other command is a client of it.
RootCommand root = HarnessCli.Create();

Command serve = new("serve", "Run the harness daemon in the foreground.");
serve.SetAction(async (parse, ct) =>
{
    try
    {
        await using var app = HarnessServer.Build(CliContext.Paths(parse));
        await app.StartAsync(ct);
        await app.WaitForShutdownAsync(ct);
        return 0;
    }
    catch (Harness.Core.Config.ConfigException ex)
    {
        Console.Error.WriteLine($"config error: {ex.Message}");
        return 1;
    }
});
root.Subcommands.Add(serve);

return await root.Parse(args).InvokeAsync();
