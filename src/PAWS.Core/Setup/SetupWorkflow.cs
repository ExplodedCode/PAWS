using PAWS.Core.Abstractions;
using PAWS.Core.Configuration;
using PAWS.Core.Proton;
using PAWS.Core.Security;

namespace PAWS.Core.Setup;

/// <summary>Result of <see cref="SetupWorkflow.AddAccount"/>.</summary>
public sealed record AddAccountResult(ProtonAuthResult Auth, ProtonAccount? Account)
{
    public bool IsSuccess => Auth.IsSuccess && Account is not null;
}

/// <summary>
/// Orchestrates multi-account setup and management. Authentication (always browser/session-fork)
/// produces a resumable session stored per account (encrypted), while folder mappings live in
/// non-secret settings. Free of UI/console I/O so it is shared by the console tool and the WinUI
/// app, and is unit-testable.
/// </summary>
public sealed class SetupWorkflow
{
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;

    public SetupWorkflow(ISettingsStore settings, ISecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    /// <summary>
    /// Registers a NEW account from an already-authenticated <see cref="ProtonSession"/> obtained via
    /// the browser/session-fork flow. Persists its session + key password and adds it to settings.
    /// Multiple accounts are supported — even the same Proton email more than once, since accounts are
    /// keyed by a generated id.
    /// </summary>
    public AddAccountResult AddAccount(ProtonSession session, SyncPair? initialPair = null, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var account = new ProtonAccount
        {
            Email = session.Username,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
        };

        if (initialPair is not null)
        {
            account.SyncPairs.Add(initialPair);
        }

        _secrets.SaveSecrets(account.Id, new ProtonSecrets
        {
            Username = session.Username,
            DataPassword = session.DataPassword,
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

        return new AddAccountResult(ProtonAuthResult.Success(session), account);
    }

    /// <summary>
    /// Re-saves an existing account's session (fresh tokens + key password) after another browser
    /// login, without creating a new account or disturbing its folder mappings. Use this to recover a
    /// session whose refresh token has expired/rotated.
    /// </summary>
    public void RefreshAccountSession(string accountId, ProtonSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var settings = _settings.Load();
        if (settings.Accounts.All(a => a.Id != accountId))
        {
            throw new InvalidOperationException($"Unknown account '{accountId}'.");
        }

        _secrets.SaveSecrets(accountId, new ProtonSecrets
        {
            Username = session.Username,
            DataPassword = session.DataPassword,
            SessionId = session.SessionId,
            AccessToken = session.AccessToken,
            RefreshToken = session.RefreshToken,
            UserId = session.UserId,
            Scopes = session.Scopes,
            PasswordMode = session.PasswordMode,
        });
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

    /// <summary>Updates the auto-sync preference for a folder mapping and persists it.</summary>
    public void SetPairAutoSync(string accountId, string pairId, bool autoSync)
    {
        var settings = _settings.Load();
        var pair = settings.Accounts
            .FirstOrDefault(a => a.Id == accountId)?.SyncPairs
            .FirstOrDefault(p => p.Id == pairId);
        if (pair is null)
        {
            return;
        }

        pair.AutoSync = autoSync;
        _settings.Save(settings);
    }

    /// <summary>Updates a folder mapping's per-pair speed-limit overrides (see <see cref="SyncPair.UploadLimitKBps"/>) and persists them.</summary>
    public void SetPairSpeedLimits(string accountId, string pairId, int? uploadLimitKBps, int? downloadLimitKBps)
    {
        var settings = _settings.Load();
        var pair = settings.Accounts
            .FirstOrDefault(a => a.Id == accountId)?.SyncPairs
            .FirstOrDefault(p => p.Id == pairId);
        if (pair is null)
        {
            return;
        }

        pair.UploadLimitKBps = uploadLimitKBps;
        pair.DownloadLimitKBps = downloadLimitKBps;
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
