namespace PAWS.Core.Configuration;

/// <summary>
/// Non-secret PAWS configuration, persisted as <c>settings.json</c>. Holds one or more
/// <see cref="ProtonAccount"/>s, each with its own folder mappings. Anything sensitive
/// (passwords, session tokens) lives in <see cref="PAWS.Core.Abstractions.ISecretStore"/>,
/// keyed per account.
/// </summary>
public sealed class PawsSettings
{
    /// <summary>Schema version, for future migrations. v2 introduced multi-account.</summary>
    public int Version { get; set; } = 2;

    /// <summary>Proton Drive API base URL. Default taken from the Proton SDK (<c>ProtonApiDefaults.BaseUrl</c>).</summary>
    public string ApiBaseUrl { get; set; } = "https://drive-api.proton.me/";

    /// <summary>All configured accounts. Multiple are supported, including duplicate emails.</summary>
    public List<ProtonAccount> Accounts { get; set; } = new();

    /// <summary>
    /// Start PAWS automatically when the user signs in to Windows. Applied via the per-user Run registry
    /// entry (see <c>StartupRegistration</c>), kept in sync at launch and when toggled in Settings.
    /// </summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>
    /// Keep running in the tray (background sync continues) when the window is closed. When off, closing
    /// the window exits the app. Consulted by the main window's closing handler.
    /// </summary>
    public bool RunInBackground { get; set; } = true;

    /// <summary>
    /// Start background auto-sync (watchers + Drive polling) for EVERY enabled folder when the app
    /// starts — pausing a folder is session-scoped; launch flips it back to auto. When off, pause states
    /// persist across restarts and nothing syncs in the background until started manually (on-demand
    /// folders still connect so they stay browsable/hydratable).
    /// </summary>
    public bool AutoSyncOnLaunch { get; set; } = true;

    /// <summary>
    /// Automatically dehydrate ("free up space" on) on-demand files not used for this many days, checked
    /// at app startup. Null = off. Files marked "Always keep on this device" (pinned) and files with
    /// unpushed local changes are never dehydrated.
    /// </summary>
    public int? AutoDehydrateDays { get; set; } = 14;

    /// <summary>
    /// Upload speed cap in KB/s; null = unlimited. Enforced by <c>TransferThrottle</c> in every upload
    /// path; changes apply immediately, including to in-flight transfers.
    /// </summary>
    public int? UploadLimitKBps { get; set; }

    /// <summary>Download speed cap in KB/s; null = unlimited. Enforced like the upload cap, including on-demand hydration.</summary>
    public int? DownloadLimitKBps { get; set; }

    /// <summary>True once at least one account has been added.</summary>
    public bool HasAnyAccount => Accounts.Count > 0;
}
