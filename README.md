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
| `src/PAWS.Core` | `net8.0` | Domain models + ports (`ISecretStore`, `ISettingsStore`, `IProtonAuthenticator`, `IWebProtonAuthenticator`) and `SetupWorkflow`. BCL-only. |
| `src/PAWS.Infrastructure` | `net8.0-windows` | Adapters: `DpapiSecretStore`, `JsonSettingsStore`, `PawsPaths`, **`WebProtonAuthenticator`** (browser login), `StubProtonAuthenticator`. |
| `PAWS/` | `net8.0-windows10.0.19041.0` | WinUI 3 app: browser login, accounts/folders UI, tray/background host. |
| `src/PAWS.Setup` | `net8.0-windows` | Console dev tool (`--weblogin`, `--show`, `--reset`). |
| `src/PAWS.Proton` | `net10.0` | SRP fallback via the official `Proton.Sdk` + `Proton.Cryptography` (native). |
| `src/PAWS.AuthTest` | `net10.0` | Console that exercises the native crypto + SRP login. |

**Planned:** `PAWS.CloudFilter` (Cloud Filter API placeholder engine), `PAWS.Sync` (reconciler).

## Authentication

Primary login is **browser-based** ("sign in with Proton"): the app opens Proton's website, you
sign in there — **passkeys / 2FA / CAPTCHA are all handled by Proton** — and the app receives a
forked session. Your password is never entered into PAWS. This path needs no native crypto; it's
pure HTTP + `System.Security.Cryptography.AesGcm`.

The SRP/password path (`PAWS.Proton`, using Proton's native GopenPGP/GoSRP) is kept as an internal
fallback and for future Drive file (PGP) operations.

PAWS supports **multiple accounts at once** — different Proton accounts, or even the same account
added more than once — each with its own set of synced folders.

### Where things are stored (`%LOCALAPPDATA%\PAWS\`)
- `settings.json` — non-secret config (schema v2): `Accounts[]`, each with its own `SyncPairs[]`.
- `secrets\{accountId}.bin` — **one DPAPI-encrypted blob per account** (CurrentUser): the resumable
  Proton session (UID + tokens) and the key password. Decryptable only by the same Windows user on
  the same machine.

## Running the app

Open `PAWS.slnx` in Visual Studio (set platform to **x64**) and run the `PAWS` project, or:

```powershell
dotnet build PAWS/PAWS.csproj -c Debug -p:Platform=x64
```

First launch (no accounts) shows the **setup screen** (Sign in with Proton → choose a folder);
afterwards the **home screen** lists accounts and folders. Closing the window keeps PAWS running in
the background — reopen or quit from the **tray icon**.

The console tool mirrors the same flow for quick testing:

```powershell
dotnet run --project src/PAWS.Setup -- --weblogin   # browser sign-in, store the account
dotnet run --project src/PAWS.Setup -- --show       # list configured accounts
dotnet run --project src/PAWS.Setup -- --reset      # clear all credentials + settings
```

## Native crypto library

`PAWS.Proton` / `PAWS.AuthTest` (the SRP fallback) and future Drive file operations require
`proton_crypto.dll`. It is **not committed** to the repo — build it once from the `dotnet-crypto`
Go source and drop it at `native/win-x64/proton_crypto.dll`. See [native/README.md](native/README.md).
The browser login path does **not** need it.

## Status

Browser login works end to end against Proton. Not yet implemented: autostart-at-login,
single-instancing, the Drive client (file listing / upload / download), the Cloud Filter placeholder
layer, and the sync engine.

## License

MIT — see [LICENSE](LICENSE). The whole dependency stack is MIT; Proton's GPL-3.0 `windows-drive`
client is used only as a read-only reference, never copied.
