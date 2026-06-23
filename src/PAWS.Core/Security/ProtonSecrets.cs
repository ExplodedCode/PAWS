namespace PAWS.Core.Security;

/// <summary>
/// Sensitive Proton material, persisted only through <see cref="PAWS.Core.Abstractions.ISecretStore"/>
/// (encrypted at rest). Design preference: store the <em>resumable session</em> rather than the login
/// password — once authenticated, the session tokens plus the data password are enough to reconnect
/// without prompting again.
/// </summary>
public sealed class ProtonSecrets
{
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The Proton "data"/mailbox password used to unlock the encryption keys (<c>ApplyDataPasswordAsync</c>).
    /// In single-password accounts this equals the login password. Required on every resume to decrypt
    /// Drive node keys. Stored encrypted; never written to <c>settings.json</c>.
    /// </summary>
    public string? DataPassword { get; set; }

    // ---- Resumable session (maps to ProtonApiSession.Resume). Populated after a successful login. ----

    public string? SessionId { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? UserId { get; set; }
    public string[] Scopes { get; set; } = Array.Empty<string>();

    /// <summary>"single" or "dual" — mirrors the SDK's PasswordMode.</summary>
    public string? PasswordMode { get; set; }

    public bool HasResumableSession =>
        !string.IsNullOrEmpty(SessionId)
        && !string.IsNullOrEmpty(AccessToken)
        && !string.IsNullOrEmpty(RefreshToken);
}
