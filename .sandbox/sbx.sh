#!/usr/bin/env bash
# sbx.sh: devcontainer-style workflow for ACA Sandboxes.
#
#   sbx.sh image [stack]                 build the base disk image, or base + image/stacks/<stack>.Dockerfile
#   sbx.sh up <standard|hardened|hardened-apikey> <repo-url> [name]
#                                        create volume (once) + sandbox, run bootstrap
#   sbx.sh shell <name>                  interactive shell
#   sbx.sh exec <name> <command...>      run a command
#   sbx.sh code <name>                   start a VS Code Remote Tunnel (connect from desktop VS Code)
#   sbx.sh allow-vscode <name>           add the VS Code tunnel hosts to a running hardened sandbox (no rebuild)
#   sbx.sh allow-all <name> [ttl]        hardened: switch egress default to Allow (rules still apply first); ttl e.g. 30m, 2h
#   sbx.sh lockdown <name>               switch egress default back to Deny
#   sbx.sh learned <name>                hosts reached only via the default Allow (candidates for SBX_ALLOW)
#   sbx.sh preview <name> [port|from-to] [email]
#                                        expose port(s) via sandbox ingress, Entra auth (default 8000, az login email)
#   sbx.sh ls                            list sandboxes created by sbx.sh
#   sbx.sh down <name>                   delete the sandbox, KEEP the volume ("rebuild")
#   sbx.sh purge <name>                  delete the sandbox AND its volume
#   sbx.sh secrets                       set github / anthropic secrets for the hardened profile
#   (shell, code, allow-*, lockdown, learned, preview: <name> may be omitted if exactly one sbx sandbox is running)
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

