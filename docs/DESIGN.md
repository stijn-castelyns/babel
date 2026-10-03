# Custom Coding Agent Harness — Architecture & Design

Oct 3, 2026 · @Stijn Castelyns

## Overview

Build one self-contained .NET 10 executable, `harness`, that runs as a daemon and as its own CLI client. Microsoft Agent Framework 1.x owns the agent loop; the harness owns everything around it: tools, sandboxing, sessions, triggers, plugins, and the remote API.

The daemon is the only process that talks to models, runs tools or touches sessions. The CLI, the web API and the PWA are all clients of the same daemon API, so a run started from WhatsApp can be watched from the terminal or the phone.

Guiding principles:

- **Agent Framework for the loop, harness for the world.** Use `ChatClientAgent`, `AIContextProvider`, `ChatHistoryProvider`, middleware, compaction and `AgentSkillsProvider` as-is. Build only what the framework lacks: sandbox, triggers, run templates, plugin loading, remote API.
- **Chat Completions only.** Every model goes through an `IChatClient` built from a chat-completions endpoint. Nothing in the harness may depend on Responses-API features (hosted shell, server-side threads, background responses).
- **Everything declarative is a file.** Config, agents, triggers, run templates, skills, prompts and sessions live in plain folders, so the whole setup can be versioned in git.
- **Code extensions go through one door.** A plugin is a C# assembly implementing the plugin SDK. MCP and skills cover everything that does not need compiled code.
- **Sandbox by default for unattended runs.** Interactive sessions may run on the host with approvals; triggered runs always run inside a sandbox.

## Technology stack

