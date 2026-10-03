# End-to-end checks

Manual checks against a real daemon, for what unit tests cannot reach: the terminal UI as drawn in a terminal, and
passkeys with a real WebAuthn ceremony. Not part of `dotnet test`.

- `mockllm.py PORT` — a scripted OpenAI-compatible chat-completions server (streaming and not). Messages containing
  `shell`, `list` or `edit` get a tool call, `slow` waits 8 s, anything else an echo; after a tool result it answers with
  Markdown. Point a model profile at it: `provider: openai`, `endpoint: http://127.0.0.1:PORT/v1`.
- `ptydrive.py COLSxROWS 'steps' -- command…` — runs a command in a pseudo-terminal, sends keys and prints the screen as
  a terminal would show it (needs `pip install pyte`). Steps are separated by `;;`: `w:1.5` waits, `s:text` sends text
  with Python escapes (`\r` Enter, `\x1b` Esc, `\x0b` Ctrl+K, `\x1b\r` Alt+Enter, `\x1b[200~…\x1b[201~` a bracketed
  paste), `p` prints the screen, `fg:N` prints row N's colours.
- `passkey.mjs BASE SETUPCODE` — signs up, registers a passkey, steps up, signs in with it and with a password, using
  Chromium through Playwright and a CDP virtual authenticator. Use `http://localhost:PORT` (a secure context) for an
  API listener on `127.0.0.1:PORT`, a fresh `identity.db`, and the setup code from the daemon log or
  `harness admin setup`. Run with `NODE_PATH=/opt/node22/lib/node_modules node passkey.mjs …` where Playwright is global.

- `pwa.mjs BASE SETUPCODE OUTDIR` — the web app at phone size: setup, a passkey, chat with an approval (automatic
  step-up), dashboard, triggers, settings; screenshots land in OUTDIR. Needs the model profile on `mockllm.py`, a named
  workspace, and a fresh `identity.db`.
- `push.mjs BASE USER PASSWORD` — the service worker's push handling: delivers a message through CDP and reads back the
  notification (full Chromium via `channel: 'chromium'`; the headless shell has no notifications). It also tries a real
  subscription, which fails without a browser push service.
- Type-check the web app: `cd src/Harness.Server/wwwroot && npx -p typescript@5 tsc --noEmit --allowJs --checkJs
  --target es2022 --module es2022 --lib es2023,dom,dom.iterable app.js`. The daemon serves the copy embedded at build
  time, so rebuild before checking a change in the browser.

Typical session:

```bash
python3 tests/e2e/mockllm.py 18080 &
harness --home /tmp/h init && $EDITOR /tmp/h/config.yaml     # model profile → the mock; listeners.api for passkeys
harness --home /tmp/h serve &
python3 tests/e2e/ptydrive.py 120x30 'w:3;;s:please list the files;;s:\r;;w:3;;p;;s:\x11' -- harness --home /tmp/h
```
