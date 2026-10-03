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

**Compaction checkpoints (phase 4)**: at the start of every run (not a resume), when the history since the last checkpoint
is estimated (JSON length / 4) above the agent's `compaction.summarizeAfterTokens` (default: half the model profile's
`contextWindow`, or 32,000; `0` turns it off), everything before the last `compaction.keepTurns` (default 4) user turns is
summarised by the run's model and appended to `checkpoints.jsonl` as `{ upToSeq, ts, summary }`, with a `CHECKPOINT` event.
The cut is always just before a user turn, so no tool call is separated from its result. The summary call goes through the
model-call hooks and counts toward the run's tokens; it sees the previous summary plus a plain-text transcript (tool results
clipped, oldest entries dropped when it would not fit). `FileChatHistoryProvider` sends the latest summary plus the messages
after it; `history.jsonl` is never rewritten. In-run compaction (old tool results to stubs, the sliding turn window) still
applies on top. A failed summary is a `RUN_STATE` notice, not a failed run.

**Approvals and sandboxing (phase 2)**: `ask | allow | deny | allowlist` per tool, MCP server or skill scripts; inline,
cross-client approvals with "always for this session"; unattended runs treat `ask` as `deny`. Approvals are durable:
a run waiting on a person is parked in SQLite with its whole approval batch (and any answers already given) plus its
agent session state. When the daemon restarts, the approvals are pending again, approval timeouts keep counting from the
original request, and the run resumes on its session when the last one is answered (or can be cancelled). A parked run
is unparked before it resumes, so a crash mid-resume can never run an approved tool twice. Sandbox providers
`none`, `bubblewrap` (namespaces unshared, read-only system dirs, private `/tmp` and home) and `container`
(Docker/Podman with CPU, memory and PID limits). Triggered runs refuse to start unsandboxed unless allowed.

**Egress proxy (`network: allowlist`)**: a bubblewrap sandbox with `network: allowlist` keeps its network namespace unshared.
The daemon starts an HTTP proxy per sandbox on a Unix socket in a private 0700 folder, bind-mounted at `/harness/egress`.
Inside, the harness binary runs as `harness __egress-forward 3128 /harness/egress/proxy.sock -- <command>`: it listens on
the sandbox's own `127.0.0.1:3128`, relays each connection to the socket, and runs the command as its child (stdio and exit
code pass through); `HTTP(S)_PROXY`/`ALL_PROXY` (both cases) point at it and `NO_PROXY` keeps loopback direct. The proxy
serves `CONNECT host:port` and absolute-form `http://` requests (forwarded with `Connection: close`), resolves names in the
daemon, and only connects to `allowHosts`: `host` (ports 80 and 443), `*.domain` (subdomains only), `host:port`. Refusals
answer 403 with the reason and become `EGRESS_DENIED` run events. When the daemon itself needs a proxy (`HTTPS_PROXY`,
`HTTP_PROXY`, `ALL_PROXY`, with credentials, honouring `NO_PROXY` hosts, suffixes and CIDRs), connections are chained
through it. Setup steps, skill scripts and stdio MCP servers go through the same path. The forwarder needs only the binary
for a single-file publish; under `dotnet harness.dll` the app folder and the .NET install are mounted read-only too.

`harness sandbox test <profile>` (`POST /api/sandboxes/{name}/test`, run by the daemon so it sees the daemon's environment)
starts the profile on a scratch workspace and checks from the inside: commands run, the workspace is read-write and visible
on the host, `/usr` is read-only, the harness home is invisible, each mount exists with its mode, the network matches the
profile (no direct route to 1.1.1.1:53 for `none`/`allowlist`; up to three allowlisted hosts reachable by `CONNECT` through
the proxy; an outside host refused with 403), and limits (cgroup `memory.max`, `pids.max`, `cpu.max` in containers; a
warning where the provider does not enforce them). It prints pass / warn / fail and exits 1 on any failure.
`harness sandbox ls` lists the profiles.

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
(HMAC-SHA256; a JSON body's `text` and `sender` become the event's), `file-watch`, `manual`, `run-completed` and plugin sources. `run-completed` (`triggers: [a, b]` or
`trigger: a`, `states:` defaulting to `succeeded`, or `any`) fires once per finished upstream run, after its delivery; without
`triggers:` it follows every other trigger, never its own. Its event text is the run's text (its error when it did not
succeed), it inherits the run's reply address, and `{event.data.run.state}`, `{event.data.run.output.x}`… expose the rest:
every value in an event's data is a `{event.data.a.b}` placeholder. Chains through `run-completed` and the `run` sink share
one depth count, stored with the queued events, and stop after 5 runs.

