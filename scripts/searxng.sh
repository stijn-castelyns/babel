#!/usr/bin/env bash
# Runs a private SearXNG instance for the harness's web_search tool and points config.yaml at it.
#
#   scripts/searxng.sh [up]      write settings, (re)start the container, check it answers JSON, set tools.web.searxng
#   scripts/searxng.sh test      run a query against the instance
#   scripts/searxng.sh down      stop and remove the container (settings stay)
#
# Options: --port N (default 8888)  --engine docker|podman  --image ref  --no-config (leave config.yaml alone)
#
# The instance listens on 127.0.0.1 only, has the JSON format on (web_search uses it) and the bot limiter off (only the
# harness talks to it, and the limiter would refuse it). Settings live in $HARNESS_HOME/searxng/settings.yml
# (~/.harness by default); edit them and run `up` again to apply.
set -euo pipefail

port=8888
engine=""
image="docker.io/searxng/searxng:latest"
name="harness-searxng"
write_config=true
command="up"

while [ $# -gt 0 ]; do
    case "$1" in
        up | test | down) command="$1" ;;
        --port) port="$2"; shift ;;
        --engine) engine="$2"; shift ;;
        --image) image="$2"; shift ;;
        --no-config) write_config=false ;;
        -h | --help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown argument: $1 (see --help)" >&2; exit 2 ;;
    esac
    shift
done

home="${HARNESS_HOME:-$HOME/.harness}"
dir="$home/searxng"
url="http://127.0.0.1:$port"

# The same choice the container sandbox makes: podman when it is installed, else docker.
if [ -z "$engine" ]; then
    if command -v podman >/dev/null 2>&1; then engine=podman
    elif command -v docker >/dev/null 2>&1; then engine=docker
    else echo "Neither podman nor docker is installed; install one (or pass --engine)." >&2; exit 1
    fi
fi

check() {
    local body count
    if ! body=$(curl -fsS --max-time 30 "$url/search?q=harness+agent&format=json"); then
        echo "SearXNG at $url did not answer the JSON search; see: $engine logs $name" >&2
        return 1
    fi
    count=$(printf '%s' "$body" | grep -o '"url": *"' | wc -l | tr -d ' ')
    if [ "$count" -eq 0 ]; then
        echo "SearXNG answers, but the test query found nothing; its engines may be blocked from this machine ($engine logs $name)." >&2
        return 1
    fi
    echo "SearXNG at $url answered a test query with $count results."
}

case "$command" in
    down)
        "$engine" rm -f "$name" >/dev/null 2>&1 && echo "Removed the $name container; settings stay in $dir." \
            || echo "No $name container."
        exit 0
        ;;
    test)
        check
        exit $?
        ;;
esac

# Settings: SearXNG's defaults plus what the harness needs. The secret key survives reruns.
mkdir -p "$dir"
secret=""
if [ -f "$dir/settings.yml" ]; then
    secret=$(sed -n 's/^ *secret_key: *"\{0,1\}\([A-Za-z0-9]*\)"\{0,1\} *$/\1/p' "$dir/settings.yml" | head -n 1)
fi
if [ -z "$secret" ]; then
    secret=$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')
fi
cat >"$dir/settings.yml" <<EOF
# Written by scripts/searxng.sh for the harness's web_search tool; rerunning it rewrites this file (keeping the key).
# See https://docs.searxng.org/admin/settings/ for everything else.
use_default_settings: true
server:
  secret_key: "$secret"
  limiter: false          # only the harness uses this instance; the limiter would answer it with 429
  public_instance: false
  image_proxy: false
search:
  formats: [html, json]   # web_search reads the JSON format
  safe_search: 0
EOF
chmod 600 "$dir/settings.yml"

echo "Starting $image as $name on $url ($engine)…"
"$engine" pull -q "$image" >/dev/null
"$engine" rm -f "$name" >/dev/null 2>&1 || true
# Only the file is mounted, read-only: given the whole folder, the image would chown it to its own user on the host.
"$engine" run -d --name "$name" --restart unless-stopped \
    -p "127.0.0.1:$port:8080" \
    -v "$dir/settings.yml:/etc/searxng/settings.yml:ro" \
    -v "$name-cache:/var/cache/searxng" \
    -e FORCE_OWNERSHIP=false \
    -e "SEARXNG_BASE_URL=$url/" \
    "$image" >/dev/null

for _ in $(seq 1 60); do
    curl -fsS --max-time 2 "$url/healthz" >/dev/null 2>&1 && break
    sleep 1
done
if ! curl -fsS --max-time 2 "$url/healthz" >/dev/null 2>&1; then
    echo "SearXNG did not come up within a minute. Its log:" >&2
    "$engine" logs --tail 30 "$name" >&2
    exit 1
fi
check || true

if [ "$engine" = podman ]; then
    echo "Podman restarts the container at boot only with: systemctl --user enable --now podman-restart.service"
fi

# Point the harness at it. Strict YAML forbids a second top-level key, so an existing tools: section is left to you.
config="$home/config.yaml"
snippet="tools:
  web:
    searxng: $url"
if ! $write_config; then
    echo "Left config.yaml alone. web_search uses this instance with:"
    echo "$snippet"
elif [ ! -f "$config" ]; then
    echo "No $config yet (run 'harness init'); then add:"
    echo "$snippet"
elif grep -Eq "^[[:space:]]+searxng:[[:space:]]*$url/?[[:space:]]*(#.*)?$" "$config"; then
    echo "$config already points web_search at $url."
elif grep -Eq '^tools:' "$config"; then
    echo "$config has a tools: section; add this under it (replacing any other searxng: line):"
    echo "  web:"
    echo "    searxng: $url"
else
    printf '\n# SearXNG for web_search, set up by scripts/searxng.sh.\n%s\n' "$snippet" >>"$config"
    echo "Added tools.web.searxng: $url to $config; the next run uses it."
fi
echo "Agents get the tool by listing web_search (and web_fetch) under tools.builtin."
