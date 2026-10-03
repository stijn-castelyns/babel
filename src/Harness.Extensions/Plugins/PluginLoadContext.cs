using System.Reflection;
using System.Runtime.Loader;

namespace Harness.Extensions.Plugins;

/// <summary>
/// A collectible load context per plugin. The SDK and the framework abstractions always come from the host so types
/// match across the boundary; everything else resolves from the plugin folder.
/// </summary>
public sealed class PluginLoadContext(string entryAssemblyPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(entryAssemblyPath), isCollectible: true)
{
    private static readonly string[] SharedPrefixes =
    [
        "Harness.Sdk",
        "Microsoft.Extensions.AI",
        "Microsoft.Agents.AI",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Primitives",
        "Microsoft.AspNetCore",
        "System.",
    ];

    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);

    public static bool IsShared(string name) =>
        name == "System" || name == "netstandard" || SharedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && IsShared(name)) return null;   // fall back to the host's copy
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
