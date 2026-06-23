using PAWS.Core.Security;

namespace PAWS.Core.Abstractions;

/// <summary>
/// Encrypted, machine-bound storage for sensitive values. Implementations MUST encrypt at rest
/// (e.g. Windows DPAPI scoped to the current user, or Windows Credential Manager).
/// </summary>
public interface ISecretStore
{
    bool HasProtonSecrets { get; }

    void SaveProtonSecrets(ProtonSecrets secrets);

    ProtonSecrets? LoadProtonSecrets();

    void ClearProtonSecrets();
}