Target .NET 10 (LTS) and Agent Framework 1.23, the current stable line. [Microsoft.Agents.AI 1.23.0](https://www.nuget.org/packages/Microsoft.Agents.AI/) shipped on 29 Sep 2026 and depends on Microsoft.Extensions.AI 10.10. Agent Framework [reached 1.0 GA on 3 Apr 2026](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/) with stable APIs; Skills and the harness patterns were still marked preview at GA, so pin versions and wrap them behind harness interfaces.

| Concern | Choice | Notes |
| --- | --- | --- |
| Runtime | .NET 10, self-contained single-file publish | No trimming, no Native AOT: plugins need `AssemblyLoadContext` and JIT |
| Agent loop | `Microsoft.Agents.AI` 1.23 (`ChatClientAgent`, `AgentSession`) | Middleware, context providers, compaction, tool approval |
| Model abstraction | `Microsoft.Extensions.AI` 10.10 (`IChatClient`) | One pipeline for every provider |
| Azure OpenAI | `Azure.AI.OpenAI` → `GetChatClient(deployment).AsIChatClient()` | Never `GetResponsesClient` |
| Ollama | `OllamaSharp` `OllamaApiClient` (implements `IChatClient`) | Native `/api/chat`; [Agent Framework's documented path](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/ollama) |
| Skills | `AgentSkillsProvider` / `AgentSkillsProviderBuilder` | File, class and inline skills ([post](https://devblogs.microsoft.com/agent-framework/agent-skills-in-net-three-ways-to-author-one-provider-to-run-them/)) |
| MCP client | `ModelContextProtocol` 1.x | [1.0.0 is stable](https://csharp.sdk.modelcontextprotocol.io/versioning.html); MCP tools are `AIFunction`s |
| Web host | ASP.NET Core 10 (Kestrel) inside the daemon | REST + Server-Sent Events |
| Auth | ASP.NET Core Identity with [built-in passkeys](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/passkeys) | Requires Identity schema version 3 |
| Local index | SQLite via EF Core (`Microsoft.Data.Sqlite`) | Index only; sessions and runs stay as files |
| Scheduling | `Cronos` cron parser + an in-process timer service | No Quartz needed for one machine |
| CLI | `System.CommandLine` + `Spectre.Console` | Plus Terminal.Gui 2.5 for the full-screen TUI; all in the daemon's binary |
| PWA | TypeScript SPA (Preact + Vite), embedded in the binary | Blazor WASM is the all-C# alternative, with a much larger first download on a phone |
| Sandbox | bubblewrap on Linux, Docker/Podman anywhere | Behind one `ISandboxProvider` interface |
| Telemetry | OpenTelemetry (built into Agent Framework) | Exported to local files or an OTLP endpoint |

Two open-source .NET projects already build on Agent Framework in this space. Read them for design ideas only; the harness takes no dependency on either: [DotHarness/dotcraft](https://github.com/DotHarness/dotcraft) and [openclaw.net](https://github.com/clawdotnet/openclaw.net).

## System architecture

&#91;embedded content: harness architecture · 3 client types, 9 daemon components, 3 external dependencies\]

Clients never call models or touch files directly: they send commands to the API host and read events back. Triggered work enters through the trigger engine, and anything that executes code leaves the daemon only through the per-run sandbox.

## Solution layout

Use eleven projects with one hard rule: plugins reference only `Harness.Sdk`, and `Harness.Sdk` references only `Microsoft.Extensions.AI.Abstractions` and `Microsoft.Agents.AI.Abstractions`. That keeps the plugin contract stable while the harness internals change.

```
harness/
├── src/
│   ├── Harness.Sdk/            # Plugin SDK (published to NuGet): IHarnessPlugin, hooks, events, ITrigger, IOutputSink
│   ├── Harness.Core/           # Agent factory, model providers, prompt layering, sessions, compaction config
│   ├── Harness.Tools/          # Built-in tools: read, list, glob, grep, edit, write, shell
│   ├── Harness.Sandbox/        # ISandboxProvider + none / bubblewrap / container providers
│   ├── Harness.Extensions/     # MCP manager, skills loader, plugin loader (AssemblyLoadContext)
│   ├── Harness.Runs/           # Run orchestrator, run journal, templates, output contracts
│   ├── Harness.Triggers/       # Trigger engine + schedule, webhook, file-watch, Signal, WhatsApp, Messenger
│   ├── Harness.Server/         # ASP.NET Core: REST, SSE, WebSocket, Identity + passkeys, embedded PWA
│   ├── Harness.Client/         # Typed client for the daemon API (used by the CLI and tests)
│   ├── Harness.Cli/            # System.CommandLine commands, Terminal.Gui TUI, plain-mode output
│   └── Harness/                # Entry point; publishes the single `harness` executable
├── web/pwa/                    # Preact + Vite; build output embedded into Harness.Server
├── templates/harness-plugin/   # `dotnet new harness-plugin` template
├── samples/plugins/            # Example plugins (git hooks, Jira trigger, Slack sink)
└── tests/
```

The `harness` binary carries every role. `harness serve` starts the daemon; every other command (`harness chat`, `harness runs watch`, `harness trigger fire`) is a client that connects to a running daemon. Locally the CLI connects over a Unix domain socket (a named pipe on Windows), so file permissions are the authentication; remotely it uses HTTPS with a token.

## Agent core

Every agent is a `ChatClientAgent` built per run by one `AgentFactory` from two files: a model profile and an agent definition. The factory is the single place where tools, skills, MCP, plugins, prompts, history and hooks are wired together.

### Model profiles

Profiles live in `config.yaml`. Both providers produce an `IChatClient`; nothing above this layer knows which one is in use.

```yaml
models:
  local:
    provider: ollama
    endpoint: http://localhost:11434
    model: <coding-model-tag>        # create it from a Modelfile with PARAMETER num_ctx set high
  azure:
    provider: azure-openai
    endpoint: https://<resource>.openai.azure.com
    deployment: <chat-deployment>
    auth: { type: entra }             # or { type: apiKey, secret: env:AZURE_OPENAI_KEY }
```

```csharp
public IChatClient Create(ModelProfile p) => p.Provider switch
{
    "ollama" => new OllamaApiClient(new Uri(p.Endpoint), p.Model),
    "azure-openai" => new AzureOpenAIClient(new Uri(p.Endpoint), credentials.For(p))
        .GetChatClient(p.Deployment)          // Chat Completions only
        .AsIChatClient(),
    _ => throw new NotSupportedException(p.Provider)
};
```

Add a startup check that rejects any profile whose client type is a Responses client, so the chat-completions rule cannot regress. For Ollama, ship a `harness models doctor` command that warns when the model's context window is small; Ollama's default is far below what a coding agent needs.

### Agent definitions

An agent definition is a YAML file in `agents/`. It names a model profile and declares everything the agent may use.

```yaml
name: coder
model: azure
instructions: prompts/coder.md
tools:
  builtin: [read, list, glob, grep, edit, write, shell]
  mcp: [github]
skills: [skills/]
plugins: [Contoso.GitHooks]
approvals: { shell: ask, write: allow, mcp: ask }   # ask | allow | deny | allowlist
sandbox: workspace                                   # profile from sandboxes.yaml
compaction: { toolResultsAfter: 20, slidingWindowTurns: 30 }
limits: { maxToolIterations: 60, maxRunMinutes: 30 }
```

### The factory

The three Agent Framework middleware layers map directly onto the harness hook points: `IChatClient` middleware for model calls, function-calling middleware for tool calls, and agent-run middleware for whole runs ([docs](https://learn.microsoft.com/en-us/agent-framework/agents/middleware/)).

```csharp
public async Task<AIAgent> BuildAsync(AgentDefinition def, RunContext run, CancellationToken ct)
{
    IChatClient chat = models.Create(def.Model).AsBuilder()
        .Use(getResponseFunc: hooks.ModelCall(run), getStreamingResponseFunc: hooks.ModelCallStreaming(run))
        .UseOpenTelemetry()
        .Build();

    IList<AITool> tools = approvals.Wrap(def.Approvals, run,
    [
        .. builtins.Create(def.Tools.Builtin, run.Workspace, run.Sandbox),
        .. await mcp.GetToolsAsync(def.Tools.Mcp, run, ct),
        .. plugins.GetTools(def, run),
    ]);

    AIAgent agent = chat.AsBuilder()
        .UseAIContextProviders(new CompactionProvider(def.Compaction.ToStrategy()))
        .BuildAIAgent(new ChatClientAgentOptions
        {
            Name = def.Name,
            ChatOptions = new ChatOptions { Instructions = prompts.Base(def, run), Tools = tools },
            AIContextProviders = [prompts.FolderProvider(run), skills.Build(def, run), .. plugins.GetContextProviders(def, run)],
            ChatHistoryProvider = new FileChatHistoryProvider(run.Session),
        });

    return agent.AsBuilder()
        .Use(runFunc: hooks.Run(run), runStreamingFunc: hooks.RunStreaming(run))
        .Use(hooks.ToolCall(run))
        .Build();
}
```

`approvals.Wrap` wraps any tool whose policy is `ask` in `ApprovalRequiredAIFunction`. The run then surfaces `FunctionApprovalRequestContent`, which the harness turns into an approval event that any connected client (CLI, PWA push, or the originating chat channel) can answer. Unattended runs with no approver configured treat `ask` as `deny`.

## Built-in tools

Ship seven tools and nothing else: `read`, `list`, `glob`, `grep`, `edit`, `write` and `shell`. Every richer capability comes from MCP, skills or plugins, so the base prompt stays small and the tool list stays easy for local models to use correctly.

| Tool | Purpose | How it saves context |
| --- | --- | --- |
| `read` | Read a text file as numbered lines | Pages by `offset`/`limit` (default 400 lines); clips long lines; replies "unchanged since turn N" when the same range is re-read |
| `list` | Directory tree | Depth 2 by default; honours `.gitignore`; caps entries and reports how many were hidden |
| `glob` | Find files by pattern | Returns paths only, newest first, capped at 100 |
| `grep` | Search file contents | Uses ripgrep when present; modes `files`, `content`, `count`; default mode `files` |
| `edit` | Exact string replace in a file | `old` must match once (or `replaceAll`); returns a 3-line diff, not the file |
| `write` | Create or overwrite a file | Returns byte and line counts only |
| `shell` | Run a command in the sandbox | Head + tail of output (16 KB cap); full output spilled to a file the agent can `read` |

Rules that apply to all of them:

- **Plain text results, never JSON envelopes.** Each result starts with a one-line header (`lines 1–400 of 1,840 · next: offset=401`) so the model knows how to continue.
- **Spill, don't truncate silently.** Any result over the per-tool token budget is written to `.harness/spill/<id>.txt` in the workspace and replaced with its head, tail and path.
- **Read before write.** `edit` and `write` on an existing file fail unless the file was read in this session and its hash has not changed since. This prevents clobbering edits made by the user or another run.
- **One path policy.** All file tools resolve paths through `IWorkspace`, which canonicalises symlinks and rejects anything outside the workspace roots. The sandbox enforces the same boundary a second time.
- **Old results fade.** `ToolResultCompactionStrategy` replaces stale tool results with one-line stubs once the conversation passes the configured message count.

Tools are plain C# classes turned into `AIFunction`s, so plugins can ship tools the same way:

```csharp
public sealed class ReadTool(IWorkspace workspace, ToolBudget budget)
{
    [Description("Read a text file as numbered lines. Page with offset and limit.")]
    public Task<string> Read(
        [Description("Path relative to the workspace root")] string path,
        int offset = 1, int limit = 400, CancellationToken ct = default)
        => workspace.ReadLinesAsync(path, offset, limit, budget, ct);
}

AITool read = AIFunctionFactory.Create(new ReadTool(ws, budget).Read, name: "read");
```

`shell` runs `bash -lc` on Linux and macOS and `pwsh -NoProfile -Command` on Windows, always through `ISandbox.ExecAsync`. It takes `command`, `workdir`, `timeoutSeconds` (default 120, max 600) and `background`. Background commands return a handle; `shell` with `handle=` reads their latest output or stops them, which avoids a separate process-management tool.

## Layered and folder-specific prompts

The system prompt is composed from up to seven layers, most general first, so a nearer layer can refine or override a farther one. Folder layers use `AGENTS.md`, the convention most coding agents already read, with the file names configurable.

| # | Layer | Source | Loaded |
| --- | --- | --- | --- |
| 1 | Harness base | Built in: tool rules, environment block (OS, date, workspace, sandbox mode) | Every run |
| 2 | Agent | `instructions:` file in the agent definition | Every run |
| 3 | User global | `~/.harness/AGENTS.md` | Every run |
| 4 | Folder chain | Each `AGENTS.md` from the workspace root down to the working directory | Every run |
| 5 | Subfolder, lazy | `AGENTS.md` in a folder the agent touches later | First tool call inside that folder |
| 6 | Run | Trigger template instructions + output contract | Triggered runs only |
| 7 | Plugins | `IPromptContributor` implementations | When the plugin is enabled |

Layers 1–4, 6 and 7 are injected by one `AIContextProvider`, so they are recomputed per run and never stored in session history. Files are cached by content hash, so editing an `AGENTS.md` takes effect on the next turn without restarting anything.

```csharp
public sealed class FolderInstructionsProvider(PromptComposer composer) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        RunContext run = RunContext.From(context);          // harness state attached to the session
        string text = await composer.ComposeAsync(run, ct);  // layers 1-4, 6, 7
        return new AIContext { Instructions = text };
    }
}
```

Layer 5 cannot use the context provider, because providers run once per agent invocation and a coding run may spend dozens of tool calls inside one invocation. Instead, the tool-call hook appends the folder's instructions to the result of the first `read`, `edit` or `list` inside that folder, once per session. Monorepos get per-package rules without paying for every package up front.

### Folder configuration beyond prompts

A folder can also carry `.harness/config.yaml`, which overrides the agent definition for runs in that folder: model profile, extra MCP servers, skills directories, denied tools, sandbox profile. Lists append and scalars override, and `!replace` forces a list to replace instead.

Treat folder configuration as untrusted, because it usually arrives with a cloned repository. Prompt layers always load. Anything that adds executable surface (MCP servers, plugins, skill scripts) or loosens approvals is ignored until the user runs `harness trust <path>`, which records the folder and a hash of its config. A changed hash requires trusting again.

## Extensibility: MCP, skills, plugins and hooks

Offer four extension mechanisms, ordered from least to most trusted. Steer users toward the least-trusted one that does the job.

| Mechanism | Runs where | Trust | Use for |
| --- | --- | --- | --- |
| Skill | Prompt + scripts in the sandbox | Low | Procedures, reference material, small scripts |
| MCP server | Separate process (sandboxed for stdio) or remote | Low–medium | External systems, tools written in any language |
| Command hook | Shell command, JSON on stdin | Medium | Policy checks and notifications without writing C# |
| C# plugin | In the daemon process | Full | Tools, hooks, triggers, output sinks, sandbox providers |

### MCP

Read MCP configuration from `~/.harness/mcp.json` and each trusted folder's `.harness/mcp.json`, using the common `mcpServers` shape so existing configs can be copied in. Support stdio and streamable HTTP transports.

An `McpManager` starts servers lazily on first use. Remote HTTP servers are shared across runs; stdio servers are spawned per run inside that run's sandbox, because they execute arbitrary commands. Tools are exposed as `mcp__<server>__<tool>`, with per-server `allow` and `deny` lists to keep the tool list short. OAuth tokens for remote servers go in the secret store, never in config.

```csharp
await using McpClient client = await McpClient.CreateAsync(
    new StdioClientTransport(new() { Name = "github", Command = cmd, Arguments = args }));

IList<McpClientTool> tools = await client.ListToolsAsync();   // each one is an AIFunction
```

Because `McpClientTool` is an `AIFunction`, MCP tools flow through the same approval wrapper and tool-call hooks as built-in tools.

### Skills

Use Agent Framework's `AgentSkillsProvider` rather than building a loader. It already supports file-based skills (`SKILL.md` + scripts + references), class-based skills (`AgentClassSkill<T>`) and inline skills, and advertises only each skill's name and description until the agent loads it ([post](https://devblogs.microsoft.com/agent-framework/agent-skills-in-net-three-ways-to-author-one-provider-to-run-them/)).

```csharp
var skills = new AgentSkillsProviderBuilder()
    .UseFileSkill(Path.Combine(harnessHome, "skills"))
    .UseFileSkill(Path.Combine(workspace, ".harness", "skills"))   // trusted folders only
    .UseFileScriptRunner(sandboxScriptRunner.RunAsync)              // never SubprocessScriptRunner
    .UseFilter((skill, _) => def.Skills.Allows(skill.Frontmatter.Name))
    .Build();
```

Replace the sample `SubprocessScriptRunner` with a runner that executes inside the run's sandbox; the framework's own guidance says production runners need sandboxing, limits and audit logging. All three skill tools (`load_skill`, `read_skill_resource`, `run_skill_script`) require approval by default. Auto-approve the first two and route `run_skill_script` through the normal approval policy. Plugins contribute class-based skills with `AddSkill`.

### C# plugins

A plugin is a .NET 10 class library that references only `Harness.Sdk`. Install it as a folder under `~/.harness/plugins/<id>/` with a `plugin.json` manifest (id, version, entry assembly, supported SDK range, declared permissions). `dotnet new harness-plugin` scaffolds one; `harness plugin add <path|nupkg>` installs it.

Each plugin loads into its own collectible `AssemblyLoadContext`. The SDK, `Microsoft.Extensions.AI.Abstractions` and `Microsoft.Agents.AI.Abstractions` are always resolved from the host so types match across the boundary; everything else resolves from the plugin folder through `AssemblyDependencyResolver`. In-process plugins are fully trusted, so code that should not be trusted belongs behind MCP instead.

```csharp
public interface IHarnessPlugin
{
    string Id { get; }
    void Configure(IPluginBuilder plugin);
}

public interface IPluginBuilder
{
    IServiceCollection Services { get; }             // plugin-scoped DI
    IConfiguration Configuration { get; }            // the plugin's section of config.yaml
    IPluginBuilder AddTools<T>() where T : class;     // [Description] methods become AIFunctions
    IPluginBuilder AddTool(Func<IToolContext, AITool> factory);
    IPluginBuilder AddSkill(AgentSkill skill);
    IPluginBuilder AddContextProvider<T>() where T : AIContextProvider;
    IPluginBuilder AddPromptContributor<T>() where T : class, IPromptContributor;
    IPluginBuilder AddHook<T>() where T : class, IHarnessHook;
    IPluginBuilder AddTriggerSource<T>(string type) where T : class, ITriggerSource;
    IPluginBuilder AddOutputSink<T>(string type) where T : class, IOutputSink;
    IPluginBuilder AddSandboxProvider<T>(string type) where T : class, ISandboxProvider;
}
```

### Hooks

One `IHarnessHook` interface with default no-op methods covers every lifecycle point. The harness implements it on top of the three Agent Framework middleware layers plus its own run and trigger events, so plugin authors never touch middleware delegates directly.

| Hook | Fires | A hook can |
| --- | --- | --- |
| `OnTriggerFired` | A trigger produced an event | Drop it, enrich it, pick a different template |
| `OnRunStarting` | Before the first model call | Edit input messages, cancel the run |
| `OnModelCalling` / `OnModelCalled` | Each model request | Edit messages or options; read token usage |
| `OnToolCalling` | Before a tool runs | Block with a reason, rewrite arguments, pre-approve |
| `OnToolCalled` | After a tool runs | Rewrite the result (redact secrets, add notes) |
| `OnApprovalRequested` | A tool needs approval | Decide automatically or defer to a human |
| `OnCompacting` | Before history compaction | Pin messages that must survive |
| `OnRunCompleted` | Run reached a final state | Validate or transform output, notify |

```csharp
public sealed class GitGuardPlugin : IHarnessPlugin
{
    public string Id => "contoso.git-guard";
    public void Configure(IPluginBuilder plugin) => plugin
        .AddTools<GitTools>()
        .AddHook<NoForcePush>();
}

sealed class NoForcePush : IHarnessHook
{
    public ValueTask OnToolCallingAsync(ToolCallingContext ctx, CancellationToken ct)
    {
        if (ctx.ToolName == "shell" && ctx.Argument<string>("command")?.Contains("push --force") == true)
            ctx.Block("Force-push is blocked by policy.");
        return ValueTask.CompletedTask;
    }
}
```

Command hooks reuse the same events without C#: a `hooks:` entry in config names an event, an optional tool matcher and a command. The harness sends the context as JSON on stdin; exit code 0 continues, exit code 2 blocks with stderr as the reason, and JSON on stdout may rewrite arguments or results. A built-in plugin implements this, which also proves the hook API is complete.

## Sessions and local persistence

Store each session as a folder of append-only JSONL files, with SQLite only as a rebuildable index. Chat Completions keeps no state on the server, so [history is client-managed by design](https://devblogs.microsoft.com/agent-framework/chat-history-storage-patterns-in-microsoft-agent-framework/) and the harness owns it completely.

A **session** is a conversation. A **run** is one execution against a session: one interactive user turn, or one triggered job. Triggered runs start a fresh session by default, or continue a keyed one (for example `whatsapp:+31612345678`), so a chat channel keeps its conversation across messages.

```
~/.harness/sessions/2026/10/<session-id>/
├── session.json      # id, agent, model, workspace, title, parent + fork point, status, token totals,
│                     # and the serialized AgentSession state
├── history.jsonl     # every ChatMessage, never rewritten (source of truth)
├── events.jsonl      # UI event log: run started, tool start/end, approvals, usage, errors
├── checkpoints.jsonl # compaction records: "messages ≤ seq 412 are summarised as …"
├── lock              # lease held by the run currently writing
└── artifacts/        # spilled tool output, attachments, run outputs
```

A custom `FileChatHistoryProvider` plugs this into Agent Framework as the agent's `ChatHistoryProvider`. It loads the latest checkpoint summary plus the messages after it, appends new messages as they arrive, and flushes at each turn boundary. Messages are serialised with `AIJsonUtilities.DefaultOptions`, so function calls, results and other content types round-trip without custom converters.

Rules that keep this robust:

- **One writer per session.** A run takes the `lock` lease before writing. A second client that wants to send a message either queues behind the run or is told the session is busy.
- **Crash-safe by construction.** A torn last line is ignored on load. A tool call without a matching result gets a synthetic "interrupted" result on resume, because Chat Completions rejects history with orphaned tool calls.
- **Compaction never deletes.** Compaction writes a checkpoint record; `history.jsonl` keeps everything, so a run can be audited or a session forked from any point.
- **Index is disposable.** SQLite holds title, workspace, agent, tags, last activity and a full-text index of messages for `harness sessions search`. `harness sessions reindex` rebuilds it from the folders.

Session operations exposed through the API and CLI: `list`, `show`, `resume`, `fork --at <seq>`, `export --format md|json`, `delete`, plus a retention policy per trigger (for example, keep scheduled-run sessions 30 days).

## Sandboxing

Split execution in two: the daemon keeps the model calls, secrets and file tools; everything that executes code (shell, skill scripts, stdio MCP servers) runs inside a per-run sandbox. API keys therefore never enter the sandbox, and a compromised command cannot read them.

File tools run in the daemon against the host path of the workspace mount, behind the path policy. That keeps `read` and `grep` fast and avoids an exec per file operation, while the sandbox mount enforces the same boundary for anything the agent runs.

```csharp
public interface ISandboxProvider
{
    string Type { get; }                                   // "none", "bubblewrap", "container", plugin types
    Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct);
}

public interface ISandbox : IAsyncDisposable
{
    string Id { get; }
    PathMap Paths { get; }                                 // host path <-> sandbox path
    Task<ExecResult> ExecAsync(ExecRequest request, CancellationToken ct);       // shell, skill scripts
    Task<ISandboxProcess> StartAsync(ExecRequest request, CancellationToken ct); // stdio MCP, background jobs
}
```

| Provider | Platforms | How it works | Best for |
| --- | --- | --- | --- |
| `none` | All | Host execution; path policy and approvals only | Interactive work on your own repo |
| `bubblewrap` | Linux | Per-command `bwrap`: system dirs read-only, workspace read-write, private `/tmp`, all namespaces unshared, dies with parent | Fast, rootless isolation on Linux hosts |
| `container` | Linux, macOS, Windows | One Docker or Podman container per run; `exec` per command; CPU, memory and PID limits | Reproducible toolchains; isolation on Windows and macOS |
| Plugin | Any | `AddSandboxProvider<T>` | MicroVMs, remote runners, Windows-native isolation |

Sandboxes are described by named profiles, and agent definitions and trigger templates refer to them by name:

```yaml
# sandboxes.yaml
workspace:
  type: bubblewrap
  network: allowlist                 # none | allowlist | full
  allowHosts: [api.nuget.org, registry.npmjs.org, github.com]
  mounts:
    - { host: "{workspace}", path: /workspace, mode: rw }
    - { host: "~/.nuget/packages", path: /home/agent/.nuget/packages, mode: ro }
  env: { DOTNET_CLI_TELEMETRY_OPTOUT: "1" }
  limits: { cpus: 2, memoryMb: 4096, pids: 256, wallClockMinutes: 30 }

dotnet-ci:
  type: container
  image: mcr.microsoft.com/dotnet/sdk:10.0
  network: none
  mounts: [{ host: "{workspace}", path: /workspace, mode: rw }]
```

`network: allowlist` routes all traffic through a small egress proxy inside the daemon that only permits the listed hosts. The sandbox has no other route out: the bubblewrap provider unshares the network and exposes the proxy through a bound Unix socket, and the container provider uses an internal network whose only reachable host is the proxy.

Policy defaults: interactive sessions default to the agent's profile (often `none` with approvals); triggered runs refuse to start with `none` unless the trigger explicitly sets `allowUnsandboxed: true`. `harness sandbox test <profile>` runs a probe script that checks the mounts, network and limits actually hold.

## Triggers, run templates and output contracts

A triggered run is defined by three files: a **trigger** (when to run and for whom), a **run template** (what filesystem and agent the run gets) and an **output contract** inside the template (what the run must produce). Every trigger event goes through the same pipeline, whatever its source.

&#91;embedded content: triggered run pipeline · 8 stages, a retry loop and an approval loop\]

Invalid output loops back to the agent with the validation errors, up to the template's retry limit. Timeouts, cancellations and denied approvals skip delivery and go straight to a final state.

### Trigger sources

A trigger source is anything that emits `TriggerEvent`s. Built-in sources ship with the harness; plugins add more with `AddTriggerSource<T>`. Webhook-based sources register their own HTTP routes through the context, so a plugin can add a new chat channel without touching the server project.

```csharp
public interface ITriggerSource
{
    string Type { get; }
    Task StartAsync(TriggerSourceContext ctx, CancellationToken ct);  // ctx.Emit(evt), ctx.MapWebhook(path, handler)
    Task StopAsync(CancellationToken ct);
}

public sealed record TriggerEvent(
    string EventId,                        // provider message id, used for de-duplication
    string TriggerId,
    DateTimeOffset ReceivedAt,
    string? Sender,
    string? Text,
    IReadOnlyList<EventAttachment> Attachments,
    JsonObject Data,                       // raw payload, available to templates
    ReplyAddress? ReplyTo);                // where the `reply` sink and in-chat approvals go
```

| Source | Mechanism | Notes |
| --- | --- | --- |
| `schedule` | Cron (with time zone), interval or one-shot `at` | Missed runs during downtime: `skip`, `runOnce` or `catchUp` |
| `manual` | `harness trigger fire <id>` or the PWA | Accepts ad-hoc inputs |
| `webhook` | `POST /hooks/<id>` with HMAC signature | Generic integration point (CI, GitHub, home automation) |
| `file-watch` | `FileSystemWatcher` with debounce | Inbox folders, drop-a-file workflows |
| `run-completed` | Another run finished | Chains runs without a workflow engine |
| `signal` | `signal-cli` in JSON-RPC daemon mode | Signal has no official bot API; use a dedicated number |
| `whatsapp` | WhatsApp Business Cloud API webhook + Graph send API | Verify `X-Hub-Signature-256`; free-form replies only inside the 24-hour customer-service window |
| `messenger` | Messenger Platform webhook + Send API | Same signature scheme; same 24-hour messaging window |

The three chat channels need a public HTTPS endpoint. Serve `/hooks/*` on a separate listener from the API and expose only that listener, through a tunnel such as Cloudflare Tunnel or Tailscale Funnel.

### Trigger definitions

```yaml
# triggers/whatsapp-assistant.yaml
id: whatsapp-assistant
source:
  type: whatsapp
  phoneNumberId: "<id>"
  appSecret: secret:whatsapp-app-secret
  verifyToken: secret:whatsapp-verify-token
filter:
  senders: ["+31612345678"]          # allowlist; everything else is dropped and logged
coalesce: 5s                          # merge messages sent in quick succession into one run
session: "whatsapp:{event.sender}"    # keep one conversation per contact
template: assistant-scratch
concurrency: { perSession: 1, global: 2, onBusy: queue }
approvals: { route: reply, timeout: 10m, onTimeout: deny }
output: { sinks: [reply] }
```

```yaml
# triggers/nightly-deps.yaml
id: nightly-deps
source: { type: schedule, cron: "0 3 * * 1-5", timeZone: Europe/Amsterdam, missed: skip }
template: dependency-update
inputs: { repo: "git@github.com:contoso/api.git" }
output:
  sinks:
    - { type: file, path: "~/reports/deps-{date}.md", from: output.summary }
    - { type: webhook, url: secret:teams-webhook }
```

Events are written to a durable queue (a SQLite table) before anything else happens, then de-duplicated by `EventId`. WhatsApp and Messenger retry webhooks they consider undelivered, and a daemon restart must not lose a message that arrived during a long run.

### Run templates

A run template is a folder. It declares how to build the run's workspace, which agent and sandbox to use, the prompt, and the output contract.

```
templates/dependency-update/
├── template.yaml
├── files/            # copied into the workspace; *.tmpl files rendered with event and input variables
├── AGENTS.md         # copied to the workspace root, so it becomes the folder prompt layer
├── setup.sh          # runs inside the sandbox before the agent starts
└── schemas/report.schema.json
```

```yaml
# template.yaml
name: dependency-update
agent: coder
sandbox: dotnet-ci
workspace:
  steps:
    - git: { url: "{inputs.repo}", ref: main, depth: 1, into: repo }
    - copy: { from: files/, into: . }
    - run: ./setup.sh                 # non-zero exit aborts the run before any model call
  keep: onFailure                     # always | onFailure | never
prompt: |
  Update outdated NuGet packages in repo/. Run the tests. Do not change public APIs.
output:
  kind: json                          # text | json | files | reply
  schema: schemas/report.schema.json
  files: ["report.md"]
  retries: 2
limits: { maxRunMinutes: 45, maxTokens: 2000000 }
```

Each run gets a fresh directory under `~/.harness/runs/<run-id>/workspace`, built by the steps in order and then mounted into the sandbox. Steps are themselves pluggable (`IWorkspaceStep`), so a plugin can add, for example, a step that restores a database snapshot.

### Output contracts

Every triggered run gets one extra tool, `submit_output`, whose parameter schema is the template's output schema. The run succeeds only when the agent calls it with data that validates and every declared file exists. This works on both Azure OpenAI and Ollama, because it relies only on ordinary tool calling, not on provider-specific structured-output features.

If the agent stops without submitting, or submits invalid data, the harness replies with the validation errors as a user message and lets it try again, up to `retries`. The final run result has one of six states: `succeeded`, `invalid_output`, `failed`, `timed_out`, `cancelled` or `rejected` (an approval was denied).

Sinks deliver the result. Built-ins are `reply` (back to the originating channel), `file`, `webhook` and `run` (start another trigger). Plugins add more with `AddOutputSink<T>`, for example email, Slack or opening a pull request.

```csharp
public interface IOutputSink
{
    string Type { get; }
    Task DeliverAsync(RunResult result, SinkOptions options, CancellationToken ct);
}
```

## Run orchestration and monitoring

One `RunOrchestrator` executes every run, interactive or triggered, and publishes every step as an event. Monitoring is then just a matter of subscribing to that event stream, from the CLI, the PWA or anything else.

### Orchestrator

- **Queues with limits.** Runs wait in a queue with global, per-trigger, per-session and per-model-profile limits. A local Ollama model on one GPU typically gets a limit of 1, while Azure OpenAI can run several in parallel.
- **Explicit states.** `queued → preparing → running ⇄ awaiting_approval → validating → delivering →` one of the six final states from the output contract. Every transition is an event.
- **Durable approvals.** When Agent Framework returns `FunctionApprovalRequestContent`, the orchestrator persists the request, emits `APPROVAL_REQUESTED` and parks the run. Whichever client answers first wins; the orchestrator then resumes with the approval response on the same session. Because the session is on disk, a pending approval survives a daemon restart.
- **Cancellation everywhere.** One `CancellationToken` per run reaches the model call, every tool and the sandbox; cancelling also kills the sandbox's process tree.
- **Usage accounting.** The model-call hook records token usage per request and adds it to the run and session totals, which limits like `maxTokens` are enforced against.

### Event stream

Events share one envelope: `{ seq, ts, runId, sessionId, type, data }`. Use the AG-UI event names where they exist, so an AG-UI adapter for third-party front ends is a thin mapping later.

| Event type | Persisted | Carries |
| --- | --- | --- |
| `RUN_STARTED`, `RUN_STATE`, `RUN_FINISHED`, `RUN_ERROR` | Yes | State, trigger, agent, timings, final result |
| `TEXT_MESSAGE_START` / `_END` | Yes | Complete assistant message |
| `TEXT_MESSAGE_CONTENT` | No (live only) | Token deltas for streaming UIs |
| `TOOL_CALL_START` / `_END`, `TOOL_CALL_RESULT` | Yes | Tool name, arguments, truncated result |
| `APPROVAL_REQUESTED` / `APPROVAL_RESOLVED` | Yes | Tool call, decision, who decided, via which client |
| `USAGE` | Yes | Input/output tokens per model call |

Persisted events go to the session's `events.jsonl` and to an in-memory hub. Clients subscribe over Server-Sent Events with a `Last-Event-ID`: the server replays from the journal, then switches to live. A dropped phone connection therefore resumes exactly where it stopped.

### CLI monitoring

| Command | Does |
| --- | --- |
| `harness runs watch` | Live table of active and recent runs (state, trigger, agent, elapsed, tokens, last tool) |
| `harness runs attach <id>` | Live transcript of one run; answer approvals inline; Ctrl-C offers detach or cancel |
| `harness runs ls --state failed --since 24h` | Filtered history |
| `harness runs show/logs/output <id>` | Summary, full event log, final output and artifacts |
| `harness runs cancel/retry <id>` | Control |
| `harness triggers ls/next/fire/enable/disable` | Trigger status, next scheduled fire times, manual fire |

In a terminal, these commands open the matching views of the [interactive terminal UI](#mh8b50vefkz.50050); piped or with `--plain`, they print plain text instead.

Example `harness runs watch` view:

```
RUN      TRIGGER             AGENT     STATE              ELAPSED  TOKENS   LAST TOOL
r_8f2c   nightly-deps        coder     running            12m04s   184k     shell: dotnet test
r_8f31   whatsapp-assistant  helper    awaiting_approval   0m41s     9k     shell: git push
r_8f1a   manual              coder     succeeded          31m10s   402k     submit_output
```

Agent Framework already emits OpenTelemetry traces. Export them to a local file by default, or to any OTLP endpoint for teams that run Grafana or Aspire's dashboard.

## Host API: daemon, CLI and web service

The daemon serves one API on three listeners, and the CLI, the PWA and any script are all clients of it. Commands are plain REST calls; everything live comes back over Server-Sent Events, which are plain HTTP, resumable with `Last-Event-ID`, proxy-friendly and built into ASP.NET Core.

| Listener | Default binding | Who uses it | Authentication |
| --- | --- | --- | --- |
| Local socket | `~/.harness/harness.sock` (named pipe on Windows) | CLI on the same machine | OS file permissions (mode 0600) |
| API | `https://127.0.0.1:7443`; opt in to LAN or tailnet binding | PWA, remote CLI, scripts | Passkey/password session cookie, or scoped bearer token |
| Webhooks | `http://127.0.0.1:7444`, exposed only via a tunnel | WhatsApp, Messenger, generic webhooks | Per-source signature check |

### Endpoints

| Method and path | Purpose |
| --- | --- |
| `POST /api/sessions` | Create a session (agent, workspace, optional title) |
| `GET /api/sessions`, `GET /api/sessions/{id}` | List and inspect sessions |
| `GET /api/sessions?q=<text>` | Full-text search over session titles and messages |
| `POST /api/sessions/{id}/messages` | Send a user message; returns the new `runId` |
| `GET /api/sessions/{id}/messages?before=<seq>&limit=<n>` | Paged transcript reads, so clients load long sessions as the user scrolls |
| `POST /api/sessions/{id}/fork` | Fork at a message sequence number |
| `GET /api/runs`, `GET /api/runs/{id}` | List and inspect runs |
| `GET /api/runs/{id}/events` | SSE stream for one run (replay, then live) |
| `GET /api/events` | SSE firehose of all runs, filterable; drives the TUI, `runs watch` and the PWA dashboard |
| `POST /api/runs/{id}/approvals/{requestId}` | Approve or deny a pending tool call |
| `POST /api/runs/{id}/cancel` | Cancel a run |
| `GET /api/triggers`, `POST /api/triggers/{id}/fire` | Trigger status and manual fire |
| `PATCH /api/triggers/{id}` | Enable or disable |
| `GET /api/agents`, `GET /api/templates` | Read-only catalogue for pickers in the clients |
| `GET /api/workspaces`, `GET /api/workspaces/{name}/files?q=<text>` | Named workspaces; fuzzy file-path completion for @ mentions |
| `POST /api/push/subscriptions` | Register a Web Push subscription for the PWA |

Publish an OpenAPI document from the server (built into ASP.NET Core 10) and keep `Harness.Client` in sync with it, so the CLI and tests never drift from the server.

### Workspaces from a remote client

`harness chat` run locally uses the current directory as the workspace. A remote client cannot do that, because the path would not exist on the daemon's machine. Remote clients therefore pick from **named workspaces** registered on the daemon (`harness workspace add api ~/src/api`), and the PWA shows that list.

### Remote CLI login

`harness login https://host:7443` starts a device-pairing flow: the CLI shows a short code, you approve it in the PWA (already signed in with a passkey), and the CLI receives a scoped token stored in the OS keychain. Scopes are `read`, `run`, `approve` and `admin`; scripts and CI get tokens with only what they need.

### Optional protocol adapters

Two adapters are cheap once the core API exists. An AG-UI endpoint (`Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`) lets third-party front ends talk to an agent. An MCP server endpoint (`ModelContextProtocol.AspNetCore`) exposes `start_run`, `get_run` and `list_triggers` as tools, so other agents can delegate work to this harness.

## Interactive terminal UI

Running `harness` with no arguments opens a full-screen, keyboard-first terminal UI for browsing, searching and working in sessions. It is a pure client of the daemon API, so it behaves the same over the local socket and over HTTPS from another machine, and every action in it also exists as a scriptable subcommand.

### Framework

Build it on [Terminal.Gui v2](https://www.nuget.org/packages/Terminal.Gui), whose 2.5.0 stable release (11 Sep 2026) targets .NET 10. It provides lists, tables, trees, text editing, key bindings mapped to named commands, themes and TrueColor on Windows, macOS and Linux. Spectre.Console stays for plain, non-interactive output; it renders well but has no full-screen focus and navigation model.

Markdig parses assistant Markdown into styled text. The `Terminal.Gui.Editor` package supplies the composer (undo/redo, search) and optional TextMate highlighting for code blocks. [OpenMonoAgent.ai](https://github.com/StartupHakk/OpenMonoAgent.ai), an open-source .NET terminal coding agent built on Terminal.Gui, is worth reading for patterns, as inspiration only.

### Layout

Three regions: a sidebar (sessions above, live runs below), the focused session, and a one-line status bar. The sidebar collapses on narrow terminals, and `Ctrl+B` toggles it.

```
┌ Sessions ────────────────┐┌ api · coder · azure ──────────────────── ● running 02:14 ┐
│ / filter                 ││ you  Add rate limiting to the orders endpoint            │
│ ▾ api  ~/src/api         ││                                                          │
│ ● Rate limiting       2m ││ ▸ grep MapPost src/ · 3 files                         ✓  │
│   Fix flaky test      1h ││ ▸ read src/Orders/Endpoints.cs 1-180                  ✓  │
│   ↳ fork at #42       1h ││ ▾ edit src/Orders/Endpoints.cs                        ✓  │
│ ▾ web  ~/src/web         ││     - app.MapPost("/orders", Create);                    │
│   Upgrade to Vite     3d ││     + app.MapPost("/orders", Create)                     │
│ ▾ Triggered              ││     +     .RequireRateLimiting("orders");                │
│ ! whatsapp-assistant now ││ ! shell  dotnet test --filter Orders   [a]pprove [d]eny  │
│ ✓ nightly-deps        9h ││                                                          │
├ Runs ────────────────────┤├──────────────────────────────────────────────────────────┤
│ r_9a10  running   2m  61k││ > Also add a test for the 429 response_                  │
│ r_9a0e  approval  0m   9k││   @ file  / command  Ctrl+G editor  Enter send           │
└──────────────────────────┘└──────────────────────────────────────────────────────────┘
 ctx 38% · 61k tokens · 2 approvals waiting (g a) · ? help · Ctrl+K commands
```

### Views

| View | Open with | Shows |
| --- | --- | --- |
| Sessions | `harness`, `g s` | Tree grouped by named workspace, plus a Triggered group; fuzzy filter; running and approval badges; fork lineage |
| Session | `Enter` on a session, `harness chat` | Transcript, composer, session status (agent, model, context use, tokens) |
| Runs | `g r`, `harness runs watch` | Live table of active and recent runs across all sessions and triggers |
| Run detail | `Enter` on a run, `harness runs attach <id>` | Event timeline, tool-call inspector, output and artifacts |
| Approvals inbox | `g a` | Every pending approval across sessions, oldest first, each with the full command or diff |
| Triggers | `g t` | Enabled state, next fire time, last result; fire with inputs |
| Command palette | `Ctrl+K` or `:` | Every action by name, fuzzy-matched, with its current key binding |

`harness chat --workspace <name> --resume <id>` opens straight into a session. `runs watch` and `runs attach` open their views in a terminal and print line-oriented output when piped.

### Keyboard map

Arrow keys and `Enter` always work; Vim-style keys also work wherever a list or the transcript has focus. Every binding is a named command, so `~/.harness/tui.yaml` can remap any key, and the palette and the `?` overlay always show the current binding.

| Keys | Action | Where |
| --- | --- | --- |
| `Ctrl+K` or `:` | Command palette | Everywhere |
| `?` | Bindings for the focused view | Everywhere |
| `g s` · `g r` · `g a` · `g t` | Go to sessions, runs, approvals inbox, triggers | Everywhere |
| `Tab` / `Shift+Tab` | Move focus between sidebar, transcript and composer | Everywhere |
| `Esc` | Back, or close the overlay | Everywhere |
| `Ctrl+B` | Toggle the sidebar | Everywhere |
| `Ctrl+C` | Cancel the running turn; press again within 2 seconds to quit | Everywhere |
| `↑ ↓` or `j k`, `gg` / `G` | Move; jump to top or bottom | Lists, transcript |
| `/`, then `n` / `N` | Filter the list, or search the transcript and step through matches | Lists, transcript |
| `n` · `r` · `f` · `R` · `e` · `d` | New, resume, fork, rename, export, delete (with confirmation) | Session list |
| `[` / `]` | Previous or next user turn | Transcript |
| `Space` or `o` / `O` | Expand or collapse the selected tool call / all tool calls | Transcript |
| `y` | Copy the selected message (OSC 52, so it works over SSH) | Transcript |
| `f` | Fork the session at the selected message | Transcript |
| `i` | Focus the composer | Transcript |
| `a` · `d` · `A` | Approve; deny with an optional reason; approve and allow this exact command for the rest of the session | Approval card, approvals inbox |
| `Enter` / `Alt+Enter` | Send / new line | Composer |
| `↑` on an empty composer | Recall the previous message | Composer |
| `Ctrl+G` | Edit the message in `$EDITOR` | Composer |

### Composer

- **Enter sends, Alt+Enter adds a line.** Many terminals cannot tell Shift+Enter from Enter, so Alt+Enter is the reliable newline key.
- **`@` completes files.** It fuzzy-matches paths in the session's workspace; picked files are attached as references the agent can `read`, not pasted inline.
- **`/` opens slash commands** at the start of a message: `/model`, `/agent`, `/new`, `/fork`, `/compact`, `/approvals` (session-level policy, such as auto-approving edits), `/export` and `/usage`.
- **Large pastes become an attachment chip** instead of flooding the composer.
- **Messages typed during a run are queued** and sent when the turn ends.

### Transcript

- **A message cursor.** `j`/`k` moves a highlight between messages, and copy, fork and expand act on the highlighted one.
- **Tool calls fold to one line**: tool, main argument, result summary, status. Expanded, `edit` shows a coloured unified diff, `shell` shows the command, exit code and output tail, and other tools show arguments and the truncated result.
- **Streaming without jumping.** Output follows the bottom unless you have scrolled up; then a "↓ new output" marker appears instead.
- **Approvals inline.** A pending approval appears as a highlighted card showing exactly what will run (the full command or diff) with its keys. Approvals in other sessions show in the status bar and the inbox.
- **Fast on long sessions.** Only visible messages are rendered, and older pages load from the daemon as you scroll up.

### Live data and state

The TUI holds one SSE subscription to `/api/events` and one in-memory store. API events and key commands both become actions, a reducer updates the store, and views render from it. Badges, the runs list, the approval count and the open transcript therefore stay in sync with work started anywhere: the PWA, a trigger or another terminal. State logic is tested by replaying recorded event streams into the store, with no terminal involved.

The TUI needs three API additions, listed in the endpoints table: paged transcript reads, full-text session search, and workspace file completion for `@`.

### Fallbacks and accessibility

- `--plain`, or any non-TTY output, switches to line mode with Spectre.Console: a streamed transcript and `y/n` approval prompts. Plain mode is also the path for screen-reader users.
- Every TUI action also exists as a subcommand with `--json`, so scripts never drive the TUI.
- `NO_COLOR` is respected; 16-colour, 256-colour and TrueColor terminals are detected; light and dark themes ship by default. Mouse clicks and wheel scrolling work but are never required.

## PWA and authentication

Use ASP.NET Core Identity on SQLite with its [built-in passkey support](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/passkeys) (.NET 10 and later), plus Identity's normal password sign-in. The PWA is a small TypeScript app embedded in the binary and served by the daemon from `/`.

### Authentication design

- **Two ways in, one stronger than the other.** Users can sign in with a passkey or a password. A password-only session can view runs and chat; approving tool calls, firing triggers and admin actions require a fresh passkey assertion (step-up). This keeps the password as a recovery path without letting it approve a `git push` from a stolen login.
- **Bootstrap from the machine.** On first start, `harness serve` prints a one-time setup URL. The first user sets a password and registers a passkey; the setup endpoint then disables itself. `harness admin reset-password` and `harness admin passkeys clear` work only over the local socket, so physical or SSH access to the machine is the root of trust.
- **Identity schema version 3.** Passkey storage requires opting Identity into schema version 3 and running its migration; the daemon applies migrations on start.
- **Cookies and tokens.** The PWA uses a `Secure`, `HttpOnly`, `SameSite=Strict` cookie with sliding expiry. The CLI and scripts use scoped bearer tokens from the pairing flow. Identity lockout and ASP.NET Core rate limiting protect the password endpoint.
- **Roles.** `owner`, `operator` (run, approve) and `viewer`. Most installs have exactly one owner.

### Hostname and TLS

Passkeys need a secure context and a stable domain as the relying-party ID, set through `IdentityPasskeyOptions.ServerDomain`. A bare IP address or a changing hostname will not work, and a credential registered for one domain cannot be used on another.

The simplest robust setup is Tailscale: the machine gets a stable `*.ts.net` name with a real certificate, and the phone reaches it only over the tailnet. The alternative is a Cloudflare Tunnel to your own domain, which exposes the login page to the internet and so leans much harder on the auth design above.

### PWA

The manifest declares `display: standalone`, icons and a start URL, and a service worker caches the app shell, so Android Chrome offers "Install app". API responses are never cached by the service worker.

| Screen | Contents |
| --- | --- |
| Dashboard | Active runs, pending approvals (badge count), recent failures |
| Run | Live transcript over SSE, tool calls folded by default, approve/deny, cancel, output and artifacts |
| Chat | Sessions per named workspace; send messages to an agent; resume or fork |
| Triggers | Enable/disable, next fire times, fire with inputs |
| Settings | Passkeys, password, paired CLIs and tokens, push notifications |

Web Push (VAPID keys generated at setup) notifies the phone when a run needs approval or finishes. A notification opens the approval screen rather than approving directly, because the service worker cannot perform the passkey step-up that an approval requires.

## Packaging, installation and configuration

Ship one self-contained, single-file `harness` executable per platform, built for `linux-x64`, `linux-arm64`, `osx-arm64` and `win-x64`. Users install it by copying one file and running `harness install`.

```bash
dotnet publish src/Harness -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true   # bundles SQLite's native library
```

- **No trimming, no Native AOT.** Plugin loading, EF Core and `AIFunctionFactory` all rely on reflection and runtime code loading. Accept the larger binary.
- **PWA built into the binary.** An MSBuild target runs the Vite build before compile and embeds `web/pwa/dist` as resources served through `ManifestEmbeddedFileProvider`.
- **Service install.** `harness install` writes a systemd user unit on Linux (and enables lingering so it runs without a login), a launchd agent on macOS, and a Windows service through `UseWindowsService`. `harness uninstall` reverses it.
- **Updates.** `harness update` downloads the release for the current platform, verifies its signature, swaps the binary and restarts the service.

### Home directory

Everything lives under `~/.harness` (override with `HARNESS_HOME`), so the declarative parts can be a git repository and the state parts can be backed up or excluded separately.

```
~/.harness/
├── config.yaml        # model profiles, listeners, defaults, per-plugin settings
├── AGENTS.md          # user-global prompt layer
├── mcp.json           # global MCP servers
├── sandboxes.yaml     # sandbox profiles
├── agents/            # agent definitions
├── prompts/           # agent instruction files
├── skills/            # global skills
├── triggers/          # trigger definitions
├── templates/         # run templates
├── plugins/           # installed plugins, one folder each
├── sessions/          # state: conversations
├── runs/              # state: triggered-run workspaces and artifacts
├── logs/              # state: daemon logs and OpenTelemetry files
├── harness.db         # state: SQLite index, identity store, trigger queue
└── harness.sock       # local CLI socket
```

### Configuration rules

- **Secrets never in YAML.** `harness secrets set <name>` stores a value in the OS credential store (DPAPI on Windows, Keychain on macOS, libsecret on Linux, or an encrypted file on headless servers). Config refers to it as `secret:<name>`, or to an environment variable as `env:<NAME>`.
- **Schemas for every file.** Publish JSON Schemas for `config.yaml`, agents, triggers, templates and sandboxes. `harness config validate` checks them, and a `# yaml-language-server: $schema=` header gives autocomplete in editors.
- **Hot reload where safe.** Agents, prompts, skills, triggers and templates reload on change and apply to the next run. Listeners, plugins and the auth configuration require a restart, which `harness restart` performs gracefully after active runs reach a safe point.

## Security model

Assume every model output can be steered by whoever wrote the content the agent reads: a chat message, a web page, a file in a cloned repo, an MCP result. The design therefore never relies on the model behaving; it relies on what the run is able to reach.

For each trigger, break at least one leg of the risky combination of private data, untrusted input and a way to send data out. A WhatsApp assistant that reads untrusted messages should run with `network: none` or a tight allowlist, and its `reply` sink can only answer the original sender.

| Threat | Mitigation |
| --- | --- |
| Prompt injection drives harmful tool use | Sandbox for all execution; network `none` or allowlist; approvals on `shell`, MCP and skill scripts; secrets kept in the daemon |
| Unknown sender triggers a run | Sender allowlist per trigger; webhook signature checks; per-sender rate limit; unknown senders dropped and logged |
| Malicious repository config | Folder config that adds MCP servers, plugins or skill scripts, or loosens approvals, is ignored until `harness trust` |
| Malicious or buggy plugin | Plugins are fully trusted, so install only from explicit paths or a configured feed; record and verify each plugin's hash; prefer MCP for third-party code |
| Secrets leak into transcripts | Secrets never enter the sandbox unless a profile maps one explicitly; a built-in hook masks known secret values in tool results and events |
| Stolen phone or password | Passkey step-up for approvals and admin; revoke sessions, devices and tokens from the CLI over the local socket |
| Exposed API | Bound to localhost by default; tailnet or tunnel for remote access; same-origin only, no CORS |
| Exposed webhook listener | Separate port serving only `/hooks/*`; body size limits; signature and timestamp checks |
| Runaway loops and cost | `maxToolIterations`, `maxTokens` and wall-clock limits per run; daily token budget per trigger |
| Sandbox escape | Defence in depth: rootless bubblewrap or containers, no container-runtime socket in the sandbox, no secrets inside to steal |

## Implementation roadmap

Build in six phases, each closed by a gate that proves the phase works before the next one depends on it. The order puts the agent loop first and the public-facing parts (PWA, chat channels) last, so nothing is exposed before sandboxing and approvals exist.

&#91;embedded content: implementation roadmap · 6 phases, each closed by a gate\]

Phases 1–3 give a usable local coding agent; Phase 4 makes it autonomous; Phases 5–6 make it reachable from the phone and chat apps.

## Open questions and decisions

These choices change the design in specific places; each lists the assumption this document makes until it is decided.

- [ ] **Passkey and password: either, or both?** Assumed: either signs in, and a passkey is required as step-up for approvals and admin. If you meant both factors on every sign-in, Identity's two-factor flow covers it.
- [ ] **Primary host OS.** Assumed Linux, which makes bubblewrap the default sandbox. On Windows or macOS the container provider becomes the default and Docker or Podman becomes a prerequisite.
- [ ] **Single user or a few.** Assumed one owner, with roles in place for later.
- [ ] **WhatsApp access route.** The official Cloud API needs a Meta business account, a dedicated number and template messages outside the 24-hour window. Unofficial bridges avoid that but risk the account being banned.
- [ ] **Signal number.** A dedicated number registered in signal-cli, or signal-cli linked as a secondary device of your own number (which lets the agent see all your chats).
- [ ] **Network exposure.** Tailscale-only for the API is assumed. WhatsApp and Messenger still need a public webhook URL, through Tailscale Funnel or a Cloudflare Tunnel on the webhook port only.
- [ ] **Build or adopt.** Review [dotcraft](https://github.com/DotHarness/dotcraft) and [openclaw.net](https://github.com/clawdotnet/openclaw.net) before Phase 1 for patterns to borrow in Phases 1–3; use them as inspiration only, not as dependencies.
- [ ] **Plugin distribution.** Folder installs are assumed; a private NuGet feed would allow `harness plugin add <id>` with versioning.
- [ ] **Local model choice.** Tool-calling reliability varies a lot between Ollama models; pick and benchmark one against the built-in tools early in Phase 1.
- [ ] **"Tools and books" in plugins.** Read as "tools and hooks"; if you meant something else, such as bundled documentation, the plugin builder needs one more registration method.
