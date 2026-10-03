namespace Harness.Core;

/// <summary>Locations under the harness home (<c>~/.harness</c>, or <c>HARNESS_HOME</c>).</summary>
public sealed class HarnessPaths
{
    public HarnessPaths(string home) => Home = Path.GetFullPath(ExpandHome(home));

    public static HarnessPaths Default() =>
        new(Environment.GetEnvironmentVariable("HARNESS_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harness"));

    public string Home { get; }
    public string ConfigFile => Path.Combine(Home, "config.yaml");
    public string SandboxesFile => Path.Combine(Home, "sandboxes.yaml");
    public string WorkspacesFile => Path.Combine(Home, "workspaces.yaml");
    public string TrustFile => Path.Combine(Home, "trust.json");
    public string SecretsFile => Path.Combine(Home, "secrets.json");
    public string McpFile => Path.Combine(Home, "mcp.json");
    public string TuiFile => Path.Combine(Home, "tui.yaml");
    /// <summary>API tokens and device pairings; unlike harness.db, not rebuildable from other files.</summary>
    public string AuthDatabase => Path.Combine(Home, "auth.db");
    public string CredentialsFile => Path.Combine(Home, "credentials.json");
    public string GlobalAgentsMd => Path.Combine(Home, "AGENTS.md");
    public string AgentsDir => Path.Combine(Home, "agents");
    public string PromptsDir => Path.Combine(Home, "prompts");
    public string SkillsDir => Path.Combine(Home, "skills");
    public string TriggersDir => Path.Combine(Home, "triggers");
    public string TemplatesDir => Path.Combine(Home, "templates");
    public string PluginsDir => Path.Combine(Home, "plugins");
    public string SessionsDir => Path.Combine(Home, "sessions");
    public string RunsDir => Path.Combine(Home, "runs");
    public string LogsDir => Path.Combine(Home, "logs");
    public string Database => Path.Combine(Home, "harness.db");
    public string Socket => Path.Combine(Home, "harness.sock");

    public void EnsureCreated()
    {
        foreach (string dir in new[] { Home, AgentsDir, PromptsDir, SkillsDir, TriggersDir, TemplatesDir, PluginsDir, SessionsDir, RunsDir, LogsDir })
            Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Resolves a path from a config file: <c>~</c> expands to the user profile, relative paths are relative to the harness home.</summary>
    public string Resolve(string path)
    {
        path = ExpandHome(path);
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(Home, path));
    }

    public static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
            : path;
}
