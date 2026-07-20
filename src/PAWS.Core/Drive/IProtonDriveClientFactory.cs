namespace PAWS.Core.Drive;

/// <summary>
/// Creates a connected <see cref="IProtonDriveClient"/> for a configured account by resuming its
/// persisted browser-login session. Lets the app and sync engine obtain a ready-to-use Drive client
/// without depending on the concrete Proton SDK adapter.
/// </summary>
public interface IProtonDriveClientFactory
{
    /// <summary>
    /// Loads the account's stored secrets, resumes its Proton session (keys unlocked), and connects
    /// the Drive client. Throws if the account has no resumable session (the user must sign in again).
    /// The caller owns the returned client and should dispose it when done.
    /// </summary>
    Task<IProtonDriveClient> CreateAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when an account's Proton session can no longer be resumed automatically — its refresh
    /// token has been rejected as invalid/expired (discovered lazily, whenever a Drive call next needs
    /// one), so every stored token for the account has been cleared and the user must sign in again via
    /// the browser flow. Fired with the account id; may fire from a background thread and, for an
    /// account with several concurrently-active sessions (on-demand + full-sync, multiple pairs), more
    /// than once for the same underlying expiry — handlers should be idempotent.
    /// </summary>
    event Action<string>? SessionExpired;
}