`coalesce: 5s` merges events of one conversation (the rendered session key, or the sender) into one run: each event passes
the sender filter and `OnTriggerFired` hooks on its own, then waits; the window restarts with every new event, capped at six
windows after the first. The merged event joins the texts with newlines, combines attachments, keeps the last reply address
and lists the originals in `{event.data.coalesced}`; every original is marked `started` with the run's id. Waiting events
stay `pending` in the durable queue, so a restart or reload replays and re-coalesces them, and an in-memory set keeps a replay
from running an event this daemon is already handling. Manual fires and the `run` sink never coalesce.

`concurrency: { perSession, global, onBusy }` limits a trigger's runs: `global` across all its sessions, `perSession` per
rendered session key (triggers without `session:` only have `global`). With `onBusy: queue` (default) the run is created at
once and waits in `queued` (with a `RUN_STATE` notice) until a slot that fits its key frees up, in arrival order; it holds the
slot through approvals and delivery until it is final, and can be cancelled while waiting. Its events stay `queued` in the
durable queue until it gets the slot, so a restart replays them (interrupted runs are now marked failed before hosted
services start, so the replay sees them as final). With `onBusy: drop` the event is dropped and logged with status `busy`,
and a manual fire answers with the reason. These sit on top of the daemon-wide `runs.globalConcurrency` and per-model-profile
`maxConcurrency` limits.

`rateLimit: { perSender: 10/1h }` (also `3/m`) counts a sender's events in a sliding window from the durable queue, which now
records the sender: only events queued earlier that were let through count, so two messages arriving together cannot both
push each other over. Events over the limit are dropped and logged with status `rate_limited` before any hook runs. Manual
fires, chained runs and events without a sender are never limited.

`budget: { dailyTokens, timeZone }` caps the input plus output tokens of all a trigger's runs per calendar day (local time
unless `timeZone:` is set), counted from the run index plus the live usage of active runs. Once it is spent, events are
dropped with status `over_budget` and manual fires are refused with the reason. A run that starts while budget remains gets
the rest as its token limit (the lower of that and the agent's or template's `maxTokens`), and fails with "Trigger 'x' used up
its daily token budget" when it reaches it (checked before each model call, so the last call may overshoot). `harness triggers ls` shows today's usage against the budget.

**Retention (phase 4)**: `config.yaml` `retention: { sessions, interactiveSessions, runs, events, interval }` (ages such as
`30d`, or `never`; everything but `events: 30d` defaults to `never`) and a trigger's own `retention: { sessions, runs }`. The
daemon sweeps every `interval` (first sweep 30 s after start, once parked runs are back): sessions idle longer than their age
are deleted with their run records and run folders, unless they have an active or non-final run; run folders
(`runs/<run-id>/`, workspace and output files) of runs finished longer ago than `runs` are deleted, as are folders whose run
record is gone; handled trigger events older than `events` are forgotten (pending and queued ones are kept). `runs/scratch`
is never touched. `harness sessions prune [--dry-run]` (`POST /api/retention/sweep`) sweeps on demand and lists what it
removed. A trigger references a run template with `template:`; its
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
states are `succeeded`, `invalid_output`, `failed`, `timed_out`, `cancelled` and `rejected`. `submit_output` is sent with
`strict: false` and an explicit `additionalProperties` (the OpenAI adapter otherwise defaults it to `false`, which would
steer a model to submit `{}` for a free-form object); validation errors list only the failing leaves.

