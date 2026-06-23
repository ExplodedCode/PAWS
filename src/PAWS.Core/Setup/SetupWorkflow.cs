using PAWS.Core.Abstractions;
using PAWS.Core.Configuration;
using PAWS.Core.Proton;
using PAWS.Core.Security;

namespace PAWS.Core.Setup;

/// <summary>Result of <see cref="SetupWorkflow.AddAccountAsync"/>.</summary>
public sealed record AddAccountResult(ProtonAuthResult Auth, ProtonAccount? Account)
{
    public bool IsSuccess => Auth.IsSuccess && Account is not null;
}

/// <summary>
/// Orchestrates multi-account setup and management. Authentication produces a resumable session
/// stored per account (encrypted), while folder mappings live in non-secret settings. Free of
/// UI/console I/O so it is shared by the console tool and the WinUI app, and is unit-testable.
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
    /// Authenticates and, on success, registers a NEW account. Multiple accounts are supported —
    /// even the same Proton email more than once, since accounts are keyed by a generated id.
    /// Persists the account's resumable session under its id and adds it (with an optional first
    /// folder) to settings. Nothing is written if authentication fails.
    /// </summary>
    public async Task<AddAccountResult> AddAccountAsync(
        ProtonLoginRequest login,
        SyncPair? initialPair = null,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        var auth = await _authenticator.AuthenticateAsync(login, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return new AddAccountResult(auth, null);
        }

        var session = auth.Session!;
        var account = new ProtonAccount
        {
            Email = session.Username,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
        };

        if (initialPair is not null)
        {
            account.SyncPairs.Add(initialPair);
        }

        // Persist the resumable session + the data password (for key unlock), keyed by account id.
        _secrets.SaveSecrets(account.Id, new ProtonSecrets
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

        var settings = _settings.Load();
        settings.Accounts.Add(account);
        _settings.Save(settings);

        return new AddAccountResult(auth, account);
    }

    /// <summary>Adds another folder mapping to an existing account (no re-authentication needed).</summary>
    public void AddSyncPair(string accountId, SyncPair pair)
    {
        var settings = _settings.Load();
        var account = settings.Accounts.FirstOrDefault(a => a.Id == accountId)
            ?? throw new InvalidOperationException($"Unknown account '{accountId}'.");
        account.SyncPairs.Add(pair);
        _settings.Save(settings);
    }

    /// <summary>Removes a single folder mapping from an account.</summary>
    public void RemoveSyncPair(string accountId, string pairId)
    {
        var settings = _settings.Load();
        var account = settings.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null)
        {
            return;
        }

        account.SyncPairs.RemoveAll(p => p.Id == pairId);
        _settings.Save(settings);
    }

    /// <summary>Removes an account entirely: clears its stored credentials and its configuration.</summary>
    public void RemoveAccount(string accountId)
    {
        _secrets.ClearSecrets(accountId);

        var settings = _settings.Load();
        settings.Accounts.RemoveAll(a => a.Id == accountId);
        _settings.Save(settings);
    }
}
