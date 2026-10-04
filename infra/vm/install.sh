#!/usr/bin/env bash
# Sets up the harness VM and installs a release. Runs as root through `az vm run-command invoke` on every deploy
# (.github/workflows/azure-deploy.yml), so every step is idempotent. Takes its input from the environment, because
# run-command parameters are split at '=' and a SAS URL is full of them:
#
#   BINARY_URL          where to download the harness executable (a short-lived read-only SAS URL)
#   TAILSCALE_AUTHKEY   only needed until the VM has joined the tailnet
#   TAILSCALE_HOSTNAME  the machine's name on the tailnet (default: harness)
#
# run-command reports success whatever the script does, so the last line is a marker the workflow checks for.
set -euo pipefail

binary_url=${BINARY_URL:?BINARY_URL is required}
ts_authkey=${TAILSCALE_AUTHKEY:-}
ts_hostname=${TAILSCALE_HOSTNAME:-harness}
home=/var/lib/harness
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin DEBIAN_FRONTEND=noninteractive
apt_get() { apt-get -o DPkg::Lock::Timeout=600 -q "$@"; }   # unattended-upgrades holds the lock after first boot
as_harness() { runuser -u harness -- env HOME="$home" "$@"; }

# Packages: Tailscale for the HTTPS name, bubblewrap for sandboxes, ripgrep for grep, git for workspace steps.
if [ ! -f /etc/apt/sources.list.d/tailscale.list ]; then
  codename=$(sed -n 's/^VERSION_CODENAME=//p' /etc/os-release)
  curl -fsSL "https://pkgs.tailscale.com/stable/ubuntu/$codename.noarmor.gpg" -o /usr/share/keyrings/tailscale-archive-keyring.gpg
  curl -fsSL "https://pkgs.tailscale.com/stable/ubuntu/$codename.tailscale-keyring.list" -o /etc/apt/sources.list.d/tailscale.list
  apt_get update
fi
missing=()
for pkg in tailscale bubblewrap ripgrep git; do dpkg -s "$pkg" >/dev/null 2>&1 || missing+=("$pkg"); done
if [ ${#missing[@]} -gt 0 ]; then apt_get update && apt_get install -y "${missing[@]}"; fi

# Ubuntu 24.04 lets only programs with an AppArmor profile create user namespaces; give bwrap one.
if [ ! -f /etc/apparmor.d/harness-bwrap ]; then
  cat > /etc/apparmor.d/harness-bwrap <<'PROFILE'
abi <abi/4.0>,
include <tunables/global>

profile harness-bwrap /usr/bin/bwrap flags=(unconfined) {
  userns,
  include if exists <local/harness-bwrap>
}
PROFILE
  apparmor_parser -r /etc/apparmor.d/harness-bwrap
fi

# The small sizes have 1 GiB of memory; swap keeps a build in a sandbox from taking the daemon down.
if [ ! -f /swapfile ]; then
  fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile >/dev/null && swapon /swapfile
  echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi

id harness >/dev/null 2>&1 || useradd --system --create-home --home-dir "$home" --shell /bin/bash harness

# The release: a self-contained single-file executable. Replacing the file under a running daemon is safe.
install -d -m 755 /opt/harness
curl -fsSL --retry 3 -o /opt/harness/harness.new "$binary_url"
chmod 755 /opt/harness/harness.new
mv -f /opt/harness/harness.new /opt/harness/harness
ln -sf /opt/harness/harness /usr/local/bin/harness

# Tailscale: joins the tailnet once, then serves https://<host>.<tailnet>.ts.net with a real certificate.
systemctl enable --now tailscaled >/dev/null
ts_state() { tailscale status --json 2>/dev/null | python3 -c 'import json, sys; print(json.load(sys.stdin)["BackendState"])' || echo NoState; }
for _ in $(seq 10); do [ "$(ts_state)" != NoState ] && break; sleep 1; done
if [ "$(ts_state)" != Running ]; then
  if [ -z "$ts_authkey" ]; then
    echo "Tailscale is not logged in yet: set the TAILSCALE_AUTHKEY secret and deploy again." >&2
    exit 1
  fi
  tailscale up --authkey "$ts_authkey" --hostname "$ts_hostname"
fi
fqdn=$(tailscale status --json | python3 -c 'import json, sys; print(json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))')
# Without HTTPS enabled for the tailnet this waits for it to be switched on; do not hang the deploy on that.
if ! tailscale serve status 2>/dev/null | grep -q 'http://127.0.0.1:7443' \
  && ! timeout 60 tailscale serve --bg --https=443 http://127.0.0.1:7443; then
  echo "tailscale serve failed: enable MagicDNS and HTTPS certificates in the Tailscale admin console (DNS page), then deploy again." >&2
  exit 1
fi

# First install: the sample config, with the API listener behind Tailscale Serve. Afterwards the config is yours.
if [ ! -f "$home/.harness/config.yaml" ]; then
  as_harness harness init >/dev/null
  as_harness python3 - "$home/.harness/config.yaml" "$fqdn" <<'PY'
import re, sys
path, fqdn = sys.argv[1], sys.argv[2]
text = open(path).read()
block = f"listeners:\n  api: http://127.0.0.1:7443\n  publicHost: {fqdn}   # passkeys are bound to this name\n  behindProxy: true\n"
text, n = re.subn(r"^listeners:\n(?:[ \t]+#.*\n)*", block, text, count=1, flags=re.M)
if n == 0:
    text += "\n" + block
open(path, "w").write(text)
PY
fi

cat > /etc/systemd/system/harness.service <<UNIT
[Unit]
Description=harness coding agent daemon
After=network-online.target tailscaled.service
Wants=network-online.target

[Service]
User=harness
Group=harness
WorkingDirectory=$home
Environment=HOME=$home DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=/opt/harness/harness serve
Restart=on-failure
RestartSec=5
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable harness >/dev/null
systemctl restart harness

for _ in $(seq 30); do
  as_harness harness status >/dev/null 2>&1 && break
  sleep 1
done
if ! as_harness harness status >/dev/null 2>&1; then
  journalctl -u harness -n 30 --no-pager >&2
  exit 1
fi

echo "harness $(as_harness harness --version 2>/dev/null || true) is running at https://$fqdn/"
echo "HARNESS_DEPLOY_OK"
