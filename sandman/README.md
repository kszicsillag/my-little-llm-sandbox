# Sandman

The CLI and tray for the [`.sandbox/`](../.sandbox/) workflow (Windows and Linux), built on the
ACA Sandboxes .NET SDK ([`Microsoft.Azure.ContainerApps.Sandbox`](https://www.nuget.org/packages/Microsoft.Azure.ContainerApps.Sandbox)).
It doesn't need bash, Python, `aca` or `az rest`. Only disk image builds stay in `.sandbox/sbx.sh image`.

| Project | Output | What |
|---|---|---|
| `Sandman.Core` | library | config, auth, sandbox operations over the SDK |
| `Sandman.Cli` | `sbx` | the workflow commands (`sbx --help`); usage in [`.sandbox/README.md`](../.sandbox/README.md) |
| `Sandman.Tray.Core` | library | tray logic shared by both tray apps: polling, allow-all auto-lockdown, operations, the menu as data |
| `Sandman.Tray.Windows` | `Sandman.exe` | Windows tray: native Win32 menu via [H.NotifyIcon](https://www.nuget.org/packages/H.NotifyIcon), no Avalonia |
| `Sandman.Tray` | `Sandman` | Linux (and macOS) tray: Avalonia `TrayIcon` |

## Setup

- .NET 10 SDK.
- The sandbox group config from `aca config sandbox set` (`~/.aca/config.json`), or the
  `ACA_SUBSCRIPTION` / `ACA_RESOURCE_GROUP` / `ACA_REGION` / `ACA_SANDBOX_GROUP` environment variables.
- Signed in with `az login`. Sandman reuses that session through `AzureCliCredential` and falls back to the other
  developer credentials. It never opens a browser.

## Install

### Debian / Ubuntu / KDE neon (APT feed)

The `sandman` package has `sbx` and the tray (`sandman`, started at login), self-contained: no .NET runtime needed.
Updates arrive with `apt upgrade` and in KDE Discover. Once per machine:

```sh
base=https://github.com/kszicsillag/my-little-llm-sandbox/releases/download/sandman-apt
sudo curl -fsSLo /usr/share/keyrings/sandman-archive-keyring.gpg $base/sandman-archive-keyring.gpg
echo "deb [signed-by=/usr/share/keyrings/sandman-archive-keyring.gpg] $base/ ./" | sudo tee /etc/apt/sources.list.d/sandman.list
sudo apt update && sudo apt install sandman
```

The package puts the `.sandbox/*.yaml` profiles next to `sbx` (`/usr/lib/sandman/.sandbox`), so `sbx up` works outside
the repo too; a `.sandbox` folder in the current repo still takes precedence.

Releasing: push a tag `sandman-v<version>` (e.g. `sandman-v0.2.0`). The [release workflow](../.github/workflows/sandman-release.yml)
builds the amd64 and arm64 packages ([packaging/linux](packaging/linux)), attaches them to a release for the tag, and
replaces the files of the rolling `sandman-apt` release, which apt reads as a flat repository. The repository is
signed with the GPG key in the `SANDMAN_APT_SIGNING_KEY` secret. To create it (once; keep a backup, a new key means
every machine re-downloads the keyring):

```sh
export GNUPGHOME=$(mktemp -d)
gpg --batch --passphrase '' --quick-gen-key "Sandman APT <sandman@users.noreply.github.com>" ed25519 sign never
gpg --armor --export-secret-keys | gh secret set SANDMAN_APT_SIGNING_KEY
gpg --armor --export-secret-keys > sandman-apt-signing-key.asc   # backup: store it somewhere safe, not in the repo
```

### From source

Publish into a directory on your `PATH`:

```sh
dotnet publish src/Sandman.Cli -c Release -o ~/.sandman/bin            # then add ~/.sandman/bin to PATH
dotnet publish src/Sandman.Tray.Windows -c Release -o ~/.sandman/bin    # tray, Windows (optional; e.g. run at login)
dotnet publish src/Sandman.Tray -c Release -o ~/.sandman/bin            # tray, Linux / macOS
```

All publish as single files that run on the installed .NET 10 runtime: `sbx` / `sbx.exe` (about 5 MB), the Windows
tray `Sandman.exe` (about 7 MB) and the Linux tray `Sandman` (about 33 MB). The Linux tray's native Avalonia
libraries (Skia, HarfBuzz) are in the bundle; on first start they're unpacked to a `.net/Sandman` folder (set
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` to move it).
They're built for this machine's platform by default; pass `-r linux-x64`, `-r win-x64` or `-r osx-arm64` to build
for another. After switching between self-contained and framework-dependent builds for the same platform, delete
the project's `obj` folder first. Otherwise the publish may reuse the old bundle.

`up` and `allow-vscode` read the profiles in `.sandbox/`. `sbx` finds them by searching upward from the current
directory, so run it inside the repo, or set `SBX_HOME=/path/to/repo/.sandbox` to use it from anywhere.

Without installing: `dotnet run --project src/Sandman.Cli -- ls`, `dotnet run --project src/Sandman.Tray.Windows`
(or `src/Sandman.Tray` on Linux).

On Windows the tray icon may start in the taskbar's overflow panel (`^`). To keep it visible: Settings →
Personalization → Taskbar → Other system tray icons → Sandman. Windows remembers this per exe path.

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
- The two tray apps share everything except the menu and icon code (`Sandman.Tray.Core`'s `TrayController` and
  `MenuEntry`). Both list each sandbox with Resume / Stop and "VS Code tunnel"
  under it (`sbx code` from the tray: resumes, starts the tunnel and opens it with `code --folder-uri`, or vscode.dev
  without `code` on PATH; the one-time tunnel login opens a terminal running `sbx code <name>`). A running sandbox shows
  "Running · VS Code tunnel" while its tunnel is up. That's checked once per running period, after the tray's own
  tunnel action and on Refresh, not on every poll: the check is an exec, which might hold off idle auto-suspend. The Windows menu
  puts the egress actions in an Egress submenu. The Avalonia menu is flat: on Windows, Avalonia draws the
  tray menu as its own popup, which closes when a submenu takes the focus.
- On Linux the tray uses StatusNotifierItem. Plain GNOME needs the AppIndicator extension; KDE, XFCE and Ubuntu work
  as they are.
- The tray icon is drawn at runtime (Avalonia: `TrayIconArt.cs`, Windows: GDI+ in `IconArt.cs`): a grey moon when
  all sandboxes are asleep, amber when some are running (the count and open egress are in the tooltip and the menu).
