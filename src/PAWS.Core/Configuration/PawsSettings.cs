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
    /// Start PAWS automatically when the user signs in to Windows. NOT YET ENFORCED — autostart
    /// registration (StartupTask) is planned; the preference is captured now.
    /// </summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>
    /// Keep running in the tray (background sync) when the window is closed or minimized. The tray
    /// behavior is currently always on; this preference will control it in an upcoming build.
    /// </summary>
    public bool RunInBackground { get; set; } = true;

    /// <summary>
    /// Automatically start syncing all accounts when the app starts. Launch auto-sync currently follows
    /// each folder's own auto-sync flag; this app-wide switch will gate it in an upcoming build.
    /// </summary>
    public bool AutoSyncOnLaunch { get; set; } = true;

    /// <summary>
    /// Upload speed cap in KB/s; null = unlimited. Persisted app-wide. NOT YET ENFORCED — the transfer
    /// throttle is planned; the setting exists so the UI can capture the preference now.
    /// </summary>
    public int? UploadLimitKBps { get; set; }

    /// <summary>Download speed cap in KB/s; null = unlimited. Same not-yet-enforced caveat as upload.</summary>
    public int? DownloadLimitKBps { get; set; }

    /// <summary>True once at least one account has been added.</summary>
    public bool HasAnyAccount => Accounts.Count > 0;
}
