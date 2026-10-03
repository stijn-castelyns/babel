# Working on this repository

This is the `harness`: a self-hosted .NET 10 coding agent harness built on Microsoft Agent Framework 1.23.

- **The spec:** `docs/DESIGN.md` is the architecture and design document the implementation follows (verbatim copy of the
  original; three embedded diagrams did not survive the export and appear as `[embedded content: …]` placeholders).
- **Where things stand:** `docs/STATUS.md` lists what is built, every deliberate deviation from the design and why, and
  what is not built yet. Keep it current: update it in the same commit as the work.
- **Next up:** the rest of phase 4: coalescing, per-trigger concurrency limits, per-sender rate limits, daily token budgets,
  retention policies and compaction checkpoints; then the egress proxy for `network: allowlist`.

## Build and test

```bash
dotnet build Harness.slnx
dotnet test Harness.slnx        # ~50 tests, a few seconds; includes real bubblewrap and real-socket server tests
```

The build treats warnings as errors and uses central package versions (`Directory.Packages.props`).

In a fresh cloud container the .NET SDK is not preinstalled and `dot.net` downloads are blocked; install from Ubuntu:
`apt-get install -y dotnet-sdk-10.0 bubblewrap` (ripgrep is usually present). Bubblewrap tests skip themselves when
`bwrap` is missing.

For an end-to-end check without a real model, point a model profile at any OpenAI-compatible endpoint
(`provider: openai`, `endpoint: http://127.0.0.1:<port>/v1`); a tiny scripted chat-completions server that returns tool
calls is enough. Tests use `ScriptedChatClient` through `AgentFactory.ChatClientOverride` instead.

## Gotchas learned the hard way

- Do not register `CompactionProvider` on the chat client (`UseAIContextProviders`): Agent Framework then stops passing
  request messages to the `ChatHistoryProvider` whenever a tool runs, so user messages vanish from history.
  `CompactingChatClient` runs the strategies through `CompactionProvider.CompactAsync` instead.
- Approval responses are bound to requests kept in the `AgentSession` state, so the agent session must be saved when a
  run is parked, not only when it finishes.
- Start child processes through `ProcessSpawner`: `bwrap --die-with-parent` fires when the forking *thread* exits.
- Kestrel exposes no endpoint feature for Unix-socket connections; listeners are identified by tagging connections
  (`Listener.Tag`).
- Empty YAML sections deserialize to null; `Yaml.Parse` refills them with defaults. Unknown keys are errors on purpose.
- Live-only events (`TEXT_MESSAGE_CONTENT`) are never replayed; tests must assert on persisted events.
- The OpenAI adapter rewrites every tool's root schema and defaults `additionalProperties` to `false`; set it explicitly
  when a tool takes free-form objects (`submit_output` does).
- Templated runs always get the output contract: scripted test models must call `submit_output` or the run ends
  `invalid_output`.

## Conventions

- Layering: plugins reference only `Harness.Sdk`; `Harness.Core` holds the agent runtime; `Harness.Runs` wires
  everything; the CLI talks to the daemon only through `Harness.Client` (local file commands excepted).
- Tool results are plain text with a one-line header that tells the model how to continue.
- Commit messages describe the why; do not include model identifiers in anything pushed.