cmd_image() {  # [stack]: no stack = base image; a stack appends image/stacks/<stack>.Dockerfile
  local stack="${1:-}" ctx
  if [ -z "$stack" ]; then
    "$ACA" sandboxgroup disk create --source "$HERE/image" --name "$SBX_IMAGE"
    return
  fi
  [ -f "$HERE/image/stacks/$stack.Dockerfile" ] || die "unknown stack '$stack' (see image/stacks/)"
  ctx="$(mktemp -d)"
  cp "$HERE/image/Dockerfile" "$HERE/image/bootstrap.sh" "$ctx/"
  printf '\n' >>"$ctx/Dockerfile"
  cat "$HERE/image/stacks/$stack.Dockerfile" >>"$ctx/Dockerfile"
  echo "building $SBX_IMAGE = base + stack '$stack'"
  "$ACA" sandboxgroup disk create --source "$ctx" --name "$SBX_IMAGE"
  rm -rf "$ctx"
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
  # SBX_VSCODE=1: enable the commented-out VS Code tunnel allowlist block (hardened profiles).
  if [ "${SBX_VSCODE:-0}" = 1 ]; then
    sed -i -E 's/^(\s*)# (- \{ name: (vscode-|ms-login))/\1\2/' "$manifest"
    echo "SBX_VSCODE=1: VS Code tunnel hosts allowed"
  fi
  # SBX_ALLOW="host1,*.host2,host3/path/*": extra Allow rules for this sandbox (hardened profiles),
  # appended after the profile's own rules (first match wins, so injection/deny rules apply first).
  # A "/path" part restricts the rule to that path and to GET/HEAD: use it for shared hosts such
  # as storage.googleapis.com, where a host-wide allow would open every bucket for upload.
  if [ -n "${SBX_ALLOW:-}" ]; then
    grep -q '^  rules:' "$manifest" || die "SBX_ALLOW needs a profile with egress rules (hardened*)"
    local h i=0
    for h in ${SBX_ALLOW//,/ }; do
      i=$((i+1))
      if [[ "$h" == */* ]]; then
        printf '    - { name: extra-%d, match: { host: "%s", path: "/%s", methods: [GET, HEAD] }, action: { type: Allow } }\n' \
          "$i" "${h%%/*}" "${h#*/}" >>"$manifest"
      else
        printf '    - { name: extra-%d, match: { host: "%s" }, action: { type: Allow } }\n' "$i" "$h" >>"$manifest"
      fi
    done
    echo "SBX_ALLOW: $i extra rule(s) allowed"
  fi
  "$ACA" sandbox apply --file "$manifest"
  rm -f "$manifest"

  local id; id="$(require_sandbox "$name")"
  # hardened-* variants differ only in egress/auth; bootstrap is the same.
  "$ACA" sandbox exec --id "$id" -c "sandbox-bootstrap ${profile%%-*} '$repo'"
  echo "ready: sbx.sh shell $name"
}

cmd_code() {  # <name>
  local name="${1:?name}" id tunnel ws
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
  # PID file, not pgrep -f: the exec'd shell's own command line would match the pattern.
  "$ACA" sandbox exec --id "$id" -c "if [ -f /tmp/code-tunnel.pid ] && kill -0 \$(cat /tmp/code-tunnel.pid) 2>/dev/null; then echo 'tunnel already running'; else nohup code tunnel --accept-server-license-terms --name $tunnel >/tmp/code-tunnel.log 2>&1 & echo \$! >/tmp/code-tunnel.pid; sleep 8; fi; tail -4 /tmp/code-tunnel.log"
  # Open the real path, not the ~/workspace symlink: Workspace Trust is per opened path (stored in
  # your desktop VS Code), so trusting /persist/workspace once covers every repo and every rebuild.
  ws="$("$ACA" sandbox exec --id "$id" -c 'readlink -f ~/workspace 2>/dev/null' | tr -d '\r' | tail -1)"
  echo "connect: VS Code > Remote Explorer > Tunnels > $tunnel"
  if [ -n "$ws" ]; then
    echo "  or:    code --folder-uri vscode-remote://tunnel+$tunnel$ws"
    echo "  or:    https://vscode.dev/tunnel/$tunnel$ws"
    echo "trust:   on the first prompt choose 'Trust the authors of all files in the parent folder' ($(dirname "$ws"))"
  else
    echo "  or:    https://vscode.dev/tunnel/$tunnel"
  fi
}

# --- live egress policy edits (export -> edit YAML -> apply; no rebuild) ---------------------
add_rules() {  # <rule lines>; stdin: exported policy. Appends to the top-level rules list,
               # i.e. before the next top-level key (trafficInspection), so profile rules match first.
  awk -v add="$1" 'inrules && !done && /^[A-Za-z]/ {print add; done=1} /^rules:/ {inrules=1} {print} END {if (!done) print add}'
}

drop_allow_all_marker() {  # stdin: exported policy (block-style YAML); removes allow-all-* marker rules
  awk '/^- .*name: allow-all-/ {skip=1; next} skip && /^(- |[A-Za-z])/ {skip=0} !skip'
}

to_seconds() {  # 90 | 90s | 30m | 2h
  case "$1" in
    *h) echo $(( ${1%h} * 3600 )) ;; *m) echo $(( ${1%m} * 60 )) ;; *s) echo "${1%s}" ;;
    *[!0-9]*|'') die "bad duration '$1' (use e.g. 30m, 2h)" ;; *) echo "$1" ;;
  esac
}

timer_pidfile() { echo "${TMPDIR:-/tmp}/sbx-allow-all-$1.pid"; }

cancel_timer() {  # <name>: stop a pending auto-lockdown from an earlier allow-all
  local f; f="$(timer_pidfile "$1")"
  [ -f "$f" ] && kill "$(cat "$f")" 2>/dev/null || true
  rm -f "$f"
}

egress_default() {  # <id> -> Allow | Deny
  "$ACA" sandbox get --id "$1" -o json | json_pick "(d.get('egressPolicy') or {}).get('defaultAction')"
}

