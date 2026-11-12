# Sandbox templates (devcontainer-style, on ACA Sandboxes)

These templates apply the devcontainer approach to Azure Container Apps Sandboxes. Each sandbox is a microVM, so there is no shared host kernel.

| Devcontainer | Here |
|---|---|
| `Dockerfile` + features | [`image/Dockerfile`](image/Dockerfile) → disk image `agent-base-v2` (built remotely; base pinned by digest) |
| `devcontainer.json` | [`standard.yaml`](standard.yaml) / [`hardened.yaml`](hardened.yaml) (sandbox manifests) |
| `postCreateCommand` | [`image/bootstrap.sh`](image/bootstrap.sh) (`sandbox-bootstrap` in the image) |
| Named volumes + workspace bind mount | One `DataDisk` volume per repo (`persist-<name>`) at `/persist`. It holds `$HOME` state and the cloned repo |
| "Reopen in container" / rebuild | `sbx.sh up` / `sbx.sh down` + `up` |
| OneCLI gateway (hardened) | Egress proxy: default Deny, allowlist, credentials injected from sandbox-group secrets |

## Prerequisites

- The `infra/` deployment (sandbox group and data-plane role).
- The `aca` CLI, configured: `aca config sandbox set --group <sandbox_group_name>`.
- Bash (Git Bash on Windows), `python`, and `envsubst`.

## Usage

```sh
.sandbox/sbx.sh image                                   # once per Dockerfile change (~80 s)
.sandbox/sbx.sh up standard https://github.com/me/repo  # ~10 s
.sandbox/sbx.sh shell repo
.sandbox/sbx.sh down repo                               # "rebuild": volume is kept
.sandbox/sbx.sh up standard https://github.com/me/repo  # state and workspace are back
.sandbox/sbx.sh purge repo                              # delete sandbox + volume
```

**Standard** profile: run `gh auth login` and `claude` once inside the sandbox. The logins are stored on the volume and survive rebuilds.

**Hardened** profile:

```sh
.sandbox/sbx.sh secrets          # GitHub fine-grained PAT + Anthropic key → sandbox-group secrets
.sandbox/sbx.sh up hardened https://github.com/me/repo
```

The sandbox never sees a token. `GH_TOKEN` and `ANTHROPIC_API_KEY` are placeholders. The proxy injects the real values:

- `Authorization: Bearer` for `api.github.com`
- `Authorization: Basic` for git over HTTPS to `github.com`
- `x-api-key` for `api.anthropic.com`

To change what's allowed, edit the `egressPolicy.rules` in `hardened.yaml`. The first matching rule wins, so put Deny and Transform rules before Allow rules.

## What persists

| Location | Survives `down` + `up` |
|---|---|
| `/persist` (volume): `~/workspace`, git config, npm/pip caches, bash history; plus `~/.claude` and `~/.config/gh` in standard | Yes |
| Anything else (`$HOME`, `/tmp`, packages installed at runtime) | No |

## Sizing and limits

- Defaults are tier S: `SBX_CPU=500m SBX_MEMORY=1024Mi SBX_ROOT_DISK=5Gi SBX_VOLUME_SIZE=5Gi`.
- A tier's disk budget covers **root disk + volumes** together (S: 10 GiB, M: 20 GiB). Raise CPU to get more disk.
- A `DataDisk` volume mounts to **one sandbox at a time**, so you get one sandbox per repo name.
- Auto-suspend uses `mode: Disk`. **Memory mode is rejected when a DataDisk volume is attached**, so processes stop on suspend.

## Verified behaviour (2026-11-12, aca 1.0.0-preview.5)

- Commands run as `dev` (non-root), using the image's `USER`. PID 1 is the platform's `tini … adc-hold`, so the image `CMD` is ignored.
- Volume state survived deleting and recreating the sandbox. Files on the root disk didn't.
- Hardened profile:
  - headers were injected for GitHub API, git HTTPS and Anthropic;
  - `DELETE /repos/*` → 403, non-allowlisted host → 403;
  - npm and PyPI were reachable.
- With TLS inspection set to `Full`, the custom image needed no extra CA setup.
