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
}
