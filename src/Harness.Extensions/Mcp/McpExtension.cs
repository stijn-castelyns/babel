using System.Collections.Concurrent;
using Harness.Core;
using Harness.Core.Agents;
using Harness.Core.Config;
using Harness.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Harness.Extensions.Mcp;

/// <summary>
/// Exposes MCP tools as <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>. Servers start lazily: remote HTTP servers are shared across runs,
/// stdio servers are spawned per run inside that run's sandbox, because they execute arbitrary commands.
/// </summary>
public sealed class McpExtension(HarnessPaths paths, SecretStore secrets, TrustStore trust, ILoggerFactory? loggerFactory = null) : IRunExtension, IAsyncDisposable
{
    private readonly ILoggerFactory _loggers = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _remote = new();
    private readonly ConcurrentDictionary<string, List<IAsyncDisposable>> _perRun = new();

    /// <summary>Global servers merged with the workspace's servers when that folder is trusted.</summary>
    public IReadOnlyDictionary<string, McpServerConfig> Servers(string? workspaceRoot)
    {
        Dictionary<string, McpServerConfig> all = new(McpConfigFile.Load(paths.McpFile).McpServers);
        if (workspaceRoot is not null && trust.IsTrusted(workspaceRoot))
            foreach ((string name, McpServerConfig cfg) in McpConfigFile.Load(Path.Combine(workspaceRoot, ".harness", "mcp.json")).McpServers)
                all[name] = cfg;
        return all;
    }

    public async ValueTask<IEnumerable<AITool>> GetToolsAsync(RunContext run, CancellationToken cancellationToken)
    {
        if (run.Agent.Tools.Mcp.Count == 0) return [];
        IReadOnlyDictionary<string, McpServerConfig> servers = Servers(run.WorkspaceRoot);
        List<AITool> tools = [];
        foreach (string name in run.Agent.Tools.Mcp.Distinct())
        {
            if (!servers.TryGetValue(name, out McpServerConfig? cfg))
                throw new ConfigException($"MCP server '{name}' is not configured in {paths.McpFile} (or the workspace's .harness/mcp.json is not trusted).");
            McpClient client = cfg.IsRemote ? await RemoteAsync(name, cfg, cancellationToken) : await StartStdioAsync(name, cfg, run, cancellationToken);
            IList<McpClientTool> listed = await client.ListToolsAsync(cancellationToken: cancellationToken);
            foreach (McpClientTool tool in listed)
            {
                if (cfg.Allow.Count > 0 && !cfg.Allow.Contains(tool.Name)) continue;
                if (cfg.Deny.Contains(tool.Name)) continue;
                tools.Add(tool.WithName($"mcp__{name}__{tool.Name}"));
            }
        }
        return tools;
    }

    public async ValueTask ReleaseAsync(RunContext run)
    {
        if (!_perRun.TryRemove(run.RunId, out List<IAsyncDisposable>? owned)) return;
        foreach (IAsyncDisposable d in Enumerable.Reverse(owned))
        {
            try { await d.DisposeAsync(); }
            catch (Exception) { }   // a server that fails to shut down cleanly must not fail the run
        }
    }

    private Task<McpClient> RemoteAsync(string name, McpServerConfig cfg, CancellationToken ct) =>
        _remote.GetOrAdd(name, _ => new Lazy<Task<McpClient>>(async () =>
        {
            HttpClientTransport transport = new(new HttpClientTransportOptions
            {
                Name = name,
                Endpoint = new Uri(cfg.Url!),
                AdditionalHeaders = cfg.Headers.ToDictionary(kv => kv.Key, kv => secrets.Resolve(kv.Value) ?? ""),
            }, _loggers);
            return await McpClient.CreateAsync(transport, ClientOptions(), _loggers, CancellationToken.None);
        })).Value.WaitAsync(ct);

    private async Task<McpClient> StartStdioAsync(string name, McpServerConfig cfg, RunContext run, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cfg.Command)) throw new ConfigException($"MCP server '{name}' needs a command or a url.");
        ISandboxProcess process = await run.Sandbox.StartAsync(new ExecRequest
        {
            Command = cfg.Command,
            Arguments = cfg.Args,
            WorkingDirectory = run.WorkspaceRoot,
            Environment = cfg.Env.ToDictionary(kv => kv.Key, kv => secrets.Resolve(kv.Value) ?? ""),
        }, ct);
        _ = DrainAsync(process.StandardError);
        StreamClientTransport transport = new(process.StandardInput, process.StandardOutput, _loggers);
        McpClient client = await McpClient.CreateAsync(transport, ClientOptions(), _loggers, ct);
        List<IAsyncDisposable> owned = _perRun.GetOrAdd(run.RunId, _ => []);
        lock (owned) { owned.Add(process); owned.Add(client); }
        return client;
    }

    private static McpClientOptions ClientOptions() => new() { ClientInfo = new Implementation { Name = "harness", Version = "0.1.0" } };

    private static async Task DrainAsync(Stream stream)
    {
        try { await stream.CopyToAsync(Stream.Null); }
        catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Lazy<Task<McpClient>> lazy in _remote.Values)
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) await lazy.Value.Result.DisposeAsync();
        _remote.Clear();
    }
}
