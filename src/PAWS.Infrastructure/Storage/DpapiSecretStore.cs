using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PAWS.Core.Abstractions;
using PAWS.Core.Security;

namespace PAWS.Infrastructure.Storage;

/// <summary>
/// <see cref="ISecretStore"/> backed by Windows DPAPI (<see cref="ProtectedData"/>, CurrentUser scope),
/// one encrypted blob per account (<c>secrets\{accountId}.bin</c>). Blobs can only be decrypted by the
/// same Windows user on the same machine. Swap for a Windows Credential Manager implementation later
/// without touching callers, since they depend only on <see cref="ISecretStore"/>.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    // App-specific entropy mixed into DPAPI. Not a secret on its own; it just ties the blob to PAWS.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PAWS.Proton.v1");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly PawsPaths _paths;

    public DpapiSecretStore(PawsPaths paths) => _paths = paths;

    public bool HasSecrets(string accountId) => File.Exists(_paths.SecretsFileFor(accountId));

    public void SaveSecrets(string accountId, ProtonSecrets secrets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(secrets);
        _paths.EnsureCreated();

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(secrets, JsonOptions);
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

            // Write atomically: temp file, then replace, so a crash mid-write can't corrupt the store.
            var file = _paths.SecretsFileFor(accountId);
            var tmp = file + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, file, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public ProtonSecrets? LoadSecrets(string accountId)
    {
        var file = _paths.SecretsFileFor(accountId);
        if (!File.Exists(file))
        {
            return null;
        }

        var encrypted = File.ReadAllBytes(file);
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

    public void ClearSecrets(string accountId)
    {
        var file = _paths.SecretsFileFor(accountId);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }
}
