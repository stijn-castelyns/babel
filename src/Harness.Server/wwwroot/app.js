// @ts-check
// The harness web app: a client of the daemon API on the same origin, signed in with a cookie. No framework and no build
// step. Everything from the server is inserted as text, never as HTML.

const main = /** @type {HTMLElement} */ (document.getElementById('main'));
const nav = /** @type {HTMLElement} */ (document.getElementById('nav'));
const badge = /** @type {HTMLElement} */ (document.getElementById('badge'));
const toastBox = /** @type {HTMLElement} */ (document.getElementById('toast'));

const ACTIVE = ['queued', 'preparing', 'running', 'awaiting_approval', 'validating', 'delivering'];

/** @type {{ signedIn: boolean, setupNeeded: boolean, user?: string, role?: string, method?: string, steppedUp: boolean, passkeys: number } | null} */
let me = null;
/** @type {EventSource[]} streams owned by the current screen */
let streams = [];
/** @type {EventSource | null} */
let firehose = null;

// ---- small helpers ----

/**
 * Builds an element. Strings become text nodes, so server data can never inject markup.
 * @param {string} tag
 * @param {Record<string, any> | null} [attrs]
 * @param {...(Node | string | null | undefined | false)} children
 * @returns {HTMLElement}
 */
function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs ?? {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = v;
    else el.setAttribute(k, v === true ? '' : String(v));
  }
  for (const c of children) if (c !== null && c !== undefined && c !== false) el.append(c);
  return el;
}

/** @param {string} text */
function toast(text) {
  toastBox.textContent = text;
  toastBox.hidden = false;
  clearTimeout(/** @type {any} */ (toast).timer);
  /** @type {any} */ (toast).timer = setTimeout(() => { toastBox.hidden = true; }, 4000);
}

/** @param {string | null | undefined} iso */
function ago(iso) {
  if (!iso) return '-';
  const s = (Date.now() - Date.parse(iso)) / 1000;
  return s < 60 ? 'now' : s < 3600 ? `${Math.floor(s / 60)}m ago` : s < 172800 ? `${Math.floor(s / 3600)}h ago` : `${Math.floor(s / 86400)}d ago`;
}

/** @param {number} n */
const tokens = (n) => n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1000 ? `${Math.floor(n / 1000)}k` : String(n);

/** @param {Record<string, any> | null | undefined} args */
function mainArgument(args) {
  if (!args) return '';
  for (const k of ['command', 'path', 'pattern', 'handle', 'query', 'name']) if (args[k] !== undefined) return typeof args[k] === 'string' ? args[k] : JSON.stringify(args[k]);
  return Object.keys(args).length ? JSON.stringify(args) : '';
}

/**
 * Light Markdown as DOM nodes (fenced code, inline code, bold, headings); text is never parsed as HTML.
 * @param {string} text
 */
function markdown(text) {
  const out = document.createDocumentFragment();
  const parts = text.split(/^```[^\n]*\n?/m);
  parts.forEach((part, i) => {
    if (i % 2 === 1) { out.append(h('pre', null, part.replace(/\n$/, ''))); return; }
    for (const line of part.split('\n')) {
      const heading = /^#{1,6}\s+(.*)$/.exec(line);
      const el = h(heading ? 'strong' : 'span');
      const source = heading ? heading[1] : line;
      source.split(/(`[^`]+`|\*\*[^*]+\*\*)/).forEach((bit) => {
        if (bit.startsWith('`') && bit.endsWith('`') && bit.length > 1) el.append(h('code', null, bit.slice(1, -1)));
        else if (bit.startsWith('**') && bit.endsWith('**') && bit.length > 3) el.append(h('strong', null, bit.slice(2, -2)));
        else el.append(bit);
      });
      out.append(el, '\n');
    }
  });
  return out;
}

/** @param {string} state */
const stateChip = (state) => h('span', { class: `state ${state}` }, state.replace('_', ' '));

class ApiError extends Error {
  /** @param {number} status @param {string} message */
  constructor(status, message) { super(message); this.status = status; }
}

/**
 * Calls the API. A 403 that asks for a step-up runs a passkey assertion and retries once; a 401 goes to sign-in.
 * @param {string} path
 * @param {{ method?: string, body?: any, retried?: boolean }} [options]
 */
