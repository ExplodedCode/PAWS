namespace PAWS.Core.Proton;

/// <summary>Inputs gathered to perform an initial Proton login.</summary>
public sealed class ProtonLoginRequest
{
    public required string Username { get; init; }

    public required string Password { get; init; }

    /// <summary>One-time 2FA (TOTP) code, if the account has two-factor authentication enabled.</summary>
    public string? TwoFactorCode { get; init; }

    /// <summary>
    /// Separate mailbox/data password for two-password-mode accounts. Null for single-password
    /// accounts, where the login password also unlocks the encryption keys.
    /// </summary>
    public string? MailboxPassword { get; init; }
}
