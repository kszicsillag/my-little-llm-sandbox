#!/usr/bin/env bash
# sandbox-bootstrap: the "postCreateCommand" of an ACA sandbox. Idempotent.
#
#   sandbox-bootstrap <standard|hardened> [repo-url]
#
# - Repairs ownership of the persistent volume (/persist, a DataDisk volume).
# - Links developer state from /persist into $HOME so it survives sandbox deletion.
#   standard: also persists agent + gh login state (~/.claude, ~/.config/gh).
#   hardened: never persists tokens; credentials are injected by the egress proxy.
# - Clones the repo into /persist/workspace/<name> (once) and links ~/workspace.
set -euo pipefail

PROFILE="${1:?usage: sandbox-bootstrap <standard|hardened> [repo-url]}"
REPO_URL="${2:-}"
PERSIST="${PERSIST:-/persist}"
ME="$(id -un)"

if [ ! -d "$PERSIST" ]; then
  echo "no volume at $PERSIST; state will not survive sandbox deletion" >&2
  sudo mkdir -p "$PERSIST"
fi

# 1. Ownership repair (fresh volumes are root-owned).
[ "$(stat -c %U "$PERSIST")" = "$ME" ] || sudo chown "$ME:$ME" "$PERSIST"

# 2. Persist state: path in $HOME -> directory on the volume.
link_state() {  # <path relative to $HOME>
  local rel="$1" target="$PERSIST/home/$1" src="$HOME/$1"
  mkdir -p "$target" "$(dirname "$src")"
  if [ -L "$src" ]; then return; fi
  if [ -d "$src" ]; then cp -a "$src/." "$target/" && rm -rf "$src"; fi
  ln -s "$target" "$src"
}

link_state .config/git
link_state .npm
link_state .cache/pip
mkdir -p "$PERSIST/history" && touch "$PERSIST/history/.bash_history"
if ! grep -q 'sandbox-bootstrap history' "$HOME/.bashrc" 2>/dev/null; then
  cat >>"$HOME/.bashrc" <<'EOF'
# sandbox-bootstrap history (persisted on the volume)
export HISTFILE=/persist/history/.bash_history
export PROMPT_COMMAND="history -a${PROMPT_COMMAND:+; $PROMPT_COMMAND}"
EOF
fi
export GIT_CONFIG_GLOBAL="$HOME/.config/git/config"
grep -q GIT_CONFIG_GLOBAL "$HOME/.bashrc" || echo "export GIT_CONFIG_GLOBAL=\$HOME/.config/git/config" >>"$HOME/.bashrc"

case "$PROFILE" in
  standard)
    link_state .claude
    link_state .config/gh
    ;;
  hardened)
    # Tokens never live in the sandbox. git -> github.com gets its Authorization header
    # from the egress proxy; tell git not to prompt for credentials.
    git config --global credential.helper ""
    git config --global core.askPass ""
    ;;
  *) echo "unknown profile: $PROFILE" >&2; exit 2 ;;
esac

# 3. Workspace (the bind-mount equivalent: lives on the volume).
if [ -n "$REPO_URL" ]; then
  name="$(basename "${REPO_URL%.git}")"
  ws="$PERSIST/workspace/$name"
  if [ ! -d "$ws/.git" ]; then
    mkdir -p "$PERSIST/workspace"
    git clone "$REPO_URL" "$ws"
  fi
  ln -sfn "$ws" "$HOME/workspace"
  echo "workspace: $ws (~/workspace)"
fi

echo "bootstrap ($PROFILE) done"
