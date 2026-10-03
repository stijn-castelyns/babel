using System.Security.Cryptography;

namespace Harness.Tools;

/// <summary>The one path policy for every file tool: canonicalise symlinks and reject anything outside the workspace root.</summary>
public interface IWorkspace
{
    string Root { get; }
    /// <summary>Resolves a workspace-relative (or absolute) path to a canonical host path inside the root, or throws.</summary>
    string Resolve(string path);
    /// <summary>The path as the agent should see it: relative to the root, with forward slashes.</summary>
    string Display(string fullPath);
}

public sealed class Workspace : IWorkspace
{
    public Workspace(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Workspace '{root}' does not exist.");
        Root = RealPath(Path.GetFullPath(root));
    }

    public string Root { get; }

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = ".";
        path = path.Trim();
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        string real = RealPath(full);
        if (!IsInside(real))
            throw new WorkspaceAccessException($"'{path}' is outside the workspace. Use paths relative to the workspace root.");
        return real;
    }

    public string Display(string fullPath)
    {
        string rel = Path.GetRelativePath(Root, fullPath).Replace('\\', '/');
        return rel == "" ? "." : rel;
    }

    public bool IsInside(string realPath) =>
        realPath == Root || realPath.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Resolves symlinks component by component; the part of the path that does not exist yet is appended as is.</summary>
    public static string RealPath(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath) ?? "/";
        string[] parts = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string current = root;
        for (int i = 0; i < parts.Length; i++)
        {
            string next = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists && info.LinkTarget is null)
                return Path.Combine([current, .. parts[i..]]);
            if (info.LinkTarget is not null)
                next = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? next;
            current = next;
        }
        return current;
    }

    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content))[..16];

    public static string HashFile(string path) => Hash(File.ReadAllBytes(path));
}

public sealed class WorkspaceAccessException(string message) : Exception(message);
