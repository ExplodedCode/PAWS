# PAWS — Proton-Aware Windows Sync

A Windows sync client for **Proton Drive** modeled on OneDrive: map an arbitrary Proton Drive folder
to an arbitrary local folder, two-way, with **files-on-demand** (cloud files appear as placeholders
and download only when opened) or a **full local copy** mode.

Unlike Proton's built-in "Computer sync", PAWS targets a **normal, path-addressed Drive folder**
rather than a per-device silo — so it survives OS reinstalls and lets multiple machines (laptop +
desktop) sync to the same folder. Unlike rclone, it adds the on-demand placeholder layer via
Windows' Cloud Filter API, with real Explorer integration (status icons, "Always keep on this
device" / "Free up space").

## Architecture (ports & adapters)

All projects target **`net10.0`** (Windows-specific ones: `net10.0-windows10.0.19041.0`).

| Project | Role |
|---------|------|
| `src/PAWS.Core` | Domain models + ports, BCL-only: accounts/settings (`ISecretStore`, `ISettingsStore`), the Drive port (`IProtonDriveClient`), the sync reconciler + executor (`PAWS.Core.Sync`), diagnostics logging. |
| `src/PAWS.Infrastructure` | Adapters: `DpapiSecretStore`, `JsonSettingsStore`, `JsonSyncStateStore`, `JsonPopulatedFolderStore`, `PawsPaths`, `WebProtonAuthenticator` (browser login), Windows autostart registration. |
| `src/PAWS.Proton` | Wraps the official `Proton.Sdk` / `Proton.Drive.Sdk` + `Proton.Cryptography` (native E2E crypto) behind `IProtonDriveClient`: browser-session resume, file upload/download/list/rename/move/trash. |
| `src/PAWS.CloudFilter` | `IPlaceholderEngine` over the Windows Cloud Filter API (`cfapi`): sync-root registration with Explorer shell integration, placeholder creation, hydrate-on-open, dehydrate ("free up space"), lazy per-folder population. |
| `PAWS/` | WinUI 3 app: browser login, accounts/folders UI, on-demand + full-sync management, speed limits, conflict resolution, tray/background host, single-instance guard. |
| `src/PAWS.Setup` | Console dev tool for account setup outside the GUI (`--weblogin` default, `--show`, `--reset`, `--selftest`). |
| `src/PAWS.AuthTest` | Console diagnostic/test harness — dozens of `--verb` entry points exercising every layer (snapshotting, on-demand sync, hydration/dehydration, conflict resolution, throttling, single-instancing, and more) against a real or fake Drive client. The closest thing this project has to an automated test suite today — see [Known limitations](#known-limitations). |

## Sync modes

Each folder pair (local path ↔ Drive path) is configured independently as:

- **On-demand** — files appear instantly as placeholders; content downloads only when opened.
  Explorer shows cloud/pinned status icons, and right-click gives "Always keep on this device" /
  "Free up space" to pin or dehydrate a file. Folders populate lazily as you browse into them.
- **Full sync** — a complete local copy of the folder, kept in sync both ways.

Both modes support **Auto** mode (a background watcher plus a periodic Drive poll — no button
pressing needed) as well as manual sync/push/pull, per-pair or app-wide transfer speed limits, and a
conflict resolution UI for files that changed on both sides since the last sync. Deletions are never
applied automatically past a safety threshold — a large or disproportionately large deletion is held
for manual review instead of auto-applied.

## Authentication

Login is **browser-based only** ("Sign in with Proton"): the app opens Proton's website, you sign in
there — **passkeys / 2FA / CAPTCHA are all handled by Proton** — and the app receives a forked
session. Your password is never entered into PAWS. This path needs no native crypto; it's pure HTTP
+ `System.Security.Cryptography.AesGcm`. (There is no SRP/password login path in PAWS — an earlier
one was removed in favor of this browser-only flow.)

Actual Drive operations (listing, uploading, downloading, etc.) *do* need the native crypto library,
since Proton Drive is end-to-end encrypted — see [Native crypto library](#native-crypto-library).

PAWS supports **multiple accounts at once** — different Proton accounts, or even the same account
added more than once — each with its own set of synced folders.

### Where things are stored

Non-secret config and per-pair sync state live under `%LOCALAPPDATA%\PAWS\`. A packaged (MSIX)
launch transparently redirects this to
`%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Local\PAWS\` — app code doesn't need to know
the difference.

- `settings.json` — `Accounts[]`, each with its own `SyncPairs[]` (local/remote paths, mode, speed
  limits, auto-sync preference).
- `secrets\{accountId}.bin` — **one DPAPI-encrypted blob per account** (CurrentUser): the resumable
  Proton session (UID + tokens) and the key password. Decryptable only by the same Windows user on
  the same machine.
- `state\{pairId}.json` / `state\{pairId}.populated.json` — last-known sync state, and (for
  on-demand pairs) which folders have actually been browsed/materialized, so the reconciler never
  mistakes an un-browsed remote folder for a local deletion.
- `logs\paws-{yyyyMMdd}.log` — dated diagnostic log (sync failures, session/token events, startup).

## Running the app

Open `PAWS.slnx` in Visual Studio (set platform to **x64**) and run the `PAWS` project, or:

```powershell
dotnet build PAWS/PAWS.csproj -c Debug -p:Platform=x64
```

First launch (no accounts) shows the **setup screen** (Sign in with Proton → choose a folder);
afterwards the **home screen** lists accounts and folders. Closing the window keeps PAWS running in
the background — reopen or quit from the **tray icon**.

The console tool mirrors the account-setup part of that flow for quick testing, without the GUI:

```powershell
dotnet run --project src/PAWS.Setup -- --weblogin   # browser sign-in, store the account (default)
dotnet run --project src/PAWS.Setup -- --show       # list configured accounts
dotnet run --project src/PAWS.Setup -- --reset      # clear all credentials + settings
```

## Native crypto library

`PAWS.Proton` requires `proton_crypto.dll` for every Drive operation (list/upload/download/etc.),
since Proton Drive is end-to-end encrypted. It is **not committed** to the repo — build it once from
the `dotnet-crypto` Go source and drop it at `native/win-x64/proton_crypto.dll`. See
[native/README.md](native/README.md). The browser login path itself does **not** need it.

## Known limitations

**No true byte-range hydration.** Opening an on-demand file always downloads the whole file from
byte 0, even when only a small range is actually requested (e.g. seeking deep into a large file).
Proton's public C# SDK (`Proton.Drive.Sdk`) exposes only a whole-file, sequential download
(`FileDownloader.DownloadToStream`) — the block-level machinery that would allow a true range fetch
(block manifest, per-block decrypt/integrity verification) is `internal` to the SDK. Closing this
would require reflecting into the SDK's internal decryption/verification code, which was deliberately
ruled out as a security and maintainability risk not worth taking for this app. This is a permanent,
accepted limitation, not a planned future task — revisit only if Proton's SDK ever exposes block-level
access publicly. (Separately, the memory-bloat this used to cause — buffering a whole downloaded file
in RAM — is already fixed: hydration spills to a temp file on disk, not a `byte[]`, so this is purely a
random-access-speed limitation, not a memory one. See `CloudFilterPlaceholderEngine.TransferAsync`'s
doc comment for the full technical detail.)

**No formal automated test project.** Testing today is `PAWS.AuthTest`'s console harness — dozens of
hand-run `--verb` self-tests (some offline/pure, some against a live Drive account) rather than a
`PAWS.Tests` project on a standard framework (xUnit/etc.) that CI could run automatically.

## Status

Working end to end against a live Proton Drive account: browser sign-in with multi-account support,
on-demand sync with Explorer integration (placeholders, hydration, dehydration, pinning), full
two-way sync, background auto-sync (local watcher + periodic Drive poll) for both modes, conflict
resolution, per-pair and app-wide transfer speed limits, single-instancing, and packaged
autostart-at-login. See [Known limitations](#known-limitations) above for the two known gaps.

## License

MIT — see [LICENSE](LICENSE). The whole dependency stack is MIT; Proton's GPL-3.0 `windows-drive`
client is used only as a read-only reference, never copied.
