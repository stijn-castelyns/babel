using Harness.Core;
using Harness.Core.Agents;
using Harness.Runs;
using Harness.Triggers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.TestSupport;

/// <summary>A throwaway harness home and workspace with a fake model profile and a <c>coder</c> agent.</summary>
public sealed class TestHome : IAsyncDisposable
{
    public TestHome(string? agentYaml = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "harness-tests", Guid.NewGuid().ToString("N")[..10]);
        Paths = new HarnessPaths(Path.Combine(Root, "home"));
        Paths.EnsureCreated();
        Workspace = Path.Combine(Root, "ws");
        Directory.CreateDirectory(Workspace);
        File.WriteAllText(Paths.ConfigFile, """
            models:
              fake:
                provider: openai
                endpoint: http://127.0.0.1:9/v1
                model: fake-model
            defaults:
              agent: coder
            """);
        File.WriteAllText(Path.Combine(Paths.AgentsDir, "coder.yaml"), agentYaml ?? """
            name: coder
            model: fake
            approvals: { shell: ask, write: allow, edit: allow }
            limits: { maxToolIterations: 20, maxRunMinutes: 5 }
            """);
    }

    public string Root { get; }
    public HarnessPaths Paths { get; }
    public string Workspace { get; }
    public ServiceProvider? Services { get; private set; }

    /// <summary>Builds the runtime. Calling it again after disposing <see cref="Services"/> simulates a daemon restart on the same home.</summary>
    public ServiceProvider Build(IChatClient model)
    {
        ServiceCollection services = new();
        services.AddHarnessRuntime(Paths);
        services.AddHarnessTriggers();
        Services = services.BuildServiceProvider();
        Services.GetRequiredService<AgentFactory>().ChatClientOverride = _ => model;
        return Services;
    }

    public RunOrchestrator Orchestrator => Services!.GetRequiredService<RunOrchestrator>();

    public TriggerEngine Triggers => Services!.GetRequiredService<TriggerEngine>();

    /// <summary>Writes <c>templates/&lt;name&gt;/</c> from relative path → content pairs.</summary>
    public string WriteTemplate(string name, params (string Path, string Content)[] files)
    {
        string dir = System.IO.Path.Combine(Paths.TemplatesDir, name);
        foreach ((string path, string content) in files)
        {
            string file = System.IO.Path.Combine(dir, path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }
        return dir;
    }

    public async ValueTask DisposeAsync()
    {
        if (Services is not null) await Services.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
