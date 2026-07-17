using PAWS.Core.Abstractions;
using PAWS.Core.Drive;
using PAWS.Core.Proton;

namespace PAWS.Proton.Drive;

/// <summary>
/// Builds a connected <see cref="IProtonDriveClient"/> from an account's persisted secrets: load the
/// DPAPI-encrypted <c>ProtonSecrets</c>, reconstruct the resumable <see cref="ProtonSession"/>, resume
/// the SDK session (unlocking keys from the stored key password), and connect the Drive adapter.
/// </summary>
public sealed class ProtonDriveClientFactory(ISecretStore secretStore) : IProtonDriveClientFactory
{
    // Serializes the read-modify-write when persisting rotated (or cleared) tokens — the refresh/expiry
    // events can fire on a background thread, and multiple clients for the same account may be active
    // at once (on-demand + full-sync, or several sync pairs, each resuming their own session).
    private readonly object _persistLock = new();

    public event Action<string>? SessionExpired;

    public async Task<IProtonDriveClient> CreateAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var secrets = secretStore.LoadSecrets(accountId)
            ?? throw new InvalidOperationException($"No stored credentials for account '{accountId}'.");

        if (!secrets.HasResumableSession)
        {
            throw new InvalidOperationException($"Account '{accountId}' has no resumable session — please sign in again.");
        }

        var session = new ProtonSession
        {
            SessionId = secrets.SessionId!,
            UserId = secrets.UserId ?? secrets.SessionId!,
            Username = secrets.Username,
            AccessToken = secrets.AccessToken!,
            RefreshToken = secrets.RefreshToken!,
            Scopes = secrets.Scopes,
            PasswordMode = secrets.PasswordMode ?? "web",
            DataPassword = secrets.DataPassword,
        };

        var apiSession = await ProtonSessionConnector.ResumeAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Proton rotates refresh tokens on every use (the previous one is invalidated server-side). The
        // SDK refreshes the short-lived access token transparently; we must persist the rotated pair, or
        // the NEXT resume reuses a now-dead refresh token and fails with "Invalid refresh token".
        apiSession.TokenCredential.TokensRefreshed += (accessToken, refreshToken)
            => PersistRotatedTokens(accountId, accessToken, refreshToken);

        // The refresh token itself has been rejected as invalid (fires once the SDK actually attempts —
        // and fails — a refresh; it does NOT poll ahead of time). No automatic recovery is possible past
        // this point — the user must complete the browser sign-in flow again.
        apiSession.TokenCredential.RefreshTokenExpired += () => HandleSessionExpired(accountId);

        var client = new ProtonDriveClientAdapter(apiSession);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    private void PersistRotatedTokens(string accountId, string accessToken, string refreshToken)
    {
        lock (_persistLock)
        {
            var current = secretStore.LoadSecrets(accountId);
            if (current is null)
            {
                return;
            }

            current.AccessToken = accessToken;
            current.RefreshToken = refreshToken;
            secretStore.SaveSecrets(accountId, current);
        }
    }

    // Clears the dead tokens (so HasResumableSession correctly reports false and a NEXT CreateAsync
    // fails immediately with a clear "sign in again" message, instead of wasting a resume + first-call
    // round trip on a token already known not to work) and tells the app so it can prompt the user
    // proactively rather than waiting for them to notice a cryptic sync failure.
    private void HandleSessionExpired(string accountId)
    {
        lock (_persistLock)
        {
            var current = secretStore.LoadSecrets(accountId);
            if (current is null)
            {
                return;
            }

            current.AccessToken = null;
            current.RefreshToken = null;
            secretStore.SaveSecrets(accountId, current);
        }

        SessionExpired?.Invoke(accountId);
    }
}
