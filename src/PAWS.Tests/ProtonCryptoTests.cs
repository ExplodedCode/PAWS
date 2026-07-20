using PAWS.Proton;

namespace PAWS.Tests;

/// <summary>
/// Offline coverage that the native proton_crypto library (Go/cgo, built from dotnet-crypto and vendored
/// at native/win-x64/proton_crypto.dll — see native/README.md) actually loads and works: generates a
/// throwaway PGP key. Not committed to the repo, so this SKIPS rather than fails on a fresh clone that
/// hasn't run the native build step yet. Ported from PAWS.AuthTest's --cryptocheck.
/// </summary>
public class ProtonCryptoTests
{
    [SkippableFact]
    public void NativeCrypto_GeneratesAKeyFingerprint()
    {
        Skip.IfNot(NativeCryptoDllIsPresent(), "native/win-x64/proton_crypto.dll not built locally — see native/README.md.");

        var fingerprint = ProtonCryptoSelfTest.GenerateKeyFingerprint();
        Assert.False(string.IsNullOrWhiteSpace(fingerprint));
    }

    private static bool NativeCryptoDllIsPresent()
        => File.Exists(Path.Combine(AppContext.BaseDirectory, "proton_crypto.dll"));
}