async function api(path, options = {}) {
  const init = /** @type {RequestInit} */ ({ method: options.method ?? (options.body === undefined ? 'GET' : 'POST'), credentials: 'same-origin', headers: {} });
  if (options.body !== undefined) {
    init.body = JSON.stringify(options.body);
    /** @type {any} */ (init.headers)['content-type'] = 'application/json';
  }
  const response = await fetch(path, init);
  const text = await response.text();
  /** @type {any} */
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (response.ok) return data;
  const message = data?.error ?? response.statusText;
  if (response.status === 403 && String(message).startsWith('step-up required') && !options.retried) {
    toast('Confirm with your passkey');
    await passkeySignIn();
    return api(path, { ...options, retried: true });
  }
  if (response.status === 401 && !path.startsWith('/auth/')) {
    me = null;
    location.hash = '#/login';
  }
  throw new ApiError(response.status, message);
}

/** Runs an action, showing its error instead of throwing. @param {() => Promise<any>} action */
async function attempt(action) {
  try { return await action(); } catch (e) { toast(e instanceof Error ? e.message : String(e)); return undefined; }
}

// ---- passkeys ----

/** Signs in with a passkey, or steps up the current session. */
async function passkeySignIn() {
  if (!window.PublicKeyCredential) throw new Error('This browser has no passkey support.');
  const options = await api('/auth/passkey/request-options', { body: {} });
  const credential = /** @type {any} */ (await navigator.credentials.get({ publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(options) }));
  await api('/auth/passkey/signin', { body: { credential: JSON.stringify(credential.toJSON()) } });
  me = await api('/auth/status');
}

/** @param {string} name */
async function passkeyRegister(name) {
  const options = await api('/auth/passkey/creation-options', { body: {} });
  const credential = /** @type {any} */ (await navigator.credentials.create({ publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(options) }));
  await api('/auth/passkey/register', { body: { credential: JSON.stringify(credential.toJSON()), name } });
  me = await api('/auth/status');
}

// ---- live updates ----

/** Keeps the approvals badge current and lets the open screen refresh when runs change anywhere. */
function startFirehose() {
  if (firehose) return;
  firehose = new EventSource('/api/events');
  const kinds = ['RUN_STARTED', 'RUN_STATE', 'RUN_FINISHED', 'APPROVAL_REQUESTED', 'APPROVAL_RESOLVED'];
  for (const kind of kinds) firehose.addEventListener(kind, () => scheduleRefresh());
  refreshBadge();
}

let refreshTimer = 0;
/** @type {(() => void) | null} */
let onLiveChange = null;
function scheduleRefresh() {
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(() => { refreshBadge(); onLiveChange?.(); }, 300);
}

async function refreshBadge() {
  const approvals = await api('/api/approvals').catch(() => []);
  badge.hidden = approvals.length === 0;
  badge.textContent = `${approvals.length} waiting`;
  badge.onclick = () => { location.hash = '#/'; };
}

function closeStreams() {
  for (const s of streams) s.close();
  streams = [];
  onLiveChange = null;
}

// ---- screens ----

/** @param {string} title @param {...(Node | string | null | false)} children */
function page(title, ...children) {
  main.replaceChildren(h('h1', null, title), ...children.filter((c) => c !== null && c !== false).map((c) => /** @type {Node | string} */ (c)));
  document.title = `${title} · harness`;
}

async function renderSetup() {
  const code = new URLSearchParams(location.search).get('code') ?? '';
  const form = h('form', {
    onsubmit: async (/** @type {Event} */ e) => {
      e.preventDefault();
      const data = Object.fromEntries(new FormData(/** @type {HTMLFormElement} */ (e.target)));
      // attempt() yields undefined only on an error, which it has shown already.
      if (await attempt(() => api('/auth/setup', { body: data })) === undefined) return;
      me = await api('/auth/status');
      history.replaceState(null, '', '/#/settings');
      toast('Owner created. Register a passkey now: approvals need it.');
      route();
    },
  },
  h('label', null, 'Setup code', h('input', { name: 'code', value: code, required: true, autocomplete: 'off' })),
  h('label', null, 'User name', h('input', { name: 'userName', required: true, autocomplete: 'username' })),
  h('label', null, 'Password (12 characters or more)', h('input', { name: 'password', type: 'password', minlength: 12, required: true, autocomplete: 'new-password' })),
  h('button', { class: 'primary', type: 'submit' }, 'Create the owner'));
  page('Set up harness', h('p', { class: 'muted' }, 'This one-time setup creates the owner. The code is in the daemon log, or run "harness admin setup" on the machine.'), form);
}

