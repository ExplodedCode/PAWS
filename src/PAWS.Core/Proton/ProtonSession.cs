namespace PAWS.Core.Proton;

/// <summary>The resumable result of a successful Proton authentication.</summary>
public sealed class ProtonSession
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public required string Username { get; init; }
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public string[] Scopes { get; init; } = Array.Empty<string>();

    /// <summary>"single", "dual", or "web".</summary>
    public string PasswordMode { get; init; } = "single";

    /// <summary>
    /// The key/mailbox password needed to unlock encryption keys, when it is known at auth time
    /// (e.g. returned by the browser/session-fork flow). Null for the SRP path, where the caller
    /// supplies it from the login form instead.
    /// </summary>
    public string? DataPassword { get; init; }
}
