#!/usr/bin/env bash
# sbx.sh: builds the sandbox disk images. Everything else (up, shell, code, exec, egress, preview, ...)
# is the Sandman CLI `sbx` (../sandman), on the ACA Sandboxes .NET SDK.
#
#   sbx.sh image [stack]                 build the base disk image, or base + image/stacks/<stack>.Dockerfile
#
# `image` stays here because the SDK can only create disk images from a blob or a registry, not from a
# local build context like `aca sandboxgroup disk create --source <dir>`.
#
# Requires: aca CLI (configured: `aca config sandbox set --group <group>`).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

die() { echo "sbx: $*" >&2; exit 1; }

case "${1:-}" in
  image) ;;
  '' | -h | --help) sed -n '2,10p' "$0"; exit 2 ;;
  *)
    echo "sbx.sh: '$1' moved to the Sandman CLI. Run: sbx $*" >&2
    command -v sbx >/dev/null 2>&1 || command -v sbx.exe >/dev/null 2>&1 \
      || echo "  (sbx is not on PATH; install it as described in sandman/README.md)" >&2
    exit 2 ;;
esac

ACA="${ACA:-aca}"
command -v "$ACA" >/dev/null 2>&1 || ACA="$HOME/.aca/bin/aca"
if ! command -v "$ACA" >/dev/null 2>&1; then
  # From PowerShell, plain `bash` is usually WSL (System32\bash.exe), which doesn't see the Windows aca.exe.
  grep -qi microsoft /proc/version 2>/dev/null \
    && echo "sbx: running under WSL; use Git Bash instead: & \"C:\\Program Files\\Git\\bin\\bash.exe\" $0 ${*:-}" >&2
  die "aca CLI not found (tried 'aca' and $HOME/.aca/bin/aca); set ACA=/path/to/aca"
fi

SBX_IMAGE="${SBX_IMAGE:-agent-base-v3}"

cmd_image() {  # [stack]: no stack = base image; a stack appends image/stacks/<stack>.Dockerfile
  local stack="${1:-}" ctx
  if [ -z "$stack" ]; then
    "$ACA" sandboxgroup disk create --source "$HERE/image" --name "$SBX_IMAGE"
    return
  fi
  [ -f "$HERE/image/stacks/$stack.Dockerfile" ] || die "unknown stack '$stack' (see image/stacks/)"
  ctx="$(mktemp -d)"
  trap 'rm -rf "$ctx"' EXIT
  cp "$HERE/image/Dockerfile" "$HERE/image/bootstrap.sh" "$ctx/"
  printf '\n' >>"$ctx/Dockerfile"
  cat "$HERE/image/stacks/$stack.Dockerfile" >>"$ctx/Dockerfile"
  echo "building $SBX_IMAGE = base + stack '$stack'"
  "$ACA" sandboxgroup disk create --source "$ctx" --name "$SBX_IMAGE"
}

shift
cmd_image "$@"
