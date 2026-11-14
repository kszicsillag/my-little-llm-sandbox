# Sandbox templates (devcontainer-style, on ACA Sandboxes)

These templates apply the devcontainer approach to Azure Container Apps Sandboxes. Each sandbox is a microVM, so there is no shared host kernel.

| Devcontainer | Here |
|---|---|
| `Dockerfile` + features | [`image/Dockerfile`](image/Dockerfile) → disk image `agent-base-v3` (built remotely; base image, Claude Code and VS Code CLI pinned) |
| `devcontainer.json` | [`standard.yaml`](standard.yaml) / [`hardened.yaml`](hardened.yaml) (sandbox manifests) |
| `postCreateCommand` | [`image/bootstrap.sh`](image/bootstrap.sh) (`sandbox-bootstrap` in the image), then the **project's own** `<repo>/.sandbox/setup.sh` |
| VS Code "Reopen in Container" | `sbx.sh code <name>` → VS Code Remote Tunnel; ports such as 8000 forward to your `localhost` |
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

If exactly one sbx sandbox is running, you can leave out `<name>` for `shell`, `code`, `allow-vscode`, `allow-all`, `lockdown`, `learned` and `preview`. For example, `sbx.sh allow-all 30m` or `sbx.sh preview 8000`. `up`, `exec`, `down` and `purge` always need a name.

**Standard** profile: run `gh auth login` and `claude` once inside the sandbox. The logins are stored on the volume and survive rebuilds.

### Project dependencies

These go on the volume, not in a per-project image. Bootstrap runs on every `up`, after the clone:

1. **Project hook (preferred).** If `<repo>/.sandbox/setup.sh` exists, it runs from the repo root with `SANDBOX_STATE_DIR=/persist/projects/<name>`. A venv at `$SANDBOX_STATE_DIR/venv` is activated in new shells automatically. The hook must be idempotent. See skalazhato's `.sandbox/setup.sh` for an example.
2. **Auto-detect (fallback):**
   - `requirements.txt` → venv + `pip install -r`
   - `package-lock.json` → `npm ci` (or `package.json` → `npm install`)
   - `*.sln`/`*.csproj` → `dotnet restore` (needs a .NET stack image)

Bake dependencies into an image only when there's a reason to:

- the hardened profile shouldn't reach package registries;
- several sandboxes of the same project run in parallel (a DataDisk volume mounts to one sandbox);
- setup is slow or needs OS packages.

`aca sandbox commit` after bootstrap gives a disposable warm image for those cases.

### Stack images and per-sandbox allowlists

Some repos need OS-level tooling the base image doesn't have. `sbx.sh image <stack>` builds the base image plus `image/stacks/<stack>.Dockerfile`. Give it its own name:

```sh
SBX_IMAGE=agent-dotnet-v2 .sandbox/sbx.sh image dotnet   # base + .NET 10 SDK, Node 24, emf2svg, poppler, Playwright libs, csharp-ls
```

For hardened sandboxes, `SBX_ALLOW` adds Allow rules for that one sandbox:

- A plain host allows the whole host.
- `host/path/*` allows only that path, and only for GET/HEAD. Use it for shared hosts such as `storage.googleapis.com`: allowing the whole host would open every bucket for uploads.