function renderLogin() {
  const passkey = h('button', {
    class: 'primary',
    onclick: () => attempt(async () => { await passkeySignIn(); location.hash = '#/'; route(); }),
  }, 'Sign in with a passkey');
  const form = h('form', {
    onsubmit: async (/** @type {Event} */ e) => {
      e.preventDefault();
      const data = Object.fromEntries(new FormData(/** @type {HTMLFormElement} */ (e.target)));
      await attempt(async () => {
        await api('/auth/password', { body: { ...data, remember: true } });
        me = await api('/auth/status');
        location.hash = '#/';
        route();
      });
    },
  },
  h('label', null, 'User name', h('input', { name: 'userName', required: true, autocomplete: 'username webauthn' })),
  h('label', null, 'Password', h('input', { name: 'password', type: 'password', required: true, autocomplete: 'current-password' })),
  h('button', { type: 'submit' }, 'Sign in with password'));
  page('Sign in', passkey, h('h2', null, 'Or with your password'), h('p', { class: 'muted' }, 'A password session can view and chat; approvals ask for your passkey.'), form);
}

/** @param {any} a an ApprovalDto */
function approvalCard(a) {
  const decide = (/** @type {boolean} */ approved, /** @type {boolean} */ always) => attempt(async () => {
    const reason = approved ? undefined : (prompt('Reason for denying (optional)') ?? undefined);
    await api(`/api/runs/${encodeURIComponent(a.runId)}/approvals/${encodeURIComponent(a.requestId)}`, { body: { approved, always, reason } });
    toast(approved ? 'Approved' : 'Denied');
    scheduleRefresh();
  });
  const detail = a.toolName === 'edit'
    ? h('pre', null, `- ${a.arguments?.old ?? ''}\n+ ${a.arguments?.new ?? ''}`)
    : h('pre', null, a.summary ?? JSON.stringify(a.arguments, null, 2));
  return h('div', { class: 'card approval' },
    h('div', { class: 'row' }, h('strong', { class: 'grow' }, `${a.toolName} wants to run`), h('a', { href: `#/run/${a.runId}`, class: 'muted' }, `${a.runId} · ${ago(a.requestedAt)}`)),
    detail,
    h('div', { class: 'row' },
      h('button', { class: 'primary', onclick: () => decide(true, false) }, 'Approve'),
      h('button', { onclick: () => decide(true, true) }, 'Always for this session'),
      h('button', { class: 'danger', onclick: () => decide(false, false) }, 'Deny')));
}

/** @param {any} r a RunDto */
function runRow(r) {
  return h('tr', null,
    h('td', null, h('a', { href: `#/run/${r.id}` }, r.id)),
    h('td', null, r.triggerId ?? 'manual'),
    h('td', null, stateChip(r.state)),
    h('td', { class: 'muted' }, ago(r.createdAt)),
    h('td', { class: 'muted' }, tokens(r.inputTokens + r.outputTokens)),
    h('td', { class: 'muted mono' }, r.lastTool ?? ''));
}

/** @param {any[]} runs */
const runTable = (runs) => runs.length === 0 ? h('p', { class: 'muted' }, 'None.')
  : h('div', { class: 'scroll' }, h('table', { class: 'runs' }, h('tr', null, ...['Run', 'Trigger', 'State', 'Created', 'Tokens', 'Last tool'].map((t) => h('th', null, t))), ...runs.map(runRow)));

async function renderDashboard() {
  const draw = async () => {
    const [approvals, runs] = await Promise.all([api('/api/approvals'), api('/api/runs?limit=40')]);
    const active = runs.filter((/** @type {any} */ r) => ACTIVE.includes(r.state));
    const day = Date.now() - 86400000;
    const failures = runs.filter((/** @type {any} */ r) => ['failed', 'invalid_output', 'timed_out'].includes(r.state) && Date.parse(r.createdAt) > day);
    page('Dashboard',
      h('h2', null, `Approvals waiting (${approvals.length})`),
      approvals.length ? h('div', null, ...approvals.map(approvalCard)) : h('p', { class: 'muted' }, 'Nothing is waiting for you.'),
      h('h2', null, `Active runs (${active.length})`), runTable(active),
      h('h2', null, 'Recent failures'), runTable(failures),
      h('h2', null, 'Recent runs'), runTable(runs.filter((/** @type {any} */ r) => !ACTIVE.includes(r.state)).slice(0, 15)));
  };
  onLiveChange = () => { draw(); };
  await draw();
}

