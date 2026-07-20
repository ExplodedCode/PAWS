using PAWS.Core.Security;

namespace PAWS.Core.Abstractions;

/// <summary>
/// Encrypted, machine-bound storage for sensitive values, keyed per account so multiple Proton
/// accounts can be stored side by side. Implementations MUST encrypt at rest (e.g. Windows DPAPI
/// scoped to the current user, or Windows Credential Manager).
/// </summary>
public interface ISecretStore
{
    bool HasSecrets(string accountId);

    void SaveSecrets(string accountId, ProtonSecrets secrets);

    ProtonSecrets? LoadSecrets(string accountId);

    void ClearSecrets(string accountId);
}
