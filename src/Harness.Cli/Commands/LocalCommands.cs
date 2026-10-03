using System.CommandLine;
using System.Diagnostics;
using Harness.Client;
using Harness.Core;
using Harness.Core.Config;
using Harness.Core.Models;
using Harness.Extensions.Plugins;

namespace Harness.Cli.Commands;

/// <summary>Commands that work on the harness home directly and do not need a running daemon.</summary>
internal static class LocalCommands
{
    public static IEnumerable<Command> Create()
    {
        yield return Init();
        yield return Models();
        yield return Config();
        yield return Trust();
        yield return Secrets();
        yield return Workspaces();
        yield return Agents();
        yield return Templates();
        yield return Sandboxes();
        yield return Plugins();
        yield return Install();
        yield return Uninstall();
        yield return Status();
    }

    private static Command Init()
    {
        Option<bool> force = new("--force") { Description = "Overwrite existing sample files" };
        Command init = new("init", "Create the harness home with a sample config, agent, prompt and sandbox profile.") { force };
        init.SetAction(p => Local(() =>
        {
            HarnessPaths paths = CliContext.Paths(p);
            paths.EnsureCreated();
            foreach ((string file, string content) in Samples.Files(paths))
            {
                if (File.Exists(file) && !p.GetValue(force)) { Console.WriteLine($"exists   {file}"); continue; }
                File.WriteAllText(file, content);
                Console.WriteLine($"created  {file}");
            }
            Console.WriteLine($"\nNext: edit {paths.ConfigFile} to point at your model, then run 'harness serve' and 'harness chat'.");
            return 0;
        }));
        return init;
    }

