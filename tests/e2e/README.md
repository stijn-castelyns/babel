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

Typical session:

```bash
python3 tests/e2e/mockllm.py 18080 &
harness --home /tmp/h init && $EDITOR /tmp/h/config.yaml     # model profile → the mock; listeners.api for passkeys
harness --home /tmp/h serve &
python3 tests/e2e/ptydrive.py 120x30 'w:3;;s:please list the files;;s:\r;;w:3;;p;;s:\x11' -- harness --home /tmp/h
```
