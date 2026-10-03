# Implementation status

This tracks the build against *Custom Coding Agent Harness — Architecture & Design* (Oct 2026), kept in
[DESIGN.md](DESIGN.md).
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
(HMAC-SHA256), `file-watch`, `manual`, and plugin sources. A trigger references a run template with `template:`; its
`agent:` and `prompt:` override the template's.

**Run templates (phase 4)**: folders under `templates/` with a `template.yaml` (agent, sandbox, workspace steps, `keep`,
prompt, instructions, input defaults, output contract, limits), parsed strictly except for the free-form steps and sinks.
Every templated run gets a fresh `~/.harness/runs/<run-id>/workspace`, built in the `preparing` state before any model
call: `git` (clone on the host, `ref`/`depth`/`into`, commit ids checked out detached), `copy` (from the template folder;
`*.tmpl` files rendered with `{inputs.x}`, `{event.text}`, `{run.id}`, `{date}`… and written without the suffix; unknown
placeholders are left alone), `run` (in the run's sandbox, in the workspace root, with the template folder mounted
read-only at `/harness/template`; `./setup.sh` resolves to the template's copy), and plugin steps (`AddWorkspaceStep<T>`).
Without `steps:` the template copies `files/` and runs `setup.sh` when they exist. A failing step fails the run; each step
emits `WORKSPACE_STEP` events with its log. The template's `AGENTS.md` is copied to the workspace root (the folder prompt
layer) and its `instructions:` join prompt layer 6. The template's sandbox and limits win over folder config in the built
workspace. `keep: always | onFailure | never` (default `onFailure`) is applied when the run reaches its final state,
including runs cancelled or rejected while parked. Keyed sessions follow their latest run's workspace.

**Output contracts (phase 4)**: every templated run gets one extra tool, `submit_output`, whose parameter schema is the
template's output schema (`kind: json` with `schema:`; `text` and `reply` take `{ text }`, `files` takes `{ summary }`;
non-object schemas are wrapped as `{ output }`). Submissions are validated with JsonSchema.Net and every declared file
must exist; the tool answers with each problem by JSON pointer. A turn that ends without valid output moves the run to
`validating`, and the harness tells the agent what is wrong in a user message; each invalid submission or empty turn uses
one of `retries` (default 2), after which the run ends as `invalid_output`. The contract text joins prompt layer 6.
Accepted output is written to `runs/<run-id>/output/output.json` as soon as it validates (so a run parked on an approval
keeps it across a restart), declared files are copied to `runs/<run-id>/output/files/`, and both are stored on the run
record (`harness runs output`, `GET /api/runs/{id}`, `RUN_FINISHED`). Each check emits `OUTPUT_VALIDATED`. The six final
states are `succeeded`, `invalid_output`, `failed`, `timed_out`, `cancelled` and `rejected`.

## Deviations from the design

| Design | Implementation | Why |
| --- | --- | --- |
| `Harness.Sdk` references only the two abstractions packages | It also references `Microsoft.Agents.AI` and the ASP.NET Core shared framework | `AgentSkill` (for `AddSkill`) lives in `Microsoft.Agents.AI`; `MapWebhook` hands out `HttpContext`. Both are shared from the host, so types still match across the plugin boundary. |
| `chat.AsBuilder().UseAIContextProviders(new CompactionProvider(...))` | `CompactingChatClient` runs the same strategies through `CompactionProvider.CompactAsync` | With the provider registered on the chat client, Agent Framework 1.23 stops passing request messages to the `ChatHistoryProvider` whenever a tool runs, so user messages were lost from history. Covered by `RunOrchestratorTests`. |
| SQLite through EF Core | `Microsoft.Data.Sqlite` directly | The index is a handful of tables; EF Core arrives with ASP.NET Core Identity in phase 5. |
| Secrets in the OS credential store | A 0600 `secrets.json` behind `SecretStore` | Keychain, DPAPI and libsecret slot in behind the same class. |
| Triggers reference a run template | Triggers reference a template, or name `agent`, `workspace` and `prompt` directly | The direct form stays for simple runs against an existing folder (a chat assistant in a named workspace) that need no workspace build. |
| `run:` steps run `setup.sh` from the workspace | A `./name` missing from the workspace runs from the template folder, mounted read-only | Setup scripts work without copying them into the agent's workspace. |
| `git` steps | Run on the host, not in the sandbox | Clones need network and credentials the sandbox (often `network: none`) does not have; no repository code runs during a clone. |
| `network: allowlist` through an egress proxy | Treated as `none` | Fails closed until the proxy exists. |
| Processes started from any thread | All child processes start from one dedicated thread (`ProcessSpawner`) | `bwrap --die-with-parent` uses `PR_SET_PDEATHSIG`, which fires when the forking *thread* exits; retiring thread-pool threads would kill sandboxed commands. |
| Parked runs keep their whole `RunRequest` | Everything except `ExtraTools` is kept | `submit_output` is rebuilt from the run template on resume; `ExtraTools` is only for programmatic callers. |
| `submit_output` structured-output schema | The schema is sent as plain tool parameters, minus `$schema`/`$id` | Works with any chat-completions tool calling (Ollama included); validation happens in the harness either way. |
| `harness` with no arguments opens the TUI | Opens line-mode chat in the current directory | The Terminal.Gui TUI is not built yet. |

## Not built yet

- **Phase 4:** output sinks (`reply`, `file`, `webhook`, `run`), `run-completed` source, coalescing, per-sender rate limits,
  daily token budgets, retention policies, compaction checkpoints (summaries in `checkpoints.jsonl`).
- **Egress proxy** for `network: allowlist`; `harness sandbox test <profile>`.
- **Terminal UI** (Terminal.Gui 2.5), with the views, keymap and composer from the design.
- **Phase 5:** ASP.NET Core Identity with passkeys and step-up, device-pairing login, scoped tokens, the PWA, Web Push.
- **Phase 6:** Signal, WhatsApp and Messenger sources with in-chat approvals.
- OpenTelemetry exporters (traces are emitted but not exported), JSON Schemas for config files,
  `harness update`, service install on macOS and Windows, the `dotnet new harness-plugin` template and sample plugins,
  MCP OAuth, AG-UI and MCP-server adapters.
