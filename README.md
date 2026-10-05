# harness

A self-contained .NET 10 coding agent harness. One executable, `harness`, runs as a daemon and as its own CLI client.
[Microsoft Agent Framework](https://github.com/microsoft/agent-framework) owns the agent loop; the harness owns everything
around it: tools, sandboxing, sessions, approvals, triggers, plugins and the daemon API.

The daemon is the only process that talks to models, runs tools or touches sessions. The CLI (and later the TUI and PWA)
are clients of the same API, so a run started by a trigger can be watched and approved from any terminal.

The design this follows is [docs/DESIGN.md](docs/DESIGN.md). See [docs/STATUS.md](docs/STATUS.md) for what is built so far, where the implementation deviates from the design, and
what comes next.

## Build and run

Requires the .NET 10 SDK. On Linux, install `bubblewrap` for the sandbox and `ripgrep` for fast `grep`.

```bash
dotnet build Harness.slnx
dotnet test Harness.slnx

# Single-file executable (no trimming, no AOT: plugins need AssemblyLoadContext and JIT)
dotnet publish src/Harness -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
```

## Quick start

```bash
harness init                 # creates ~/.harness with a sample config, agent, prompt and sandbox profiles
$EDITOR ~/.harness/config.yaml   # point the 'local' profile at your Ollama model, or add an Azure OpenAI profile
harness models doctor        # checks every profile; warns when an Ollama context window is too small
harness serve                # the daemon, listening on ~/.harness/harness.sock (mode 0600)

cd ~/src/my-repo
harness chat                 # line-mode chat; this directory is the workspace
harness chat "add a test for the 429 response"    # one-shot
```

Approvals show up inline (`y` / `n` / `a` = always for this exact call in this session). From another terminal:

```bash
harness approvals            # everything waiting, across sessions
harness approve <run> <request>
harness runs watch           # live table of runs
harness runs attach <run>    # follow a run and answer its approvals
harness sessions ls -q "rate limiting"
harness sessions fork <id> --at 42
```

## Run it from your machine, use it from your phone

The daemon serves the web app, so the machine running `harness serve` is the host. Every push to `main` becomes a signed
GitHub release, and `harness install --auto-update` keeps a machine on the latest one (`harness update`, hourly,
restarting the daemon only when no run is active). Setup, Tailscale for phone access, and signing keys:
[docs/HOSTING.md](docs/HOSTING.md).

## Layout

| Project | Role |
| --- | --- |
| `Harness.Sdk` | Plugin contracts: `IHarnessPlugin`, `IHarnessHook`, `ISandboxProvider`, `ITriggerSource`, `IOutputSink`, the event envelope |
| `Harness.Core` | Config catalog (hot reload), secrets, trust, model providers (Chat Completions only), sessions, prompt layers, agent factory and middleware |
| `Harness.Tools` | The seven built-in tools: `read`, `list`, `glob`, `grep`, `edit`, `write`, `shell` |
| `Harness.Sandbox` | `none`, `bubblewrap` and `container` sandbox providers |
| `Harness.Extensions` | MCP client manager, skills (sandboxed script runner), plugin loader, command hooks |
| `Harness.Runs` | Run orchestrator: queues, states, approvals, cancellation, limits, event hub |
| `Harness.Triggers` | Trigger engine, durable event queue, `schedule` / `webhook` / `file-watch` / `manual` sources |
| `Harness.Server` | ASP.NET Core daemon: REST + SSE over the local socket, an optional API listener and a webhook listener |
| `Harness.Client` | Typed API client and wire contracts |
| `Harness.Cli` | `System.CommandLine` commands with Spectre.Console output |
| `Harness` | Entry point; publishes the single `harness` executable |

## Configuration

Everything declarative is a file under `~/.harness` (override with `HARNESS_HOME`), so it can live in git:

```
~/.harness/
├── config.yaml        # model profiles, listeners, defaults, command hooks, plugin settings
├── AGENTS.md          # user-global prompt layer
├── mcp.json           # MCP servers ("mcpServers" shape)
├── sandboxes.yaml     # sandbox profiles
├── workspaces.yaml    # named workspaces (harness workspace add)
├── agents/  prompts/  skills/  triggers/  plugins/
├── sessions/          # state: one folder per session, append-only JSONL
├── runs/              # state: scratch workspaces for triggered runs
└── harness.db         # state: SQLite index (rebuildable), trigger queue
```

An agent definition (`agents/coder.yaml`):

```yaml
name: coder
model: local
instructions: prompts/coder.md
tools:
  builtin: [read, list, glob, grep, edit, write, shell]
  mcp: [github]
skills: [skills/]
approvals: { shell: ask, write: allow, mcp: ask }   # ask | allow | deny | allowlist
allowlist: { shell: ["git status*", "dotnet test*"] }
sandbox: workspace
compaction: { toolResultsAfter: 40, slidingWindowTurns: 30 }
limits: { maxToolIterations: 60, maxRunMinutes: 30 }
```

Secrets never go in YAML: `harness secrets set <name>`, then refer to `secret:<name>` (or `env:<NAME>`).
A repository's own `.harness/config.yaml` and `.harness/mcp.json` can only tighten policy until you run `harness trust <path>`.
