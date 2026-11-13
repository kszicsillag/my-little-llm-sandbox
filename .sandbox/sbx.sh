#!/usr/bin/env bash
# sbx.sh: devcontainer-style workflow for ACA Sandboxes.
#
#   sbx.sh image                         build the base disk image from .sandbox/image
#   sbx.sh up <standard|hardened|hardened-apikey> <repo-url> [name]
#                                        create volume (once) + sandbox, run bootstrap
#   sbx.sh shell <name>                  interactive shell
#   sbx.sh exec <name> <command...>      run a command
#   sbx.sh code <name>                   start a VS Code Remote Tunnel (connect from desktop VS Code)
#   sbx.sh ls                            list sandboxes created by sbx.sh
#   sbx.sh down <name>                   delete the sandbox, KEEP the volume ("rebuild")
#   sbx.sh purge <name>                  delete the sandbox AND its volume
#   sbx.sh secrets                       set github / anthropic secrets for the hardened profile
#
# Requires: aca CLI (configured: `aca config sandbox set --group <group>`), python, envsubst.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ACA="${ACA:-aca}"
command -v "$ACA" >/dev/null 2>&1 || ACA="$HOME/.aca/bin/aca"
PY="$(command -v python3 || command -v python)"

# Sizing. Tier disk budget = root disk + volumes (S: 0.5 vCPU / 1 GiB / 10 GiB total).
export SBX_IMAGE="${SBX_IMAGE:-agent-base-v3}"
export SBX_CPU="${SBX_CPU:-500m}"
export SBX_MEMORY="${SBX_MEMORY:-1024Mi}"
export SBX_ROOT_DISK="${SBX_ROOT_DISK:-5Gi}"
SBX_VOLUME_SIZE="${SBX_VOLUME_SIZE:-5Gi}"

die() { echo "sbx: $*" >&2; exit 1; }

json_pick() {  # <python expression over `d`> ; reads JSON from stdin
  "$PY" -c "import json,sys; d=json.load(sys.stdin); r=($1); print(r if r is not None else '')"
}

disk_id() {
  "$ACA" sandboxgroup disk list -o json \
    | json_pick "next((x['id'] for x in (d if isinstance(d,list) else d.get('value',[])) if x.get('name')=='$SBX_IMAGE' and (x.get('status') or {}).get('state', x.get('state'))=='Ready'), None)"
}

sandbox_id() {  # <name>
  "$ACA" sandbox list -o json \
    | json_pick "next((x['id'] for x in (d if isinstance(d,list) else d.get('value',[])) if (x.get('labels') or {}).get('sbx')=='$1'), None)"
}

require_sandbox() { local id; id="$(sandbox_id "$1")"; [ -n "$id" ] || die "no sandbox named '$1'"; echo "$id"; }

cmd_image() {
  "$ACA" sandboxgroup disk create --source "$HERE/image" --name "$SBX_IMAGE"
}

cmd_up() {
  local profile="${1:?profile}" repo="${2:?repo-url}" name="${3:-}"
  [ -f "$HERE/$profile.yaml" ] || die "unknown profile '$profile'"
  [ -n "$name" ] || name="$(basename "${repo%.git}")"
  [ -z "$(sandbox_id "$name")" ] || die "sandbox '$name' already exists (DataDisk volumes mount to one sandbox at a time)"

  export SBX_NAME="$name" SBX_VOLUME="persist-$name"
  SBX_DISK_ID="$(disk_id)"; export SBX_DISK_ID
  [ -n "$SBX_DISK_ID" ] || die "disk image '$SBX_IMAGE' not found or not Ready; run: sbx.sh image"

  if ! "$ACA" sandboxgroup volume get --name "$SBX_VOLUME" >/dev/null 2>&1; then
    echo "creating DataDisk volume $SBX_VOLUME ($SBX_VOLUME_SIZE)"
    "$ACA" sandboxgroup volume create --name "$SBX_VOLUME" --type DataDisk --size "$SBX_VOLUME_SIZE" >/dev/null
  fi

  local manifest; manifest="$(mktemp --suffix=.yaml)"
  envsubst '${SBX_DISK_ID} ${SBX_CPU} ${SBX_MEMORY} ${SBX_ROOT_DISK} ${SBX_NAME} ${SBX_VOLUME}' \
    <"$HERE/$profile.yaml" >"$manifest"
  "$ACA" sandbox apply --file "$manifest"
  rm -f "$manifest"

  local id; id="$(require_sandbox "$name")"
  # hardened-* variants differ only in egress/auth; bootstrap is the same.
  "$ACA" sandbox exec --id "$id" -c "sandbox-bootstrap ${profile%%-*} '$repo'"
  echo "ready: sbx.sh shell $name"
}