/**
 * Renders one run's events into a container as they arrive (replay, then live).
 * @param {string} runId
 * @param {HTMLElement} into
 * @param {{ skipInput?: boolean, onFinished?: (state: string) => void }} [options]
 */
function followRun(runId, into, options = {}) {
  /** @type {Record<string, HTMLElement>} */ const messages = {};
  /** @type {Record<string, HTMLElement>} */ const tools = {};
  /** @type {Record<string, HTMLElement>} */ const cards = {};
  const source = new EventSource(`/api/runs/${encodeURIComponent(runId)}/events`);
  streams.push(source);
  /** @param {string} type @param {(d: any, e: MessageEvent) => void} fn */
  const on = (type, fn) => source.addEventListener(type, (e) => fn(JSON.parse(/** @type {MessageEvent} */ (e).data).data, /** @type {MessageEvent} */ (e)));
  const text = (/** @type {string} */ id) => {
    if (!messages[id]) {
      messages[id] = h('div', { class: 'text' });
      into.append(h('div', { class: 'msg' }, h('div', { class: 'who' }, 'agent'), messages[id]));
    }
    return messages[id];
  };
  on('RUN_STARTED', (d) => {
    if (!options.skipInput && d.input) into.append(h('div', { class: 'msg user' }, h('div', { class: 'who' }, d.interactive ? 'you' : `trigger ${d.triggerId ?? ''}`), h('div', { class: 'text' }, d.input)));
  });
  on('TEXT_MESSAGE_CONTENT', (d) => { text(d.messageId).append(d.delta ?? ''); });
  on('TEXT_MESSAGE_END', (d) => { text(d.messageId).replaceChildren(markdown(d.text ?? '')); });
  on('TOOL_CALL_START', (d) => {
    const result = h('pre', null, '…');
    tools[d.toolCallId] = h('details', { class: 'tool' }, h('summary', null, `${d.toolName} ${mainArgument(d.arguments)}`), h('pre', null, JSON.stringify(d.arguments, null, 2)), result);
    into.append(tools[d.toolCallId]);
  });
  on('TOOL_CALL_RESULT', (d) => {
    const el = tools[d.toolCallId];
    if (!el) return;
    /** @type {HTMLElement} */ (el.lastChild).textContent = d.content ?? '';
    const first = String(d.content ?? '').split('\n')[0];
    const summary = /** @type {HTMLElement} */ (el.firstChild);
    summary.textContent += ` · ${first}`;
    summary.className = first.startsWith('Error') || first.startsWith('Blocked') ? 'err' : '';
  });
  on('APPROVAL_REQUESTED', async (d) => {
    const pending = (await api('/api/approvals')).find((/** @type {any} */ a) => a.requestId === d.requestId);
    if (!pending) return;
    cards[d.requestId] = approvalCard(pending);
    into.append(cards[d.requestId]);
  });
  on('APPROVAL_RESOLVED', (d) => {
    cards[d.requestId]?.replaceWith(h('p', { class: d.approved ? 'ok' : 'err' }, `${d.approved ? '✓ approved' : '✗ denied'} ${d.toolName} · ${d.decidedBy ?? ''}${d.reason ? ' · ' + d.reason : ''}`));
  });
  on('RUN_STATE', (d) => { if (d.notice) into.append(h('p', { class: 'warn' }, d.notice)); });
  on('RUN_ERROR', (d) => { into.append(h('p', { class: 'err' }, `error: ${d.message}`)); });
  on('RUN_FINISHED', (d) => {
    into.append(h('p', { class: d.state === 'succeeded' ? 'muted' : 'err' }, `── ${d.state} · ${tokens(d.inputTokens ?? 0)} in / ${tokens(d.outputTokens ?? 0)} out${d.error && d.state !== 'succeeded' ? ' · ' + d.error : ''}`));
    source.close();
    options.onFinished?.(d.state);
  });
  return source;
}

