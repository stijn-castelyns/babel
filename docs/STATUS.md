# Implementation status

This tracks the build against *Custom Coding Agent Harness — Architecture & Design* (Oct 2026).
The roadmap there has six phases; phases 1–3 give a usable local coding agent, phase 4 makes it autonomous,
phases 5–6 make it reachable from a phone and chat apps.

## Built

**Agent core (phase 1)**
- One `AgentFactory` builds a `ChatClientAgent` per run from an agent definition and a model profile.
- Model profiles for `ollama` (OllamaSharp), `azure-openai` (`GetChatClient(...).AsIChatClient()`, Entra or API key) and
  `openai` (any OpenAI-compatible chat-completions endpoint). A guard rejects any client backed by the Responses API.
- `harness models doctor` checks every profile and warns when an Ollama `num_ctx` is below 32k.
- Middleware: model-call hooks and token accounting (`HookingChatClient`), compaction (`CompactingChatClient`),
  tool-call hooks, limits, secret masking, spill files and lazy folder prompts (`ToolCallMiddleware`).
- Prompt layers 1–4, 6 and 7 are composed per invocation by an `AIContextProvider`; layer 5 (subfolder `AGENTS.md`)
  is appended to the first `read`/`edit`/`list`/`write` result inside that folder, once per session.

**Built-in tools (phase 1)**: `read`, `list`, `glob`, `grep` (ripgrep when present), `edit`, `write`, `shell`
(background jobs with handles). Plain-text results with a continuation header; read-before-write by content hash;
one path policy that canonicalises symlinks; oversized output spilled to `.harness/spill/`.

**Sessions (phase 1)**: a folder per session with `session.json`, append-only `history.jsonl` and `events.jsonl`, a
single-writer lease, torn-line tolerance, synthetic results for orphaned tool calls, fork at any sequence number,
Markdown/JSON export, and a rebuildable SQLite index with FTS5 search.

**Approvals and sandboxing (phase 2)**: `ask | allow | deny | allowlist` per tool, MCP server or skill scripts; inline,
cross-client approvals with "always for this session"; unattended runs treat `ask` as `deny`. Approvals are durable:
a run waiting on a person is parked in SQLite with its whole approval batch (and any answers already given) plus its
agent session state. When the daemon restarts, the approvals are pending again, approval timeouts keep counting from the
original request, and the run resumes on its session when the last one is answered (or can be cancelled). A parked run
is unparked before it resumes, so a crash mid-resume can never run an approved tool twice. Sandbox providers
`none`, `bubblewrap` (namespaces unshared, read-only system dirs, private `/tmp` and home) and `container`
(Docker/Podman with CPU, memory and PID limits). Triggered runs refuse to start unsandboxed unless allowed.

**Extensions (phase 3)**: MCP (`mcp.json`, stdio servers spawned per run inside the sandbox, shared remote HTTP servers,
`mcp__<server>__<tool>` names, allow/deny lists); skills via `AgentSkillsProvider` with a script runner that executes in
the sandbox; C# plugins in collectible `AssemblyLoadContext`s with shared host assemblies, SDK-range and hash checks;
command hooks (JSON on stdin, exit 2 blocks); folder `.harness/` config gated by `harness trust`.

**Daemon, API and CLI (phases 1–4)**: REST + resumable SSE (`Last-Event-ID`) on a 0600 Unix socket; optional API
listener (bearer token) and a separate webhook listener that serves only `/hooks/*`. CLI: `chat`, `sessions`, `runs`
(`ls/show/logs/output/attach/watch/cancel`), `approvals/approve/deny`, `triggers`, `init`, `models`, `config validate`,
`trust`, `secrets`, `workspace`, `agents`, `plugin add/ls`, `install/uninstall` (systemd user unit), `status`, `serve`.

**Triggers (phase 4, partial)**: durable SQLite queue with de-duplication by event id, sender allowlists,
`OnTriggerFired` hooks, keyed sessions (`session: "whatsapp:{event.sender}"`), approval timeouts, enable/disable, manual
fire with inputs. Sources: `schedule` (cron with time zone, interval, one-shot; `skip`/`runOnce`/`catchUp`), `webhook`
(HMAC-SHA256), `file-watch`, `manual`, and plugin sources.

## Deviations from the design

| Design | Implementation | Why |
| --- | --- | --- |
| `Harness.Sdk` references only the two abstractions packages | It also references `Microsoft.Agents.AI` and the ASP.NET Core shared framework | `AgentSkill` (for `AddSkill`) lives in `Microsoft.Agents.AI`; `MapWebhook` hands out `HttpContext`. Both are shared from the host, so types still match across the plugin boundary. |
| `chat.AsBuilder().UseAIContextProviders(new CompactionProvider(...))` | `CompactingChatClient` runs the same strategies through `CompactionProvider.CompactAsync` | With the provider registered on the chat client, Agent Framework 1.23 stops passing request messages to the `ChatHistoryProvider` whenever a tool runs, so user messages were lost from history. Covered by `RunOrchestratorTests`. |
| SQLite through EF Core | `Microsoft.Data.Sqlite` directly | The index is a handful of tables; EF Core arrives with ASP.NET Core Identity in phase 5. |
| Secrets in the OS credential store | A 0600 `secrets.json` behind `SecretStore` | Keychain, DPAPI and libsecret slot in behind the same class. |
| Triggers reference a run template | Triggers name `agent`, `workspace` and `prompt` directly | Run templates, output contracts and sinks are the rest of phase 4. |
| `network: allowlist` through an egress proxy | Treated as `none` | Fails closed until the proxy exists. |
| Processes started from any thread | All child processes start from one dedicated thread (`ProcessSpawner`) | `bwrap --die-with-parent` uses `PR_SET_PDEATHSIG`, which fires when the forking *thread* exits; retiring thread-pool threads would kill sandboxed commands. |
| Parked runs keep their whole `RunRequest` | Everything except per-run extra tools is kept | Extra tools (the future `submit_output`) are rebuilt from the run template when templates land. |
| `harness` with no arguments opens the TUI | Opens line-mode chat in the current directory | The Terminal.Gui TUI is not built yet. |

## Not built yet

- **Phase 4:** run templates (workspace steps, `setup.sh`, `keep`), output contracts with `submit_output` and retries,
  output sinks (`reply`, `file`, `webhook`, `run`), `run-completed` source, coalescing, per-sender rate limits,
  daily token budgets, retention policies, compaction checkpoints (summaries in `checkpoints.jsonl`).
- **Egress proxy** for `network: allowlist`; `harness sandbox test <profile>`.
- **Terminal UI** (Terminal.Gui 2.5), with the views, keymap and composer from the design.
- **Phase 5:** ASP.NET Core Identity with passkeys and step-up, device-pairing login, scoped tokens, the PWA, Web Push.
- **Phase 6:** Signal, WhatsApp and Messenger sources with in-chat approvals.
- OpenTelemetry exporters (traces are emitted but not exported), JSON Schemas for config files,
  `harness update`, service install on macOS and Windows, the `dotnet new harness-plugin` template and sample plugins,
  MCP OAuth, AG-UI and MCP-server adapters.
