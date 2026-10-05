# Running the daemon on your own machine

The daemon serves the web app itself, so whatever machine runs `harness serve` hosts the PWA: there is nothing to deploy
anywhere else. This page sets it up on a Linux laptop or desktop, makes it reachable from your phone over Tailscale, and
keeps it on the latest release automatically.

```
push to main ──► Release workflow: test, build 4 platforms, sign, publish GitHub release
                                   │
laptop: harness-update.timer (hourly) ──► harness update: download, verify, swap the binary,
                                          restart the daemon once no run is active
phone ──► Tailscale ──► https://<laptop>.<tailnet>.ts.net ──► tailscale serve ──► 127.0.0.1:7443
```

## Install

Download the executable for your machine from the latest release (`harness-linux-x64` or `harness-linux-arm64`) into
a folder you can write to, so it can update itself:

```bash
mkdir -p ~/.local/bin
gh release download --repo <owner>/<repo> --pattern harness-linux-x64 --output ~/.local/bin/harness
chmod +x ~/.local/bin/harness
harness init                         # sample config, agent and sandbox profiles in ~/.harness
harness install --auto-update        # systemd user service + hourly update timer; keeps running after logout
```

## Reach it from your phone

Install [Tailscale](https://tailscale.com) on the laptop and the phone. In the admin console's DNS page, turn on MagicDNS
and HTTPS certificates, then on the laptop:

```bash
sudo tailscale up
sudo tailscale serve --bg --https=443 http://127.0.0.1:7443
tailscale status --json | jq -r .Self.DNSName      # e.g. laptop.tail1234.ts.net.
```

In `~/.harness/config.yaml`:

```yaml
listeners:
  api: http://127.0.0.1:7443
  publicHost: laptop.tail1234.ts.net   # passkeys are bound to this name; keep it stable
  behindProxy: true                    # Tailscale Serve terminates TLS on this machine
```

`systemctl --user restart harness`, then `journalctl --user -u harness` shows the one-time setup link
(`https://laptop.tail1234.ts.net/setup?code=…`; `harness admin setup` prints a new code). Open it on the phone, create the
owner, add a passkey, and install the app from the browser menu. Only devices on your tailnet can reach it; nothing is
exposed to the internet.

## Things to know

- **The laptop has to be awake.** While it sleeps the app is unreachable, schedules do not fire (each schedule's
  `skip`/`runOnce`/`catchUp` decides what happens to missed fires) and no push notifications go out. To keep it serving
  with the lid closed, set `HandleLidSwitchExternalPower=ignore` in `/etc/systemd/logind.conf`.
- **Sandbox what you approve from the phone.** The agent runs next to your SSH keys and other repositories. The sample
  `coder` agent uses `sandbox: none`; agents you drive remotely should use the `workspace` bubblewrap profile (Ubuntu
  24.04 and later only allow `bwrap` to create user namespaces with an AppArmor profile for it).
- **Models.** A local Ollama model works as is. Azure OpenAI with `auth: { type: entra }` picks up your `az login`,
  because the user service runs as you.

## Releases and automatic updates

`.github/workflows/release.yml` runs on every push to `main` (docs-only changes aside): it runs the tests, publishes
`harness-linux-x64`, `harness-linux-arm64`, `harness-osx-arm64` and `harness-win-x64.exe` as release `v1.0.<run>`, with
`SHA256SUMS` and, once a signing key is set up, `SHA256SUMS.sig`. Public repositories also get a build provenance
attestation (`gh attestation verify harness-linux-x64 --repo <owner>/<repo>`).

`harness update` (hourly from the timer, or by hand) installs a newer release:

1. Asks the GitHub API for the latest release and the asset for this machine.
2. Checks the signature over `SHA256SUMS` with the key the running build was published with, checks that the file names
   this release, and checks the download's SHA-256. Anything that fails is deleted and nothing changes.
3. Renames the new executable over the old one (atomic; the running daemon keeps the old file open).
4. Restarts `harness.service` if the daemon runs an older version **and no run is active**. Otherwise it says so, and the
   next check restarts it. `--force` restarts anyway (active runs are then marked failed); runs parked on an approval
   survive a restart either way. `--check` only reports.

`harness update` works on Linux and macOS (on macOS, restart the daemon yourself); Windows is not supported yet.

### Signing key (recommended)

Without a key, `harness update` only compares checksums, which catches a broken download but not a tampered release.
Create a P-256 key once, keep the private half as a secret and publish the public half as a variable; every build
then embeds the public key and only installs releases signed with it:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out release-signing.pem
gh secret set RELEASE_SIGNING_KEY < release-signing.pem
gh variable set RELEASE_SIGNING_PUBLIC_KEY --body "$(openssl ec -in release-signing.pem -pubout -outform DER | base64 -w0)"
shred -u release-signing.pem      # or keep it offline; a lost key means installing the next release by hand
```

A build made before the key existed checks checksums only; the first signed release it installs carries the key from
then on.

### Private repositories

The release API needs a token for a private repository. Create a fine-grained personal access token with read-only
*Contents* access to this repository only, then:

```bash
harness secrets set github-releases          # paste the token
```

```yaml
update:
  token: secret:github-releases
```

`update.repository` (for a fork) and `update.publicKey` override what the build recorded.