/** @param {string} id */
async function renderRun(id) {
  const run = await api(`/api/runs/${encodeURIComponent(id)}`);
  const transcript = h('div');
  const actions = h('div', { class: 'row' },
    h('a', { href: `#/chat/${run.sessionId}` }, 'Open the session'),
    ACTIVE.includes(run.state) && h('button', { class: 'danger', onclick: () => attempt(async () => { await api(`/api/runs/${id}/cancel`, { body: {} }); toast('Cancelling'); }) }, 'Cancel run'));
  const output = h('div');
  page(`Run ${id}`,
    h('div', { class: 'row' }, stateChip(run.state), h('span', { class: 'muted' }, `${run.agent} · ${run.model}${run.triggerId ? ' · trigger ' + run.triggerId : ''}`)),
    actions, transcript, output);
  followRun(id, transcript, {
    onFinished: async () => {
      const done = await api(`/api/runs/${encodeURIComponent(id)}`);
      if (done.output) output.append(h('h2', null, 'Output'), h('pre', null, JSON.stringify(done.output, null, 2)));
      if (done.files?.length) output.append(h('h2', null, 'Files'), h('ul', null, ...done.files.map((/** @type {string} */ f) => h('li', { class: 'mono' }, f))));
    },
  });
}

async function renderChat() {
  const [sessions, workspaces, agents] = await Promise.all([api('/api/sessions?limit=200'), api('/api/workspaces'), api('/api/agents')]);
  /** @type {Record<string, any[]>} */
  const groups = {};
  for (const s of sessions) (groups[s.triggerId ? 'Triggered' : (s.workspaceName ?? s.workspace)] ??= []).push(s);
  const form = h('form', {
    class: 'row',
    onsubmit: async (/** @type {Event} */ e) => {
      e.preventDefault();
      const data = Object.fromEntries(new FormData(/** @type {HTMLFormElement} */ (e.target)));
      const created = await attempt(() => api('/api/sessions', { body: { workspaceName: data.workspace, agent: data.agent || undefined } }));
      if (created) location.hash = `#/chat/${created.id}`;
    },
  },
  h('select', { name: 'workspace', required: true }, ...workspaces.map((/** @type {any} */ w) => h('option', { value: w.name }, w.name))),
  h('select', { name: 'agent' }, h('option', { value: '' }, 'default agent'), ...agents.map((/** @type {any} */ a) => h('option', { value: a.name }, a.name))),
  h('button', { class: 'primary', type: 'submit', disabled: workspaces.length === 0 }, 'New session'));
  page('Chat',
    workspaces.length ? form : h('p', { class: 'muted' }, 'Register a named workspace on the machine first: harness workspace add <name> <path>.'),
    ...Object.entries(groups).flatMap(([name, list]) => [
      h('h2', null, name),
      ...list.map((s) => h('div', { class: 'card row' },
        h('a', { class: 'grow', href: `#/chat/${s.id}` }, s.title ?? s.id),
        s.parentId && h('span', { class: 'muted' }, '↳ fork'),
        h('span', { class: 'muted' }, ago(s.updatedAt)))),
    ]));
}

/** @param {string} id */
async function renderSession(id) {
  const [session, history, runs] = await Promise.all([
    api(`/api/sessions/${encodeURIComponent(id)}`),
    api(`/api/sessions/${encodeURIComponent(id)}/messages?limit=100`),
    api(`/api/runs?session=${encodeURIComponent(id)}&limit=20`),
  ]);
  const live = new Set(runs.filter((/** @type {any} */ r) => ACTIVE.includes(r.state)).map((/** @type {any} */ r) => r.id));
  const transcript = h('div');
  if (history.hasMore) transcript.append(h('p', { class: 'muted' }, 'Older messages are not shown.'));
  /** @type {Record<string, HTMLElement>} */ const calls = {};
  for (const m of history.messages) {
    if (m.runId && live.has(m.runId)) continue;
    for (const c of m.contents) {
      if (c.type === 'text' && c.text) transcript.append(h('div', { class: `msg ${m.role}` }, h('div', { class: 'who' }, m.role === 'user' ? 'you' : 'agent'), h('div', { class: 'text' }, m.role === 'user' ? c.text : markdown(c.text))));
      if (c.type === 'tool_call') {
        calls[c.callId] = h('details', { class: 'tool' }, h('summary', null, `${c.toolName} ${mainArgument(c.arguments)}`), h('pre', null, JSON.stringify(c.arguments, null, 2)));
        transcript.append(calls[c.callId]);
      }
      if (c.type === 'tool_result' && calls[c.callId]) calls[c.callId].append(h('pre', null, c.result ?? ''));
    }
  }
  for (const runId of live) followRun(runId, transcript);
  const box = /** @type {HTMLTextAreaElement} */ (h('textarea', { placeholder: 'Message (Ctrl+Enter sends)', 'aria-label': 'Message' }));
  const send = async () => {
    const text = box.value.trim();
    if (!text) return;
    const sent = await attempt(() => api(`/api/sessions/${encodeURIComponent(id)}/messages`, { body: { text } }));
    if (!sent) return;
    box.value = '';
    transcript.append(h('div', { class: 'msg user' }, h('div', { class: 'who' }, 'you'), h('div', { class: 'text' }, text)));
    followRun(sent.runId, transcript, {
      skipInput: true,
      // The daemon titles a session after its first message.
      onFinished: async () => { const s = await api(`/api/sessions/${encodeURIComponent(id)}`); if (s.title) main.querySelector('h1')?.replaceChildren(s.title); },
    });
  };
  box.addEventListener('keydown', (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); send(); } });
  page(session.title ?? session.id,
    h('div', { class: 'row muted' }, `${session.agent} · ${session.model} · ${session.workspaceName ?? session.workspace}`,
      h('button', { onclick: () => attempt(async () => { const f = await api(`/api/sessions/${id}/fork`, { body: {} }); location.hash = `#/chat/${f.id}`; }) }, 'Fork')),
    transcript,
    h('div', { class: 'composer' }, box, h('button', { class: 'primary', onclick: send }, 'Send')));
}