cmd_code() {  # <name>
  local name="${1:?name}" id tunnel
  id="$(require_sandbox "$name")"
  tunnel="$(printf 'sbx-%s' "$name" | tr 'A-Z_.' 'a-z--' | cut -c1-20)"   # tunnel names: <=20 chars
  # Tunnel traffic does not count as sandbox activity; give idle suspend more room while coding.
  "$ACA" sandbox lifecycle set --id "$id" --auto-suspend enable --idle-timeout-seconds 3600 --mode Disk >/dev/null
  if ! "$ACA" sandbox exec --id "$id" -c 'code tunnel user show >/dev/null 2>&1'; then
    echo "VS Code tunnel is not logged in yet. Run once (device code login, Microsoft account):"
    echo "  sbx.sh shell $name   then:   code tunnel user login --provider microsoft"
    echo "(use the Microsoft provider: GitHub login conflicts with the hardened profile's github.com header injection)"
    exit 1
  fi
  "$ACA" sandbox exec --id "$id" -c "pgrep -f 'code tunnel' >/dev/null || (nohup code tunnel --accept-server-license-terms --name $tunnel >/tmp/code-tunnel.log 2>&1 &); sleep 5; tail -3 /tmp/code-tunnel.log"
  echo "connect: VS Code > Remote Explorer > Tunnels > $tunnel   (or https://vscode.dev/tunnel/$tunnel)"
}

cmd_secrets() {
  local tok oauth key
  read -r -s -p "GitHub fine-grained token (empty = skip): " tok; echo
  if [ -n "$tok" ]; then
    printf 'token=%s\nbasic=%s\n' "$tok" "$(printf 'x-access-token:%s' "$tok" | base64 | tr -d '\n')" \
      | "$ACA" sandboxgroup secret set --name github --from-stdin
  fi
  # hardened (subscription): token from `claude setup-token` on a machine logged in to Pro/Max.
  read -r -s -p "Claude subscription token from 'claude setup-token' (empty = skip): " oauth; echo
  if [ -n "$oauth" ]; then
    printf 'oauth=%s\n' "$oauth" | "$ACA" sandboxgroup secret set --name anthropic --from-stdin
  fi
  # hardened-apikey: Console API key, billed per token, separate from the subscription.
  read -r -s -p "Anthropic API key, pay-as-you-go (empty = skip): " key; echo
  if [ -n "$key" ]; then
    printf 'key=%s\n' "$key" | "$ACA" sandboxgroup secret set --name anthropic --from-stdin
  fi
}

case "${1:-}" in
  image)   cmd_image ;;
  up)      shift; cmd_up "$@" ;;
  shell)   "$ACA" sandbox shell --id "$(require_sandbox "${2:?name}")" ;;
  code)    cmd_code "${2:-}" ;;
  exec)    n="${2:?name}"; shift 2; "$ACA" sandbox exec --id "$(require_sandbox "$n")" -c "$*" ;;
  ls)      "$ACA" sandbox list ;;
  down)    "$ACA" sandbox delete --id "$(require_sandbox "${2:?name}")" --yes ;;
  purge)   n="${2:?name}"; id="$(sandbox_id "$n")"
           [ -z "$id" ] || "$ACA" sandbox delete --id "$id" --yes
           "$ACA" sandboxgroup volume delete --name "persist-$n" ;;
  secrets) cmd_secrets ;;
  *)       sed -n '2,15p' "$0"; exit 2 ;;
esac
