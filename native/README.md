# Native crypto library (`proton_crypto`)

`proton_crypto.dll` wraps Proton's **GopenPGP / GoSRP**. It is required only by the **SRP / native
fallback path** (`PAWS.Proton`, `PAWS.AuthTest`) and, later, Drive file (PGP) operations. The
**primary browser login does not need it.**

The dll is **not committed** (it's gitignored) — build it from the `dotnet-crypto` Go source and
drop the result here as `native/win-x64/proton_crypto.dll`. `PAWS.Proton` copies it next to the
executable at build time so `DllImport("proton_crypto")` resolves.

## Build (Windows, one-time)

Requires Go + a C compiler (cgo). Install once, no admin, with [scoop](https://scoop.sh):

```sh
scoop install go gcc
```

Then build from the cloned `ProtonDriveApps/dotnet-crypto` repo:

```sh
export PATH="$HOME/scoop/shims:$HOME/scoop/apps/gcc/current/bin:$PATH"
export CGO_ENABLED=1 GOOS=windows GOARCH=amd64
cd /path/to/dotnet-crypto
go build -C src/go -buildmode=c-shared -o "$PWD/bin/runtimes/win-x64/native/proton_crypto.dll" .
```

Copy `bin/runtimes/win-x64/native/proton_crypto.dll` to `native/win-x64/proton_crypto.dll` here.

Verify it loads:

```powershell
dotnet run --project src/PAWS.AuthTest -- --cryptocheck
```
