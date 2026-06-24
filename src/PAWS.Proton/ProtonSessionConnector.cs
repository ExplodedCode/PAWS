using PAWS.Core.Proton;
using Proton.Sdk;
using Proton.Sdk.Authentication;
using Proton.Sdk.Caching;
using Proton.Sdk.Users;

namespace PAWS.Proton;

/// <summary>
/// Rebuilds a live <see cref="ProtonApiSession"/> from a persisted browser/session-fork login, so the
/// Drive client can act on the user's behalf. There is no password/SRP login path: PAWS only ever
/// resumes a session that was originally established in the browser (see <c>WebProtonAuthenticator</c>).
///
/// Key unlock uses the fork's <c>keyPassword</c> directly via <see cref="ForkSecretCacheRepository"/>,
/// NOT the SDK's <c>ApplyDataPasswordAsync</c> (which fetches per-key salts and re-derives — forbidden
/// for an external-drive session, and unnecessary since the fork already gives the derived secret).
/// </summary>
public static class ProtonSessionConnector
{
    // Third-party Drive clients use the "external-drive-<name>@x.y.z-stable" app-version convention.
    private const string DefaultAppVersion = "external-drive-paws@1.0.0-stable";

    /// <summary>
    /// Resumes the SDK session from a stored <see cref="ProtonSession"/> (tokens + key password), with
    /// its encryption keys ready to unlock, so it can construct a <c>ProtonDriveClient</c>.
    /// </summary>
    public static Task<ProtonApiSession> ResumeAsync(
        ProtonSession stored,
        string? appVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (string.IsNullOrEmpty(stored.DataPassword))
        {
            throw new InvalidOperationException("Stored session has no key password; cannot unlock Drive encryption keys.");
        }

        // Secret cache answers key-passphrase lookups with the fork's keyPassword; entity cache holds
        // node metadata. Both in-memory, so the SQLite cache path is never exercised.
        var options = new ProtonClientOptions { EntityCacheRepository = new InMemoryCacheRepository() };

        var session = ProtonApiSession.Resume(
            (SessionId)stored.SessionId,
            stored.Username,
            (UserId)stored.UserId,
            stored.AccessToken,
            stored.RefreshToken,
            stored.Scopes,
            isWaitingForSecondFactorCode: false,
            MapPasswordMode(stored.PasswordMode),
            string.IsNullOrWhiteSpace(appVersion) ? DefaultAppVersion : appVersion,
            new ForkSecretCacheRepository(stored.DataPassword),
            options);

        return Task.FromResult(session);
    }

    // Browser-forked sessions are stored as "web"; the key password unlocks regardless, so treat
    // anything that isn't an explicit two-password account as single-password mode.
    private static PasswordMode MapPasswordMode(string? mode)
        => string.Equals(mode, "dual", StringComparison.OrdinalIgnoreCase) ? PasswordMode.Dual : PasswordMode.Single;
}