cmd_allow_all() {  # <name> [ttl]
  local name="${1:?name}" ttl="${2:-}" id policy secs=0 now deadline=0
  id="$(require_sandbox "$name")"
  [ -z "$ttl" ] || secs="$(to_seconds "$ttl")"
  now="$(date +%s)"; [ "$secs" -eq 0 ] || deadline=$((now + secs))
  policy="$(mktemp --suffix=.yaml)"
  "$ACA" sandbox egress export --id "$id" >"$policy"
  grep -q '^rules:' "$policy" || { rm -f "$policy"; die "'$name' has no egress rules (standard profile allows all egress already)"; }
  # Marker rule on a reserved .invalid host (never resolves): records when it opened and until
  # when, inside the policy itself. It matches no real traffic.
  sed 's/^defaultAction: Deny/defaultAction: Allow/' "$policy" | drop_allow_all_marker \
    | add_rules "- { name: allow-all-$now-$deadline, match: { host: \"allow-all.marker.invalid\" }, action: { type: Allow } }" \
    >"$policy.new"
  "$ACA" sandbox egress apply --id "$id" --file "$policy.new" >/dev/null
  rm -f "$policy" "$policy.new"

  cancel_timer "$name"
  if [ "$secs" -gt 0 ]; then
    # Local timer: runs while this PC is on. If it dies (reboot, logoff), the sandbox stays open
    # until you run `sbx.sh lockdown`; `sbx.sh shell` warns about it.
    nohup bash -c "sleep $secs; exec '$HERE/sbx.sh' lockdown '$name'" >/dev/null 2>&1 &
    echo $! >"$(timer_pidfile "$name")"
    echo "egress OPEN for '$name' until $(date -d "@$deadline" '+%H:%M') (auto-lockdown by a local timer)"
  else
    echo "egress OPEN for '$name' until: sbx.sh lockdown $name"
  fi
  echo "still enforced: token injection (github/anthropic), block-repo-delete. NOT enforced: data exfiltration, untrusted downloads."
  echo "afterwards: sbx.sh learned $name"
}

cmd_lockdown() {  # <name>
  local name="${1:?name}" id policy
  id="$(require_sandbox "$name")"
  cancel_timer "$name"
  policy="$(mktemp --suffix=.yaml)"
  "$ACA" sandbox egress export --id "$id" >"$policy"
  if ! grep -q '^defaultAction: Allow' "$policy" && ! grep -q 'name: allow-all-' "$policy"; then
    rm -f "$policy"; echo "'$name' is already locked down"; return
  fi
  grep -q '^rules:' "$policy" || { rm -f "$policy"; die "'$name' has no egress rules (standard profile): not touching it"; }
  sed 's/^defaultAction: Allow/defaultAction: Deny/' "$policy" | drop_allow_all_marker >"$policy.new"
  "$ACA" sandbox egress apply --id "$id" --file "$policy.new" >/dev/null
  rm -f "$policy" "$policy.new"
  [ "$(egress_default "$id")" = Deny ] || die "lockdown did not take effect, check: aca sandbox egress show --id $id"
  echo "egress locked down for '$name' (default Deny)"
}

cmd_learned() {  # <name>: allowed requests that no named rule matched, i.e. let through by default Allow
  local name="${1:?name}" id names
  id="$(require_sandbox "$name")"
  names="$("$ACA" sandbox get --id "$id" -o json \
    | json_pick "','.join(r['name'] for r in (d.get('egressPolicy') or {}).get('rules', []) if not r['name'].startswith('allow-all-'))")"
  "$ACA" sandbox egress decisions --id "$id" -o json | "$PY" -c '
import json, sys
names = set(sys.argv[1].split(","))
d = json.load(sys.stdin).get("networkEgress", {})
hosts = {}
for x in d.get("allowed", []):
    if x.get("matchedRule") in names:
        continue  # covered by a profile / SBX_ALLOW rule already
    h = hosts.setdefault(x["host"], {"n": 0, "methods": set(), "paths": set(), "last": ""})
    h["n"] += 1; h["methods"].add(x.get("method", "?")); h["paths"].add(x.get("path", "")[:60])
    h["last"] = max(h["last"], x.get("timestamp", ""))
if not hosts:
    print("no requests outside the named rules in the decisions log"); sys.exit(0)
row = "%-45s %5s  %-12s %-9s  %s"
print(row % ("host", "hits", "methods", "last seen", "sample path"))
for host, h in sorted(hosts.items(), key=lambda kv: -kv[1]["n"]):
    print(row % (host, h["n"], ",".join(sorted(h["methods"])), h["last"][11:19], sorted(h["paths"])[0]))
print("\nkeep what you need: SBX_ALLOW=host1,host2/path/* on the next `up` (path = GET/HEAD only)")
' "$names"
}

