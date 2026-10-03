using Harness.Core;
using Harness.Core.Config;
using Harness.Sdk;

namespace Harness.Sandbox;

/// <summary>Turns a named profile from <c>sandboxes.yaml</c> into a running sandbox, using built-in or plugin providers.</summary>
public sealed class SandboxFactory(ConfigCatalog catalog, IEnumerable<ISandboxProvider> providers)
{
    private readonly List<ISandboxProvider> _providers = [.. providers];

    public void Register(ISandboxProvider provider) => _providers.Insert(0, provider);

    public IEnumerable<string> Types => _providers.Select(p => p.Type).Distinct();

    public SandboxSpec Resolve(string profileName, string workspace)
    {
        SandboxProfile p = catalog.Sandbox(profileName);
        string Expand(string s) => HarnessPaths.ExpandHome(s.Replace("{workspace}", workspace, StringComparison.Ordinal));

        // The workspace mount is implicit; a profile mount of {workspace} only chooses where it appears.
        MountConfig? workspaceMount = p.Mounts.FirstOrDefault(m => m.Host == "{workspace}");
        return new SandboxSpec
        {
            Name = profileName,
            Type = p.Type,
            WorkspaceHostPath = workspace,
            WorkspaceSandboxPath = workspaceMount?.Path ?? "/workspace",
            Network = p.Network switch
            {
                "none" => NetworkMode.None,
                "allowlist" => NetworkMode.Allowlist,
                "full" => NetworkMode.Full,
                var other => throw new ConfigException($"Sandbox '{profileName}': network must be none, allowlist or full, not '{other}'."),
            },
            AllowHosts = p.AllowHosts,
            Mounts = [.. p.Mounts.Where(m => m != workspaceMount).Select(m => new MountSpec(
                Path.GetFullPath(Expand(m.Host)), m.Path, m.Mode is "rw" ? MountMode.ReadWrite : MountMode.ReadOnly))],
            Environment = p.Env,
            Limits = new SandboxLimits(p.Limits.Cpus, p.Limits.MemoryMb, p.Limits.Pids, p.Limits.WallClockMinutes),
            Image = p.Image,
            Options = p.Options,
        };
    }

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct)
    {
        ISandboxProvider provider = _providers.FirstOrDefault(p => p.Type == spec.Type)
            ?? throw new ConfigException($"No sandbox provider for type '{spec.Type}'. Available: {string.Join(", ", Types)}.");
        return provider.CreateAsync(spec, ct);
    }

    public static IEnumerable<ISandboxProvider> BuiltIn() =>
        [new NoneSandboxProvider(), new BubblewrapSandboxProvider(), new ContainerSandboxProvider()];
}