**Example: `bmeviaumb11/skalazhato-ea` (C# + Slidev), verified 2026-11-13.** Its `converter` and `slides` CI suites pass:

```sh
export SBX_IMAGE=agent-dotnet-v2 SBX_CPU=2000m SBX_MEMORY=4096Mi SBX_ROOT_DISK=8Gi SBX_VOLUME_SIZE=12Gi
export SBX_ALLOW="api.nuget.org,globalcdn.nuget.org,cdn.playwright.dev,playwright.download.prss.microsoft.com,storage.googleapis.com/chrome-for-testing-public/*"
.sandbox/sbx.sh up hardened https://github.com/bmeviaumb11/skalazhato-ea.git skalazhato-ea
```

- **Size L (2 vCPU / 4 GiB).** At 2 GiB, headless Chrome gets OOM-killed during `Page.printToPDF` when building the decks. Check with `sudo dmesg | grep -i oom`. L uses 2 cores of the regional quota.
- **Long commands:** `aca sandbox exec` times out on long runs, such as the full CI. Start them with `nohup … &` and poll a log file.
- **Not included:** the converter's Azure OpenAI calls (`AZURE_OPENAI_ENDPOINT`). They'd need the endpoint host allowed, plus a key or a managed-identity token injected by the egress proxy.

### VS Code (Remote Tunnels)

```sh
SBX_VSCODE=1 .sandbox/sbx.sh up hardened <repo-url> repo   # hardened only: allow the tunnel hosts for this sandbox
.sandbox/sbx.sh shell repo     # once per volume (standard) or per rebuild (hardened): code tunnel user login --provider microsoft
.sandbox/sbx.sh code repo      # starts the tunnel "sbx-repo"
```

Connect from desktop VS Code: install the *Remote - Tunnels* extension, then go to Remote Explorer → Tunnels → `sbx-repo`. Or open `https://vscode.dev/tunnel/sbx-repo`.

- **Workspace Trust** is stored in your desktop VS Code, per remote path, so the sandbox can't pre-trust anything. Open the real path that `sbx.sh code` prints (`/persist/workspace/<repo>`, not the `~/workspace` symlink). On the first prompt, trust the parent folder `/persist/workspace`. That covers every repo and every rebuild, as long as the tunnel name stays the same.
- Use the **Microsoft** login provider. GitHub login conflicts with the hardened profile's `github.com` header injection.
- `sbx.sh code` sets idle auto-suspend to 1 h, because tunnel traffic doesn't count as activity. Disk-mode suspend stops the tunnel.
- **Standard** keeps the tunnel login on the volume. **Hardened** doesn't, so you log in again after each rebuild.
- `SBX_VSCODE=1` enables the commented-out VS Code block in `hardened*.yaml`, for that sandbox only. Without it the hardened profiles block the tunnel.
- Already created a hardened sandbox without it? Run `sbx.sh allow-vscode <name>`. It adds the same rules to the live egress policy without a rebuild. A later `down` + `up` drops them again unless you pass `SBX_VSCODE=1`.
- **Forwarded ports go through dev tunnels, even in desktop VS Code.** The Ports tab shows `https://<id>-<port>.<region>.devtunnels.ms`, not `localhost`. That works, but the first load is slow. Use `sbx.sh preview` (below) for a fast preview.
- **Extensions get the injected GitHub token.** Injection is per host, so any extension that calls `api.github.com` (GitHub, Pull Requests) is authenticated as you, within the PAT's scope. Be selective about extensions in hardened sandboxes.
- **Copilot doesn't work in hardened** (`*.githubcopilot.com` is blocked), and neither does Microsoft/Copilot telemetry. Allow `api.githubcopilot.com`, `api.individual.githubcopilot.com` and `exp.individual.githubcopilot.com` if you want it.

### Preview a web server (`sbx.sh preview`)

```sh
.sandbox/sbx.sh preview repo [port] [email]   # default port 8000, email = your `az account show` user
# → https://<sandbox-id>--<port>.<region>.adcproxy.io   (open in the browser, e.g. append /skalazhato/)
.sandbox/sbx.sh preview repo 3030-3034        # a range: one URL per port (max 20)
```

This exposes the port through the sandbox's own ingress, which doesn't go through dev tunnels or the egress proxy. It's restricted to one Entra identity in your tenant and is never anonymous.

- **The server must listen on `0.0.0.0`.** If it listens on `127.0.0.1` only, the ingress returns `{"error":"Failed to forward request"}`. Vite-based servers (Vite, Slidev) also need `server.allowedHosts: ['.adcproxy.io']`.
- **Inside the sandbox, `sbx-url <port> [path]` prints the same URL.** It's built from `ADC_SANDBOX_ID` and `ADC_PROXY_DNS_SUFFIX`. Bootstrap also writes a marked block into `~/.claude/CLAUDE.md`. It tells the agent to report `sbx-url` URLs instead of `localhost`, to bind to `0.0.0.0`, and that you have to open the port. The agent can't see which ports are open, because only the management API knows.

- Verified with `mkdocs serve`: fast, browser sign-in works, and **live reload works**. MkDocs uses HTTP long-polling, not WebSockets.
- `aca sandbox port add --email` doesn't work as of aca 1.0.0-preview.5: it omits the required `auth.entraId.tenantIds` and gets a `400`. `sbx.sh preview` calls the same data-plane endpoint with `az rest` and adds your tenant.
- Plain `aca sandbox port add` without `--email` creates an **anonymous** port. Don't use it for anything private.

**Hardened** profiles. There are two, and they differ only in how Claude is billed:

| Profile | Claude auth | Billing |
|---|---|---|
| `hardened` | OAuth token from `claude setup-token`, injected as `Authorization: Bearer` | Your Claude Pro/Max subscription |
| `hardened-apikey` | Console API key, injected as `x-api-key` | Pay-as-you-go API credits, separate from the subscription |

```sh
claude setup-token               # on your PC, logged in with your subscription (for `hardened`)
.sandbox/sbx.sh secrets          # GitHub fine-grained PAT + Claude token and/or API key → sandbox-group secrets
.sandbox/sbx.sh up hardened https://github.com/me/repo
```

The sandbox never sees a token. `GH_TOKEN` and `CLAUDE_CODE_OAUTH_TOKEN` (or `ANTHROPIC_API_KEY`) are placeholders. The proxy injects the real values:

- `Authorization: Bearer` for `api.github.com`
- `Authorization: Basic` for git over HTTPS to `github.com`
- `api.anthropic.com`: `Authorization: Bearer` (`hardened`) or `x-api-key` (`hardened-apikey`)

Don't set `ANTHROPIC_API_KEY` in the `hardened` profile. Claude Code would use API-key mode and bill per token.

### Secrets for the hardened profiles

Secrets are stored at **sandbox-group** level. The egress proxy injects them into requests from **every** sandbox in the group, and the sandboxes never see the values. Keep one sandbox group per person or trust boundary, and don't share a group whose secrets are your personal tokens.

| Secret (bundle / key) | Used by | Needed for |
|---|---|---|
| `github` / `token`, `basic` | `api.github.com` (`Bearer`), git over HTTPS to `github.com` (`Basic`) | Both hardened profiles (clone, fetch, push, `gh`) |
| `anthropic` / `oauth` | `api.anthropic.com` (`Authorization: Bearer`) | `hardened`: Claude via your subscription |
| `anthropic` / `key` | `api.anthropic.com` (`x-api-key`) | `hardened-apikey`: Claude via pay-as-you-go API |

#### 1. GitHub fine-grained personal access token

1. GitHub → **Settings → Developer settings → Personal access tokens → Fine-grained tokens → Generate new token**.
2. **Resource owner:** the user or organization that owns the repo. If the organization requires approval for fine-grained tokens, an org owner approves it under the org's **Settings → Personal access tokens → Pending requests**.
3. **Repository access:** *Only select repositories*. Pick only the repos the sandbox should work on.
4. **Permissions** (repository):
   - **Contents: Read and write**: clone, fetch, push.
   - **Pull requests: Read and write**: `gh pr create`. Leave it out if the agent shouldn't open PRs.
   - **Issues: Read and write**: `gh issue view/create/comment/edit`, so the agent can read the issue it works on and report back. Leave it out if the agent shouldn't touch issues.
   - **Metadata: Read** is added automatically.
5. **Expiration:** as short as practical (for example 7–30 days). It has to start with `github_pat_`; classic `ghp_` tokens are broader, so don't use them.

#### 2. Claude subscription token (for `hardened`)

On your own machine, with Claude Code logged in to your Pro/Max account:

```sh
claude setup-token
```

It prints a long-lived OAuth token. Copy it right away. Usage counts against your subscription limits, the same as interactive Claude Code. It's personal: don't put it in a shared sandbox group.

#### 3. Anthropic API key (for `hardened-apikey`, optional)

**Anthropic Console** (console.anthropic.com) → **API Keys** → **Create Key**. Billing is per token from your API credits, separate from any Claude subscription. Set a spend limit for the workspace in the Console.

#### Store, check, rotate, remove

```sh
.sandbox/sbx.sh secrets                        # prompts (hidden input) for all three; Enter skips one
aca sandboxgroup secret list                   # bundles in the group
aca sandboxgroup secret show --name github     # metadata and key names
```

- **Rotate:** run `sbx.sh secrets` again with the new value. `secret set` merges keys, so the other keys in the bundle stay as they are. Whether already-running sandboxes pick up the new value right away isn't verified, so recreate them (`sbx.sh down` + `up`) to be sure.
- **Remove:** `aca sandboxgroup secret delete --name github --yes` (or `--name anthropic`). If a token may have leaked, also revoke it at the source: GitHub's fine-grained token page or the Console's API Keys page. For a `claude setup-token` token, see the Claude Code docs on revoking it.
- `sbx.sh secrets` reads values from hidden prompts and pipes them to `aca … --from-stdin`. They don't end up in shell history, files or process arguments.

**Azure portal alternative:** Container Apps Sandbox groups → *your group* → **Security → Secrets → Create**. Enter the **Secret ID** (`github` or `anthropic`) and add the key-value pairs from the table above. The same page lists, updates and deletes secrets.

- The `github` / `basic` value is `base64("x-access-token:<PAT>")`. `sbx.sh secrets` computes it for you; in the portal you paste it yourself. In PowerShell, without the token landing in history:

  ```powershell
  $t = Read-Host -AsSecureString "PAT"; $p = [Net.NetworkCredential]::new('', $t).Password
  [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$p")) | Set-Clipboard; $p = $null
  ```

- The Value field is plain text, so the token is visible on screen while you paste it.
- `aca sandboxgroup browse` opens this portal view for the configured group.

Check the injection without exposing anything (in a hardened sandbox):

```sh
.sandbox/sbx.sh exec repo 'echo $GH_TOKEN; curl -s https://api.github.com/user | head -3'
# prints the placeholder "injected-by-egress-proxy", then your GitHub user (not "Bad credentials")
```

To change what's allowed, edit the `egressPolicy.rules` in `hardened.yaml`. The first matching rule wins, so put Deny and Transform rules before Allow rules.

### Temporarily open egress (discovery mode)

When an agent or build keeps hitting denied hosts, open egress for a short window, then turn what it used into specific rules:

```sh
.sandbox/sbx.sh allow-all repo 30m   # default Allow for 30 min (no ttl = until lockdown)
# ... run the failing build / agent task ...
.sandbox/sbx.sh lockdown repo        # back to default Deny now (also cancels the timer)
.sandbox/sbx.sh learned repo         # hosts reached only via the default Allow → add them to SBX_ALLOW
```

- Only the policy's `defaultAction` changes. The rules still match first, so **token injection and `block-repo-delete` stay in force**, and tokens still never enter the sandbox.
- **Not protected while open:** the agent, or anything it runs, can upload repo contents anywhere, and fetch untrusted pages and binaries. That's the main prompt-injection route, so don't leave an open sandbox unattended.
- The **ttl is a local timer** on your PC (`nohup sleep … && sbx.sh lockdown`). If the PC reboots or the timer process dies, egress stays open. `sbx.sh shell` warns when a sandbox is open. The policy also records the window in a marker rule, `allow-all-<opened>-<deadline>`, on the never-resolving host `allow-all.marker.invalid`.
- `learned` reads `aca sandbox egress decisions`. It lists every allowed request whose `matchedRule` isn't one of the policy's named rules. Only `allow-all` lets such requests through, so they're the hosts you'd otherwise have to allow by hand.
- Like `allow-vscode`, these commands edit the live policy. A `down` + `up` always starts from the clean profile.

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

- `aca sandbox exec` runs as `dev` (non-root), using the image's `USER`. `aca sandbox shell` opens as **root**, so `sbx.sh shell` switches to `dev`; otherwise logins land in `/root`. PID 1 is the platform's `tini … adc-hold`, so the image `CMD` is ignored.
- Volume state survived deleting and recreating the sandbox. Files on the root disk didn't.
- Hardened profile:
  - headers were injected for GitHub API, git HTTPS and Anthropic;
  - `DELETE /repos/*` → 403, non-allowlisted host → 403;
  - npm and PyPI were reachable.
- With TLS inspection set to `Full`, the custom image needed no extra CA setup.
