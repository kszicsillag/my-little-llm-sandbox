#!/usr/bin/env bash
# sandbox-bootstrap: the "postCreateCommand" of an ACA sandbox. Idempotent; runs on every `sbx.sh up`.
#
#   sandbox-bootstrap <standard|hardened> [repo-url]
#
# 1. Repairs ownership of the persistent volume (/persist, a DataDisk volume).
# 2. Links developer state from /persist into $HOME so it survives sandbox deletion.
#    standard: also persists login state (~/.claude, ~/.config/gh, VS Code tunnel login).
#    hardened: never persists tokens; credentials are injected by the egress proxy.
# 3. Clones the repo into /persist/workspace/<name> (once) and links ~/workspace.
# 4. Installs project dependencies onto the volume:
#    - the project's own hook, <repo>/.sandbox/setup.sh, if present (preferred), else
#    - auto-detect: requirements.txt -> venv, package-lock.json/package.json -> npm, *.sln/*.csproj -> dotnet restore.
#    Hook contract: cwd = repo root; SANDBOX_STATE_DIR = persistent per-project dir; a
#    Python venv at $SANDBOX_STATE_DIR/venv is activated in new shells automatically.
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
  local target="$PERSIST/home/$1" src="$HOME/$1"
  mkdir -p "$target" "$(dirname "$src")"
  if [ -L "$src" ]; then return; fi
  if [ -d "$src" ]; then cp -a "$src/." "$target/" && rm -rf "$src"; fi
  ln -s "$target" "$src"
}

add_rc() {  # <marker> <line>: append to ~/.bashrc once
  grep -qF "$1" "$HOME/.bashrc" 2>/dev/null || printf '%s # %s\n' "$2" "$1" >>"$HOME/.bashrc"
}

link_state .config/git
link_state .npm
link_state .cache/pip
link_state .vscode-server          # VS Code server + remote extensions (no credentials)
mkdir -p "$PERSIST/history" && touch "$PERSIST/history/.bash_history"
add_rc sbx-histfile 'export HISTFILE=/persist/history/.bash_history'
add_rc sbx-histappend 'export PROMPT_COMMAND="history -a${PROMPT_COMMAND:+; $PROMPT_COMMAND}"'
export GIT_CONFIG_GLOBAL="$HOME/.config/git/config"
add_rc sbx-gitconfig 'export GIT_CONFIG_GLOBAL=$HOME/.config/git/config'

case "$PROFILE" in
  standard)
    link_state .claude
    link_state .config/gh
    link_state .vscode/cli         # VS Code tunnel login token
    ;;
  hardened)
    # Tokens never live on the volume. git -> github.com gets its Authorization header
    # from the egress proxy; tell git not to prompt for credentials.
    git config --global credential.helper ""
    git config --global core.askPass ""
    ;;
  *) echo "unknown profile: $PROFILE" >&2; exit 2 ;;
esac

[ -n "$REPO_URL" ] || { echo "bootstrap ($PROFILE) done (no repo)"; exit 0; }

# 3. Workspace (the bind-mount equivalent: lives on the volume).
name="$(basename "${REPO_URL%.git}")"
ws="$PERSIST/workspace/$name"
if [ ! -d "$ws/.git" ]; then
  mkdir -p "$PERSIST/workspace"
  git clone "$REPO_URL" "$ws"
fi
ln -sfn "$ws" "$HOME/workspace"
echo "workspace: $ws (~/workspace)"

# 4. Project dependencies, on the volume.
export SANDBOX_STATE_DIR="$PERSIST/projects/$name"
mkdir -p "$SANDBOX_STATE_DIR"
cd "$ws"

autodetect() {
  local did=""
  if [ -f requirements.txt ]; then
    [ -x "$SANDBOX_STATE_DIR/venv/bin/python" ] || python3 -m venv "$SANDBOX_STATE_DIR/venv"
    "$SANDBOX_STATE_DIR/venv/bin/pip" install -q --upgrade pip
    "$SANDBOX_STATE_DIR/venv/bin/pip" install -q -r requirements.txt
    did="$did pip"
  fi
  if [ -f package-lock.json ]; then npm ci --no-audit --no-fund; did="$did npm-ci"
  elif [ -f package.json ]; then npm install --no-audit --no-fund; did="$did npm-install"
  fi
  if compgen -G "*.sln" >/dev/null || compgen -G "*.csproj" >/dev/null; then
    if command -v dotnet >/dev/null; then dotnet restore; did="$did dotnet"
    else echo "dotnet project detected but no dotnet SDK in image (use a .NET stack image)" >&2; fi
  fi
  echo "deps (auto-detect):${did:- nothing detected}"
}

if [ -f .sandbox/setup.sh ]; then
  echo "deps: running project hook .sandbox/setup.sh"
  bash .sandbox/setup.sh
else
  autodetect
fi

# Activate the project venv (hook- or auto-created) in new shells, workspace-scoped.
if [ -f "$SANDBOX_STATE_DIR/venv/bin/activate" ]; then
  add_rc "sbx-venv-$name" "[ -f $SANDBOX_STATE_DIR/venv/bin/activate ] && . $SANDBOX_STATE_DIR/venv/bin/activate"
fi
add_rc sbx-state-dir "export SANDBOX_STATE_DIR=$SANDBOX_STATE_DIR"

echo "bootstrap ($PROFILE) done"