**Output sinks (phase 4)**: a trigger's `output: { sinks: [...] }` (or the template's, when the trigger has none) is
delivered in the `delivering` state once the run is final. Settings are rendered with the run's variables plus `{run.id}`,
`{state}`, `{text}`, `{error}`, `{output}` and `{output.a.b}`, and `secret:`/`env:` references are resolved. A sink runs
for `succeeded` unless its `on:` lists `invalid_output` or `failed`; timeouts, cancellations and rejections never deliver.
`from: text | output | output.<field> | error | file:<name>` picks the content. Built-ins: `file` (path relative to the
harness home, `~` expanded, optional `append`), `webhook` (POSTs the result as JSON or a rendered `body:`, extra `headers:`,
HMAC-signed with `secret:` as `X-Harness-Signature`, the header the webhook source verifies), `reply` (sent through the
trigger source that received the event when it implements `IReplyChannel`, or a registered `IReplyChannel`; skipped when
the run has no reply address) and `run` (fires another trigger with `text:`/`inputs:` rendered from this run's result,
which the next run also sees as `{event.data}`; chains stop after 5 runs). Plugins add sinks with `AddOutputSink<T>`.
Each delivery emits `OUTPUT_DELIVERED`; when a sink fails, a run with valid output ends `failed` ("Output was valid but
delivery failed: …") and keeps its output. `GET /api/templates` and `harness templates ls` list the run templates.

**Terminal UI (in progress)**: `Harness.Tui` (Terminal.Gui 2.5.0) is a pure client of the daemon through `Harness.Client`.
`harness` with no arguments, `harness chat` without a message, `harness runs watch` and `harness runs attach <id>` open it in
a terminal; piped output, `--plain`, `--json` or `TERM=dumb` keep the line-mode commands. Layout: a sidebar (sessions grouped
by workspace with a Triggered group, filter and badges; live runs below), the main pane (transcript and composer, or the
runs, run detail, approvals and triggers views) and a status bar. The transcript streams assistant text, folds tool calls,
and shows pending approvals as cards answered inline (`a`, `d` with a reason, `A`). State lives in `Harness.Tui.State`
(keymap, store/reducer, transcript model, renderer, composer logic), which needs no terminal and is unit-tested.
New API: `PATCH /api/sessions/{id}` (rename, also `harness sessions rename`) and `GET /api/sessions/{id}/files?q=` (`@`
completion in the session's workspace).

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
| The sandbox reaches the egress proxy through a bound Unix socket | A forwarder inside the sandbox (the harness binary) bridges `127.0.0.1:3128` to that socket and runs the command as its child | Tools only take TCP proxies from `HTTP(S)_PROXY`, and an unshared network namespace has nothing but loopback; something inside has to listen on it. This costs one .NET start-up per sandboxed command in allowlisted profiles. |
| Containers reach the proxy over an internal network | `network: allowlist` in a `container` profile still means no network | The forwarder would need the harness (and for `dotnet harness.dll`, the .NET runtime) to run inside arbitrary images, and no container runtime was available to verify it; it fails closed, and `sandbox test` says so. |
| `bubblewrap` limits | `cpus`, `memoryMb` and `pids` are not enforced by bubblewrap (only the wall clock is) | bwrap has no cgroup support; `sandbox test` warns. Use a container profile for hard limits. |
| Processes started from any thread | All child processes start from one dedicated thread (`ProcessSpawner`) | `bwrap --die-with-parent` uses `PR_SET_PDEATHSIG`, which fires when the forking *thread* exits; retiring thread-pool threads would kill sandboxed commands. |
| Parked runs keep their whole `RunRequest` | Everything except `ExtraTools` and the concurrency `Gate` is kept | `submit_output` is rebuilt from the run template on resume; `ExtraTools` is only for programmatic callers. A run resumed after a restart does not count against its trigger's `concurrency:` limits; slots are in-memory. |
| Delivery failures | A failed sink turns `succeeded` into `failed` | The design leaves it open; valid output that never reached its destination is not a success, and the output stays in `runs/<run-id>/output`. |
| `reply` sink | Replies through the trigger source instance (`IReplyChannel`) | The source that received the event already holds the channel's credentials; no separate registration is needed. |
| `submit_output` structured-output schema | The schema is sent as plain tool parameters, minus `$schema`/`$id` | Works with any chat-completions tool calling (Ollama included); validation happens in the harness either way. |

## Not built yet

- The egress proxy for `container` sandboxes.
- **Terminal UI** (Terminal.Gui 2.5), with the views, keymap and composer from the design.
- **Phase 5:** ASP.NET Core Identity with passkeys and step-up, device-pairing login, scoped tokens, the PWA, Web Push.
- **Phase 6:** Signal, WhatsApp and Messenger sources with in-chat approvals.
- OpenTelemetry exporters (traces are emitted but not exported), JSON Schemas for config files,
  `harness update`, service install on macOS and Windows, the `dotnet new harness-plugin` template and sample plugins,
  MCP OAuth, AG-UI and MCP-server adapters.