async function renderTriggers() {
  const draw = async () => {
    const triggers = await api('/api/triggers');
    page('Triggers', triggers.length === 0 ? h('p', { class: 'muted' }, 'No triggers. Add YAML files under ~/.harness/triggers on the machine.') :
      h('table', null,
        h('tr', null, ...['Trigger', 'Source', 'Next fire', 'Last run', 'Budget', ''].map((t) => h('th', null, t))),
        ...triggers.map((/** @type {any} */ t) => h('tr', null,
          h('td', null, t.id),
          h('td', { class: 'muted' }, t.sourceType),
          h('td', { class: 'muted' }, t.nextFireAt ? new Date(t.nextFireAt).toLocaleString() : '-'),
          h('td', null, t.lastRunId ? h('a', { href: `#/run/${t.lastRunId}` }, t.lastState ?? t.lastRunId) : '-'),
          h('td', { class: 'muted' }, t.dailyTokens ? `${tokens(t.tokensToday ?? 0)} / ${tokens(t.dailyTokens)}` : '-'),
          h('td', null, h('div', { class: 'row' },
            h('button', { onclick: () => attempt(async () => { await api(`/api/triggers/${t.id}`, { method: 'PATCH', body: { enabled: !t.enabled } }); draw(); }) }, t.enabled ? 'Disable' : 'Enable'),
            h('button', {
              onclick: () => attempt(async () => {
                const input = prompt(`Fire ${t.id} with text, or inputs as JSON`, '');
                if (input === null) return;
                /** @type {{ text?: string, inputs?: any }} */
                let body = { text: input || undefined };
                if (input.trim().startsWith('{')) { try { body = { inputs: JSON.parse(input) }; } catch { /* plain text */ } }
                const sent = await api(`/api/triggers/${t.id}/fire`, { body });
                location.hash = `#/run/${sent.runId}`;
              }),
            }, 'Fire')))))));
  };
  onLiveChange = () => { draw(); };
  await draw();
}

