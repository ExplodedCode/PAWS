using Proton.Cryptography.Pgp;

namespace PAWS.Proton;

/// <summary>
/// Tiny offline check that the native <c>proton_crypto</c> library loads and runs (no network, no account).
/// Generating a throwaway PGP key exercises the GopenPGP P/Invoke path end to end.
/// </summary>
public static class ProtonCryptoSelfTest
{
    public static string GenerateKeyFingerprint()
    {
        using var key = PgpPrivateKey.Generate("PAWS Self Test", "selftest@paws.local", KeyGenerationAlgorithm.Ecc);
        return Convert.ToHexString(key.GetFingerprint());
    }
}
