# PAWS — Proton-Aware Windows Sync

A Windows sync client for **Proton Drive** modeled on OneDrive/Mega: map an arbitrary
Proton Drive folder to an arbitrary local folder, two-way, with **files-on-demand**
(cloud files appear as placeholders and download only when opened).

Unlike Proton's built-in "Computer sync", PAWS targets a **normal, path-addressed Drive
folder** rather than a per-device silo — so it survives OS reinstalls and lets multiple
machines (laptop + desktop) sync to the same folder. Unlike rclone, it adds the on-demand
placeholder layer.

## Architecture (ports & adapters)

| Project | TFM | Role |
|---------|-----|------|
| `src/PAWS.Core` | `net8.0` | Domain models + port interfaces (`ISecretStore`, `ISettingsStore`, `IProtonAuthenticator`). BCL-only, portable, testable. |
| `src/PAWS.Infrastructure` | `net8.0-windows` | Adapters: `DpapiSecretStore` (Windows DPAPI), `JsonSettingsStore`, `PawsPaths`, `StubProtonAuthenticator`. |
| `src/PAWS.Setup` | `net8.0-windows` | Interactive credential setup workflow (this milestone). |
| `PAWS/` | `net8.0-windows10.0.19041.0` | WinUI 3 app: first-run setup, home screen, and tray/background host. |

**Planned:** `PAWS.Proton` (wraps Proton.Sdk + Proton.Cryptography — deferred until the native
crypto build is set up), `PAWS.CloudFilter` (Cloud Filter API placeholder engine), `PAWS.Sync`
(reconciler), `PAWS.Tests`.

## Credential setup workflow

```powershell
# Interactive: capture Proton account + folder pair, store encrypted
dotnet run --project src/PAWS.Setup

dotnet run --project src/PAWS.Setup -- --show      # show config (secrets redacted)
dotnet run --project src/PAWS.Setup -- --reset     # clear stored credentials + settings
dotnet run --project src/PAWS.Setup -- --selftest  # non-interactive storage round-trip check
```

PAWS supports **multiple accounts at once** — different Proton accounts, or even the same
account added more than once — each with its own set of synced folders.

### Where things are stored (`%LOCALAPPDATA%\PAWS\`)
- `settings.json` — non-secret config (schema v2): `Accounts[]`, each with its own `SyncPairs[]`.
- `secrets\{accountId}.bin` — **one DPAPI-encrypted blob per account** (CurrentUser): the resumable
  Proton session (UID + access/refresh tokens) and the data password needed to unlock encryption
  keys. Decryptable only by the same Windows user on the same machine.

The design stores a **resumable session**, not the login password, so the daemon can reconnect
without prompting and survives token refresh.

## Running the app

Open `PAWS.slnx` in Visual Studio (set platform to **x64**) and run the `PAWS` project, or:

```powershell
dotnet build PAWS/PAWS.csproj -c Debug -p:Platform=x64
```

On first launch (no stored credentials) the app shows the **setup screen**; afterwards it shows
the **home screen**. Closing the window keeps PAWS running in the background — reopen or quit from
the **tray icon** in the notification area.

## Status

Authentication currently uses `StubProtonAuthenticator` (no network) so the secure-storage
pipeline and UI can be built and tested first. The real adapter drops into `PAWS.Proton` at the
documented seam — see `StubProtonAuthenticator` and the `ProtonApiSession.BeginAsync` notes.

Not yet implemented: autostart-at-login, single-instancing, and the actual sync engine.

## License

MIT — see [LICENSE](LICENSE). The whole dependency stack is MIT; Proton's GPL-3.0 `windows-drive`
client is used only as a read-only reference, never copied.