async function renderSettings() {
  const passkeys = await api('/auth/passkeys');
  const owner = me?.role === 'owner';
  const devices = h('div', null, h('p', { class: 'muted' }, owner ? 'Confirm with your passkey to see paired devices and tokens.' : 'Only the owner manages devices.'));
  if (owner) {
    devices.prepend(h('button', {
      onclick: () => attempt(async () => {
        const [pairings, list] = await Promise.all([api('/api/pairings'), api('/api/tokens')]);
        devices.replaceChildren(
          h('h2', null, 'Pairing requests'),
          pairings.length === 0 ? h('p', { class: 'muted' }, 'None. Run "harness login <url>" on the device.') : h('div', null, ...pairings.map((/** @type {any} */ p) => {
            const scopes = ['read', 'run', 'approve', 'admin'].map((s) => h('label', { class: 'row' }, h('input', { type: 'checkbox', value: s, checked: s === 'read' || s === 'run' }), s));
            return h('div', { class: 'card' },
              h('div', { class: 'row' }, h('strong', { class: 'grow mono' }, p.userCode), h('span', { class: 'muted' }, `${p.name} · ${p.address ?? ''} · ${ago(p.createdAt)}`)),
              h('div', { class: 'row' }, ...scopes),
              h('div', { class: 'row' },
                h('button', {
                  class: 'primary',
                  onclick: () => attempt(async () => {
                    const chosen = scopes.map((l) => /** @type {HTMLInputElement} */ (l.firstChild)).filter((i) => i.checked).map((i) => i.value);
                    await api(`/api/pairings/${encodeURIComponent(p.userCode)}`, { body: { approved: true, scopes: chosen } });
                    toast(`${p.name} approved`);
                    renderSettings();
                  }),
                }, 'Approve'),
                h('button', { class: 'danger', onclick: () => attempt(async () => { await api(`/api/pairings/${encodeURIComponent(p.userCode)}`, { body: { approved: false } }); renderSettings(); }) }, 'Deny')));
          })),
          h('h2', null, 'Tokens and paired devices'),
          h('table', null, h('tr', null, ...['Name', 'Scopes', 'Last used', ''].map((t) => h('th', null, t))),
            ...list.filter((/** @type {any} */ t) => !t.revokedAt).map((/** @type {any} */ t) => h('tr', null,
              h('td', null, t.name), h('td', { class: 'muted' }, t.scopes.join(', ')), h('td', { class: 'muted' }, ago(t.lastUsedAt)),
              h('td', null, h('button', { class: 'danger', onclick: () => attempt(async () => { await api(`/api/tokens/${t.id}`, { method: 'DELETE' }); renderSettings(); }) }, 'Revoke'))))));
      }),
    }, 'Show devices and tokens'));
  }
  page('Settings',
    h('p', { class: 'muted' }, `Signed in as ${me?.user} (${me?.role}) with ${me?.method}${me?.steppedUp ? ', confirmed with a passkey' : ''}.`),
    h('h2', null, 'Passkeys'),
    passkeys.length === 0 ? h('p', { class: 'warn' }, 'No passkey yet. Approvals, trigger fires and admin actions need one.') : h('ul', null, ...passkeys.map((/** @type {any} */ p) => h('li', null,
      `${p.name ?? 'passkey'} · added ${ago(p.createdAt)} `,
      h('button', { class: 'danger', onclick: () => attempt(async () => { if (confirm('Remove this passkey?')) { await api(`/auth/passkeys/${encodeURIComponent(p.id)}`, { method: 'DELETE' }); renderSettings(); } }) }, 'Remove')))),
    h('button', { onclick: () => attempt(async () => { await passkeyRegister(prompt('Name for this passkey', 'phone') ?? 'passkey'); toast('Passkey added'); renderSettings(); }) }, 'Add a passkey'),
    h('h2', null, 'Devices'), devices,
    h('h2', null, 'Session'),
    h('button', { onclick: () => attempt(async () => { await api('/auth/signout', { body: {} }); me = null; firehose?.close(); firehose = null; location.hash = '#/login'; route(); }) }, 'Sign out'));
}

// ---- routing ----

async function route() {
  closeStreams();
  const hash = location.hash.replace(/^#/, '') || '/';
  if (!me) me = await api('/auth/status').catch(() => null);
  if (me?.setupNeeded) return renderSetup();
  if (!me?.signedIn) {
    nav.hidden = true;
    return renderLogin();
  }
  nav.hidden = false;
  for (const a of nav.querySelectorAll('a')) a.classList.toggle('active', hash === a.getAttribute('href')?.slice(1) || (hash.startsWith('/chat') && a.getAttribute('href') === '#/chat'));
  startFirehose();
  const [, section, id] = hash.split('/');
  try {
    if (section === 'run' && id) await renderRun(decodeURIComponent(id));
    else if (section === 'chat' && id) await renderSession(decodeURIComponent(id));
    else if (section === 'chat') await renderChat();
    else if (section === 'triggers') await renderTriggers();
    else if (section === 'settings') await renderSettings();
    else if (section === 'login') { location.hash = '#/'; }
    else await renderDashboard();
  } catch (e) {
    if (e instanceof ApiError && e.status === 401) return;
    page('Something went wrong', h('p', { class: 'err' }, e instanceof Error ? e.message : String(e)));
  }
}

window.addEventListener('hashchange', route);
if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(() => { /* still works without */ });
route();
