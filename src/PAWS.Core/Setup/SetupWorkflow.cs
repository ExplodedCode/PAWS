using PAWS.Core.Abstractions;
using PAWS.Core.Configuration;
using PAWS.Core.Proton;
using PAWS.Core.Security;

namespace PAWS.Core.Setup;

/// <summary>
/// Orchestrates the credential setup: authenticate, then persist the resumable session securely
/// (via <see cref="ISecretStore"/>) and the folder mapping/account in non-secret settings.
/// Kept free of UI/console I/O so it is shared by both the console setup tool and the WinUI app,
/// and is unit-testable.
/// </summary>
public sealed class SetupWorkflow
{
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly IProtonAuthenticator _authenticator;

    public SetupWorkflow(ISettingsStore settings, ISecretStore secrets, IProtonAuthenticator authenticator)
    {
        _settings = settings;
        _secrets = secrets;
        _authenticator = authenticator;
    }

    /// <summary>
    /// Authenticates and, on success, persists the session + data password (encrypted) and the
    /// sync pair + account (plain settings). Nothing is written if authentication fails.
    /// </summary>
    public async Task<ProtonAuthResult> AuthenticateAndPersistAsync(
        ProtonLoginRequest login,
        SyncPair pair,
        CancellationToken cancellationToken = default)
    {
        var result = await _authenticator.AuthenticateAsync(login, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result;
        }

        var session = result.Session!;

        // Persist the resumable session + the data password needed to unlock keys on resume.
        // We intentionally do NOT keep the raw login password beyond this (in single-password
        // accounts DataPassword equals it; in dual mode it is the separate mailbox password).
        _secrets.SaveProtonSecrets(new ProtonSecrets
        {
            Username = session.Username,
            DataPassword = login.MailboxPassword ?? login.Password,
            SessionId = session.SessionId,
            AccessToken = session.AccessToken,
            RefreshToken = session.RefreshToken,
            UserId = session.UserId,
            Scopes = session.Scopes,
            PasswordMode = session.PasswordMode,
        });

        // Persist non-secret config: account label + folder mapping (upsert by local path).
        var settings = _settings.Load();
        settings.AccountEmail = session.Username;
        settings.SyncPairs.RemoveAll(p =>
            string.Equals(p.LocalPath, pair.LocalPath, StringComparison.OrdinalIgnoreCase));
        settings.SyncPairs.Add(pair);
        settings.SetupCompleted = true;
        _settings.Save(settings);

        return result;
    }
}