    private static Command Models()
    {
        Command models = new("models", "Model profiles.");
        Argument<string?> profile = new("profile") { Description = "Only this profile", Arity = ArgumentArity.ZeroOrOne };
        Command doctor = new("doctor", "Check model profiles (client builds, Ollama context window).") { profile };
        doctor.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            HarnessPaths paths = CliContext.Paths(p);
            ConfigCatalog catalog = new(paths);
            ModelDoctor doc = new(catalog, new ChatClientFactory(new SecretStore(paths)));
            IReadOnlyList<ModelDoctor.Finding> findings = await doc.CheckAsync(p.GetValue(profile), ct);
            foreach (ModelDoctor.Finding f in findings) Console.WriteLine($"{f.Level,-5} {f.Profile}: {f.Message}");
            return findings.Any(f => f.Level == "error") ? 1 : 0;
        }));
        models.Subcommands.Add(doctor);
        Command ls = new("ls", "List model profiles.");
        ls.SetAction(p => Local(() =>
        {
            ConfigCatalog catalog = new(CliContext.Paths(p));
            Output.Table(p, ["PROFILE", "PROVIDER", "MODEL", "ENDPOINT"], catalog.Config.Models.Select(kv => new[]
            {
                kv.Key, kv.Value.Provider, kv.Value.Model ?? kv.Value.Deployment ?? "", kv.Value.Endpoint,
            }));
            return 0;
        }));
        models.Subcommands.Add(ls);
        return models;
    }

    private static Command Config()
    {
        Command config = new("config", "Configuration files.");
        Command validate = new("validate", "Validate config.yaml, agents, run templates and sandbox profiles.");
        validate.SetAction(p => Local(() =>
        {
            IReadOnlyList<string> problems = new ConfigCatalog(CliContext.Paths(p)).Validate();
            foreach (string problem in problems) Console.WriteLine(problem);
            Console.WriteLine(problems.Count == 0 ? "configuration is valid" : $"{problems.Count} problem(s)");
            return problems.Count == 0 ? 0 : 1;
        }));
        config.Subcommands.Add(validate);
        Command path = new("path", "Print the harness home directory.");
        path.SetAction(p => { Console.WriteLine(CliContext.Paths(p).Home); return 0; });
        config.Subcommands.Add(path);
        return config;
    }

    private static Command Trust()
    {
        Argument<string> path = new("path") { Description = "Folder whose .harness/ configuration to trust", DefaultValueFactory = _ => "." };
        Option<bool> revoke = new("--revoke") { Description = "Remove trust instead" };
        Command trust = new("trust", "Trust a folder's .harness/ config (MCP servers, plugins, skills, looser approvals).") { path, revoke };
        trust.SetAction(p => Local(() =>
        {
            TrustStore store = new(CliContext.Paths(p));
            string folder = Path.GetFullPath(p.GetValue(path)!);
            if (p.GetValue(revoke)) { Console.WriteLine(store.Revoke(folder) ? $"revoked trust for {folder}" : "folder was not trusted"); return 0; }
            Console.WriteLine($"trusted {folder} (config hash {store.Trust(folder)[..12]}); a change to its .harness/ config needs trusting again");
            return 0;
        }));
        return trust;
    }

    private static Command Secrets()
    {
        Command secrets = new("secrets", "Secrets referenced from config as secret:<name>.");
        Argument<string> name = new("name");
        Argument<string?> value = new("value") { Description = "Value; read from stdin when omitted", Arity = ArgumentArity.ZeroOrOne };
        Command set = new("set", "Store a secret.") { name, value };
        set.SetAction(p => Local(() =>
        {
            string? v = p.GetValue(value);
            if (v is null)
            {
                if (!Console.IsInputRedirected) Console.Write($"value for {p.GetValue(name)}: ");
                v = ReadSecret();
            }
            new SecretStore(CliContext.Paths(p)).Set(p.GetValue(name)!, v);
            Console.WriteLine("stored");
            return 0;
        }));
        secrets.Subcommands.Add(set);
        Command ls = new("ls", "List secret names.");
        ls.SetAction(p => Local(() => { foreach (string n in new SecretStore(CliContext.Paths(p)).Names().Order()) Console.WriteLine(n); return 0; }));
        secrets.Subcommands.Add(ls);
        Command rm = new("rm", "Remove a secret.") { name };
        rm.SetAction(p => Local(() => { Console.WriteLine(new SecretStore(CliContext.Paths(p)).Remove(p.GetValue(name)!) ? "removed" : "not found"); return 0; }));
        secrets.Subcommands.Add(rm);
        return secrets;
    }

    private static Command Workspaces()
    {
        Command workspace = new("workspace", "Named workspaces for remote clients.");
        Argument<string> name = new("name");
        Argument<string> path = new("path") { DefaultValueFactory = _ => "." };
        Command add = new("add", "Register a named workspace.") { name, path };
        add.SetAction(p => Local(() =>
        {
            string full = Path.GetFullPath(p.GetValue(path)!);
            if (!Directory.Exists(full)) throw new CliException($"{full} does not exist.");
            new ConfigCatalog(CliContext.Paths(p)).SetWorkspace(p.GetValue(name)!, full);
            Console.WriteLine($"{p.GetValue(name)} → {full}");
            return 0;
        }));
        workspace.Subcommands.Add(add);
        Command ls = new("ls", "List named workspaces.");
        ls.SetAction(p => Local(() =>
        {
            Output.Table(p, ["NAME", "PATH"], new ConfigCatalog(CliContext.Paths(p)).Workspaces.OrderBy(kv => kv.Key).Select(kv => new[] { kv.Key, kv.Value }));
            return 0;
        }));
        workspace.Subcommands.Add(ls);
        Command rm = new("rm", "Remove a named workspace (the folder is untouched).") { name };
        rm.SetAction(p => Local(() => { Console.WriteLine(new ConfigCatalog(CliContext.Paths(p)).RemoveWorkspace(p.GetValue(name)!) ? "removed" : "not found"); return 0; }));
        workspace.Subcommands.Add(rm);
        return workspace;
    }

    private static Command Agents()
    {
        Command agents = new("agents", "Agent definitions.");
        Command ls = new("ls", "List agents.");
        ls.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            IReadOnlyList<AgentDto> rows;
            if (CliContext.IsRemote(p))
            {
                using HarnessClient c = CliContext.Connect(p);
                rows = await c.AgentsAsync(ct);
            }
            else rows = [.. new ConfigCatalog(CliContext.Paths(p)).Agents().Select(a => new AgentDto(a.Name, a.Description, a.Model, a.Sandbox, a.Tools.Builtin))];
            if (p.GetValue(CliContext.Json)) { Output.Json(rows); return 0; }
            Output.Table(p, ["AGENT", "MODEL", "SANDBOX", "TOOLS", "DESCRIPTION"], rows.Select(a => new[] { a.Name, a.Model, a.Sandbox, string.Join(",", a.Tools), a.Description ?? "" }));
            return 0;
        }));
        agents.Subcommands.Add(ls);
        return agents;
    }

    private static Command Sandboxes()
    {
        Command sandbox = new("sandbox", "Sandbox profiles from sandboxes.yaml.");
        Command ls = new("ls", "List sandbox profiles.");
        ls.SetAction(p => Local(() =>
        {
            ConfigCatalog catalog = new(CliContext.Paths(p));
            Output.Table(p, ["PROFILE", "TYPE", "NETWORK", "ALLOW HOSTS", "MOUNTS"], catalog.Sandboxes.Select(kv => new[]
            {
                kv.Key, kv.Value.Type, kv.Value.Network, kv.Value.AllowHosts.Count > 0 ? string.Join(", ", kv.Value.AllowHosts) : "-", kv.Value.Mounts.Count.ToString(),
            }));
            return 0;
        }));
        sandbox.Subcommands.Add(ls);

        Argument<string> profile = new("profile") { Description = "Sandbox profile name" };
        Command test = new("test", "Start the profile in the daemon and check that its mounts, network and limits hold.") { profile };
        test.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            IReadOnlyList<SandboxProbeDto> results = await c.TestSandboxAsync(p.GetValue(profile)!, ct);
            if (p.GetValue(CliContext.Json)) Output.Json(results);
            else Output.Table(p, ["CHECK", "RESULT", "DETAIL"], results.Select(r => new[] { r.Check, r.Status, r.Detail }));
            return results.Any(r => r.Status == "fail") ? 1 : 0;
        }));
        sandbox.Subcommands.Add(test);
        return sandbox;
    }

    private static Command Templates()
    {
        Command templates = new("templates", "Run templates.");
        Command ls = new("ls", "List run templates.");
        ls.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            IReadOnlyList<TemplateDto> rows;
            if (CliContext.IsRemote(p))
            {
                using HarnessClient c = CliContext.Connect(p);
                rows = await c.TemplatesAsync(ct);
            }
            else rows = [.. new ConfigCatalog(CliContext.Paths(p)).Templates().Select(t => new TemplateDto(t.Name, t.Description, t.Agent, t.Sandbox, t.Output.Kind,
                [.. Harness.Runs.Templates.WorkspaceBuilder.StepsOf(t).OfType<System.Text.Json.Nodes.JsonObject>().Select(s => s.First().Key)], t.Workspace.Keep))];
            if (p.GetValue(CliContext.Json)) { Output.Json(rows); return 0; }
            Output.Table(p, ["TEMPLATE", "AGENT", "SANDBOX", "STEPS", "OUTPUT", "KEEP", "DESCRIPTION"], rows.Select(t => new[]
            {
                t.Name, t.Agent ?? "-", t.Sandbox ?? "-", string.Join(",", t.Steps), t.OutputKind, t.Keep, t.Description ?? "",
            }));
            return 0;
        }));
        templates.Subcommands.Add(ls);
        return templates;
    }

    private static Command Plugins()
    {
        Command plugin = new("plugin", "C# plugins.");
        Argument<string> path = new("path") { Description = "Folder with plugin.json and the built plugin assemblies" };
        Command add = new("add", "Install a plugin folder into the harness home (restart the daemon to load it).") { path };
        add.SetAction(p => Local(() =>
        {
            PluginManifest m = PluginRegistry.Install(CliContext.Paths(p), Path.GetFullPath(p.GetValue(path)!));
            Console.WriteLine($"installed {m.Id} {m.Version} (sha256 {m.Sha256![..12]}); run 'harness restart' or restart the service to load it");
            return 0;
        }));
        plugin.Subcommands.Add(add);
        Command ls = new("ls", "List installed plugins.");
        ls.SetAction(p => Local(() =>
        {
            string dir = CliContext.Paths(p).PluginsDir;
            List<string[]> rows = [];
            if (Directory.Exists(dir))
                foreach (string d in Directory.EnumerateDirectories(dir).Where(d => File.Exists(Path.Combine(d, "plugin.json"))))
                {
                    PluginManifest m = PluginManifest.Load(d);
                    rows.Add([m.Id, m.Version, m.EntryAssembly, string.Join(",", m.Permissions)]);
                }
            Output.Table(p, ["PLUGIN", "VERSION", "ENTRY", "PERMISSIONS"], rows);
            return 0;
        }));
        plugin.Subcommands.Add(ls);
        return plugin;
    }

    private const string UnitName = "harness.service";

    private static Command Install()
    {
        Command install = new("install", "Install the daemon as a user service (systemd on Linux).");
        install.SetAction(p => Local(() =>
        {
            if (!OperatingSystem.IsLinux()) throw new CliException("'harness install' currently supports Linux (systemd). On macOS and Windows run 'harness serve' yourself for now.");
            string exe = Environment.ProcessPath ?? throw new CliException("Cannot determine the harness executable path.");
            string home = CliContext.Paths(p).Home;
            string unitDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user");
            Directory.CreateDirectory(unitDir);
            File.WriteAllText(Path.Combine(unitDir, UnitName), $"""
                [Unit]
                Description=harness coding agent daemon

                [Service]
                ExecStart="{exe}" serve
                Environment=HARNESS_HOME={home}
                Restart=on-failure
                RestartSec=5

                [Install]
                WantedBy=default.target
                """);
            Run("systemctl", "--user", "daemon-reload");
            Run("systemctl", "--user", "enable", "--now", UnitName);
            // Lingering keeps the user service running without a login session.
            Run("loginctl", "enable-linger", Environment.UserName);
            Console.WriteLine($"installed {UnitName}; logs: journalctl --user -u {UnitName}");
            return 0;
        }));
        return install;
    }

    private static Command Uninstall()
    {
        Command uninstall = new("uninstall", "Remove the user service installed by 'harness install'.");
        uninstall.SetAction(p => Local(() =>
        {
            if (!OperatingSystem.IsLinux()) throw new CliException("'harness uninstall' currently supports Linux (systemd).");
            Run("systemctl", "--user", "disable", "--now", UnitName);
            File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", UnitName));
            Run("systemctl", "--user", "daemon-reload");
            Console.WriteLine("uninstalled");
            return 0;
        }));
        return uninstall;
    }

    private static Command Status()
    {
        Command status = new("status", "Show whether the daemon is running and what it is doing.");
        status.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            StatusDto s = await c.StatusAsync(ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(s); return 0; }
            Console.WriteLine($"daemon running · home {s.Home} · {s.ActiveRuns} active run(s) · {s.PendingApprovals} approval(s) waiting");
            foreach (string e in s.PluginErrors) Console.WriteLine($"warning: {e}");
            return 0;
        }));
        return status;
    }

    private static int Local(Func<int> body)
    {
        try { return body(); }
        catch (CliException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
        catch (ConfigException ex) { Console.Error.WriteLine($"config error: {ex.Message}"); return 1; }
        catch (IOException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
        catch (InvalidDataException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
    }

    private static void Run(string file, params string[] args)
    {
        using Process? process = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardError = true });
        process?.WaitForExit();
        if (process is { ExitCode: not 0 }) Console.Error.WriteLine($"warning: {file} {string.Join(' ', args)} exited {process.ExitCode}: {process.StandardError.ReadToEnd().Trim()}");
    }

    private static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.In.ReadToEnd().TrimEnd('\r', '\n');
        System.Text.StringBuilder sb = new();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
