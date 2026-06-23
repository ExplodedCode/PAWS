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

    /// <summary>"single" or "dual".</summary>
    public string PasswordMode { get; init; } = "single";
}
