# Sandman

Cross-platform companion for the [`.sandbox/`](../.sandbox/) workflow (Windows tray and Linux), built on the
ACA Sandboxes .NET SDK ([`Microsoft.Azure.ContainerApps.Sandbox`](https://www.nuget.org/packages/Microsoft.Azure.ContainerApps.Sandbox)).
It doesn't need bash, Python, `aca` or `az rest`.

| Project | Output | What |
|---|---|---|
| `Sandman.Core` | library | config, auth, sandbox operations over the SDK |
| `Sandman.Cli` | `sbx` | `sbx.sh`'s commands, ported one slice at a time |
| `Sandman.Tray` | `Sandman` | Avalonia tray icon: running count, Stop / Resume per sandbox |

## Setup

- .NET 10 SDK.
- The same sandbox group config as `sbx.sh`: `~/.aca/config.json` (from `aca config sandbox set`), or the
  `ACA_SUBSCRIPTION` / `ACA_RESOURCE_GROUP` / `ACA_REGION` / `ACA_SANDBOX_GROUP` environment variables.
- Signed in with `az login`. Sandman reuses that session through `AzureCliCredential` and falls back to the other
  developer credentials. It never opens a browser.

```sh
dotnet run --project src/Sandman.Cli -- ls
dotnet run --project src/Sandman.Cli -- preview skalazhato-ea 8000
dotnet run --project src/Sandman.Tray
```

## Porting status

| sbx.sh | sbx (C#) |
|---|---|
| `ls`, `stop`, `resume` | ✅ |
| `preview` | ✅ via the SDK's `PortAuthConfigEntraId.TenantIds`, which replaces the `az rest` workaround |
| `allow-all`, `lockdown` | ✅ with `--dry-run`. `ls` shows an EGRESS column |
| everything else | still in `sbx.sh` |

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