cmd_allow_vscode() {  # <name>: same rules as SBX_VSCODE=1, patched into the live egress policy
  local name="${1:?name}" id policy rules
  id="$(require_sandbox "$name")"
  policy="$(mktemp --suffix=.yaml)"
  "$ACA" sandbox egress export --id "$id" >"$policy"
  grep -q '^rules:' "$policy" || { rm -f "$policy"; die "'$name' has no egress rules (standard profile allows all egress already)"; }
  # The commented-out block in hardened.yaml is the single source of the host list.
  # Add only the rules the live policy lacks, so re-running picks up hosts added later.
  local r n; rules=""
  while IFS= read -r r; do
    n="$(sed -E 's/^- \{ name: ([^,]+),.*/\1/' <<<"$r")"
    grep -q "name: $n\$" "$policy" || rules+="${rules:+$'\n'}$r"
  done < <(sed -n -E 's/^\s*# (- \{ name: (vscode-|ms-login).*)$/\1/p' "$HERE/hardened.yaml")
  [ -n "$rules" ] || { rm -f "$policy"; echo "VS Code tunnel hosts already allowed"; return; }
  echo "adding: $(sed -E 's/^- \{ name: ([^,]+),.*/\1/' <<<"$rules" | tr '\n' ' ')"
  add_rules "$rules" <"$policy" >"$policy.new"
  "$ACA" sandbox egress apply --id "$id" --file "$policy.new" >/dev/null
  rm -f "$policy" "$policy.new"
  echo "VS Code tunnel hosts allowed for '$name'; next: sbx.sh shell $name -> code tunnel user login --provider microsoft"
}

