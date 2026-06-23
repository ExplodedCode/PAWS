using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PAWS.Core.Abstractions;
using PAWS.Core.Security;

namespace PAWS.Infrastructure.Storage;

/// <summary>
/// <see cref="ISecretStore"/> backed by Windows DPAPI (<see cref="ProtectedData"/>, CurrentUser scope).
/// Encrypted blobs can only be decrypted by the same Windows user on the same machine — exactly the
/// trust boundary a per-machine sync client wants. Swap for a Windows Credential Manager implementation
/// later without touching callers, since they depend only on <see cref="ISecretStore"/>.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    // App-specific entropy mixed into DPAPI. Not a secret on its own; it just ties the blob to PAWS.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PAWS.Proton.v1");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly PawsPaths _paths;

    public DpapiSecretStore(PawsPaths paths) => _paths = paths;

    public bool HasProtonSecrets => File.Exists(_paths.ProtonSecretsFile);

    public void SaveProtonSecrets(ProtonSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _paths.EnsureCreated();

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(secrets, JsonOptions);
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

            // Write atomically: temp file, then replace, so a crash mid-write can't corrupt the store.
            var tmp = _paths.ProtonSecretsFile + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, _paths.ProtonSecretsFile, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public ProtonSecrets? LoadProtonSecrets()
    {
        if (!File.Exists(_paths.ProtonSecretsFile))
        {
            return null;
        }

        var encrypted = File.ReadAllBytes(_paths.ProtonSecretsFile);
        var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<ProtonSecrets>(plaintext, JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void ClearProtonSecrets()
    {
        if (File.Exists(_paths.ProtonSecretsFile))
        {
            File.Delete(_paths.ProtonSecretsFile);
        }
    }
}
