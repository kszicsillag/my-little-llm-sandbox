# Sandman

The CLI and tray for the [`.sandbox/`](../.sandbox/) workflow (Windows and Linux), built on the
ACA Sandboxes .NET SDK ([`Microsoft.Azure.ContainerApps.Sandbox`](https://www.nuget.org/packages/Microsoft.Azure.ContainerApps.Sandbox)).
It doesn't need bash, Python, `aca` or `az rest`. Only disk image builds stay in `.sandbox/sbx.sh image`.

| Project | Output | What |
|---|---|---|
| `Sandman.Core` | library | config, auth, sandbox operations over the SDK |
| `Sandman.Cli` | `sbx` | the workflow commands (`sbx --help`); usage in [`.sandbox/README.md`](../.sandbox/README.md) |
| `Sandman.Tray` | `Sandman` | Avalonia tray icon: running count, Stop / Resume, allow-all with auto-lockdown |

## Setup

- .NET 10 SDK.
- The sandbox group config from `aca config sandbox set` (`~/.aca/config.json`), or the
  `ACA_SUBSCRIPTION` / `ACA_RESOURCE_GROUP` / `ACA_REGION` / `ACA_SANDBOX_GROUP` environment variables.
- Signed in with `az login`. Sandman reuses that session through `AzureCliCredential` and falls back to the other
  developer credentials. It never opens a browser.

## Install

Publish the CLI into a directory on your `PATH`:

```sh
dotnet publish src/Sandman.Cli -c Release -o ~/.sandman/bin   # then add ~/.sandman/bin to PATH
dotnet publish src/Sandman.Tray -c Release -o ~/.sandman/tray  # optional: run ~/.sandman/tray/Sandman(.exe), e.g. at login
```

The CLI publishes as a single file, `sbx` / `sbx.exe` (about 5 MB), that runs on the installed .NET 10 runtime.
It's built for this machine's platform by default; pass `-r linux-x64`, `-r win-x64` or `-r osx-arm64` to build
for another. After switching between self-contained and framework-dependent builds for the same platform, delete
`src/Sandman.Cli/obj` first. Otherwise the publish may reuse the old bundle.

`up` and `allow-vscode` read the profiles in `.sandbox/`. `sbx` finds them by searching upward from the current
directory, so run it inside the repo, or set `SBX_HOME=/path/to/repo/.sandbox` to use it from anywhere.

Without installing: `dotnet run --project src/Sandman.Cli -- ls` (and `dotnet run --project src/Sandman.Tray`).

## Porting status

| sbx.sh | sbx (C#) |
|---|---|
| `ls`, `stop`, `resume` | ✅ |
| `preview` | ✅ via the SDK's `PortAuthConfigEntraId.TenantIds`, which replaces the `az rest` workaround |
| `allow-all`, `lockdown` | ✅ with `--dry-run`. `ls` shows an EGRESS column |
| `exec` | ✅ streaming exec session, runs as `dev` (`SBX_USER`), resumes a stopped sandbox first (aca's `auto_resume`, `ACA_AUTO_RESUME=0` to disable). Long commands don't time out |
| `down`, `purge` | ✅ confirmation prompt (`--yes` to skip; refuses without it when stdin is piped) |
| `learned` | ✅ needs a running sandbox (the decisions log is read from it) |
| `allow-vscode` | ✅ `--dry-run`. The host list is the commented-out block in `.sandbox/hardened.yaml` (found by searching upward from the current or executable directory, or from `SBX_HOME`) |
| `secrets` | ✅ hidden input. Both Anthropic keys go into one `anthropic` secret in a single call (`sbx.sh` wrote them separately, so the second replaced the first) |
| `up` | ✅ `--dry-run` prints the request. Same profiles and settings as `sbx.sh` (`SBX_IMAGE`, `SBX_CPU`, `SBX_MEMORY`, `SBX_ROOT_DISK`, `SBX_VOLUME_SIZE`, `SBX_VSCODE=1`, `SBX_ALLOW`). The YAML is converted to the REST body as-is, except that `diskId` moves to `sourcesRef.diskImage.id` |
| `shell` | ✅ PTY exec session as `dev` (no root shell + `sudo`), resumes a stopped sandbox, warns when egress is open. Local raw mode: Windows console modes (VT input/output, UTF-8 code pages) or `stty raw -echo`; window resizes are passed through |
| `code` | ✅ logs in first if needed (device code via the same PTY session as `shell`), starts the tunnel with nohup + pidfile (reused if running), idle auto-suspend 3600 s, prints the connect URLs for the real workspace path |
| `image` | still in `sbx.sh` (the SDK can't build a disk image from a local directory) |

Under Git Bash on Windows, export `MSYS_NO_PATHCONV=1` before passing absolute sandbox paths to `sbx exec`. Otherwise
Git Bash rewrites `/persist` to `C:/Program Files/Git/persist` before `sbx.exe` sees it. PowerShell doesn't do this.

### Allow-all timer

An allow-all window is recorded **in the sandbox's egress policy** as a marker rule
`allow-all-<opened>-<deadline>` (unix seconds; deadline `0` means no timer) on the host `allow-all.marker.invalid`,
which never resolves. This is the same format `sbx.sh` uses. The tray reads it on every refresh and locks down any
expired window. That means:

- windows opened by `sbx`, `sbx.sh` or another machine are enforced too;
- nothing depends on a local process staying alive. After sleep, reboot or logoff, the next refresh closes an
  overdue window. (`sbx.sh`'s `nohup sleep` timer didn't survive any of those);
- windows without a timer are left alone, as they are in `sbx.sh`, but the tray icon turns red and the menu warns.

The service returns the HTTP policy twice (top-level `defaultAction`/`rules`, and mirrored under `http`). Both copies
are edited the same way, as raw JSON, so the token-injection rules and any fields this SDK version doesn't model pass
through unchanged.

Not covered by the SDK: `image` (`aca sandboxgroup disk create --source <dir>`). The SDK can only create disk images
from a blob or a registry.

## Notes

- The SDK is `1.0.0-beta.1` and is pinned exactly in `Directory.Packages.props`.
- On Linux the tray uses StatusNotifierItem. Plain GNOME needs the AppIndicator extension; KDE, XFCE and Ubuntu work
  as they are.
- The tray icon is drawn at runtime (`TrayIconArt.cs`): a grey moon when all sandboxes are asleep, amber with a count
  when some are running.