cmd_preview() {  # <name> [port|from-to] [email]
  local name="${1:?name}" port="${2:-8000}" email="${3:-}" id
  if [[ "$port" =~ ^([0-9]+)-([0-9]+)$ ]]; then
    local p from="${BASH_REMATCH[1]}" to="${BASH_REMATCH[2]}"
    [ "$from" -le "$to" ] && [ $((to - from)) -lt 20 ] || die "bad port range '$port' (from<=to, at most 20 ports)"
    for ((p = from; p <= to; p++)); do cmd_preview "$name" "$p" "$email" || return; done
    return
  fi
  [[ "$port" =~ ^[0-9]+$ ]] || die "bad port '$port'"
  id="$(require_sandbox "$name")"
  # Default: the signed-in Azure CLI user (the Entra identity that manages this sandbox group).
  [ -n "$email" ] || email="$(az account show --query user.name -o tsv 2>/dev/null)"
  [ -n "$email" ] || die "no email given and none from 'az account show'"
  # Never anonymous: restrict the ingress port to one Entra identity in this tenant.
  # `aca sandbox port add --email` omits auth.entraId.tenantIds, which the API requires,
  # so call the data plane directly (same endpoint the CLI uses).
  local cfg sub rg group region tenant
  cfg="$("$ACA" config show -o json)"
  sub="${ACA_SUBSCRIPTION:-$(json_pick "d['sandbox'].get('subscription')" <<<"$cfg")}"
  rg="${ACA_RESOURCE_GROUP:-$(json_pick "d['sandbox'].get('resource_group')" <<<"$cfg")}"
  group="${ACA_SANDBOX_GROUP:-$(json_pick "d['sandbox'].get('group')" <<<"$cfg")}"
  region="${ACA_REGION:-$(json_pick "d['sandbox'].get('region')" <<<"$cfg")}"
  tenant="$(az account show --query tenantId -o tsv)"
  [ -n "$sub" ] && [ -n "$rg" ] && [ -n "$group" ] && [ -n "$region" ] \
    || die "could not resolve subscription / resource group / sandbox group / region from 'aca config show'"

  "$ACA" sandbox port remove --id "$id" --port "$port" >/dev/null 2>&1 || true
  MSYS_NO_PATHCONV=1 az rest --method post --resource https://management.azuredevcompute.io \
    --url "https://management.$region.azuredevcompute.io/subscriptions/$sub/resourceGroups/$rg/sandboxGroups/$group/sandboxes/$id/ports/add?api-version=2026-02-01-preview" \
    --body "{\"port\":$port,\"auth\":{\"entraId\":{\"enabled\":true,\"emails\":[\"$email\"],\"tenantIds\":[\"$tenant\"]}}}" -o json \
    | json_pick "next((p['url'] for p in (d if isinstance(d,list) else d.get('ports',[d])) if p.get('port')==$port), None)"
  echo "(Entra ID auth: $email, tenant $tenant)"
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

# Missing arguments: print that command's line from the header instead of bash's terse "1: name".
usage() { grep -E "^#   sbx.sh $1( |$)" "$0" | sed 's/^#   /usage: /' >&2; exit 2; }
running_sbx() {  # names of running sandboxes created by sbx.sh, one per line
  "$ACA" sandbox list -o json | "$PY" -c '
import json, sys
d = json.load(sys.stdin); d = d if isinstance(d, list) else d.get("value", [])
for x in d:
    n = (x.get("labels") or {}).get("sbx"); s = x.get("state") or (x.get("status") or {}).get("state")
    if n and s == "Running": print(n)'
}

case "${1:-}" in
  up|exec) [ $# -ge 3 ] || usage "$1" ;;
  down|purge) [ -n "${2:-}" ] || usage "$1" ;;   # destructive: never guess the target
  # Name optional when exactly one sbx sandbox is running. A ttl/port in its place (allow-all 30m,
  # preview 8000) also means "no name given".
  shell|code|allow-vscode|allow-all|lockdown|learned|preview)
    if [ -z "${2:-}" ] || [[ "$2" =~ ^[0-9]+([smh]|-[0-9]+)?$ ]]; then
      mapfile -t run < <(running_sbx | tr -d '\r')
      if [ "${#run[@]}" -ne 1 ]; then
        echo "name required: ${#run[@]} sbx sandboxes running${run[*]:+ (${run[*]})}" >&2; usage "$1"
      fi
      echo "(using the only running sandbox: ${run[0]})" >&2
      set -- "$1" "${run[0]}" "${@:2}"
    fi ;;
esac

case "${1:-}" in
  image)   shift; cmd_image "$@" ;;
  up)      shift; cmd_up "$@" ;;
  # `aca sandbox shell` opens as root while `exec` runs as the image USER (dev): switch to dev so
  # logins (code tunnel, gh, ...) land in /home/dev. -E keeps the manifest env, PATH dodges secure_path.
  shell)   id="$(require_sandbox "${2:?name}")"
           [ "$(egress_default "$id")" != Allow ] || echo "WARNING: egress is OPEN (allow-all) for '$2'; close with: sbx.sh lockdown $2" >&2
           "$ACA" sandbox shell --id "$id" \
             -c 'sudo -E -u dev env HOME=/home/dev PATH="$PATH" bash -l' ;;
  code)    cmd_code "${2:-}" ;;
  allow-vscode) cmd_allow_vscode "${2:-}" ;;
  allow-all) shift; cmd_allow_all "$@" ;;
  lockdown)  cmd_lockdown "${2:-}" ;;
  learned)   cmd_learned "${2:-}" ;;
  preview) shift; cmd_preview "$@" ;;
  exec)    n="${2:?name}"; shift 2; "$ACA" sandbox exec --id "$(require_sandbox "$n")" -c "$*" ;;
  ls)      "$ACA" sandbox list ;;
  down)    "$ACA" sandbox delete --id "$(require_sandbox "${2:?name}")" --yes ;;
  purge)   n="${2:?name}"; id="$(sandbox_id "$n")"
           [ -z "$id" ] || "$ACA" sandbox delete --id "$id" --yes
           "$ACA" sandboxgroup volume delete --name "persist-$n" ;;
  secrets) cmd_secrets ;;
  *)       sed -n '2,21p' "$0"; exit 2 ;;
esac
