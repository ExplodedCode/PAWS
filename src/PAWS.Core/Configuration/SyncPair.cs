namespace PAWS.Core.Configuration;

/// <summary>A single mapping between a local Windows folder and a Proton Drive folder.</summary>
public sealed class SyncPair
{
    /// <summary>Stable id for this pair. Later reused as the Cloud Filter sync-root identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>Absolute local path, e.g. <c>C:\Users\me\ProtonSync</c>.</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>
    /// Path of the folder inside Proton Drive, e.g. <c>/Backup/Desktop</c>.
    /// <c>/</c> is the Drive root. This is a path on the main volume, NOT a per-device folder.
    /// </summary>
    public string RemotePath { get; set; } = "/";

    public SyncMode Mode { get; set; } = SyncMode.OnDemand;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, PAWS watches the local folder and automatically pushes changes up to Drive shortly
    /// after they settle (on-demand pairs), instead of waiting for a manual "Sync up". Persisted so the
    /// watcher is re-established on the next launch.
    /// </summary>
    public bool AutoSync { get; set; }

    /// <summary>
    /// Per-folder upload speed override in KB/s, using the same convention as the app-wide setting:
    /// <see langword="null"/> = inherit the app-wide limit (the default for every pair), <c>0</c> =
    /// explicitly unlimited for this folder even if the app-wide setting is capped, positive = a custom
    /// cap just for this folder.
    /// </summary>
    public int? UploadLimitKBps { get; set; }

    /// <summary>Per-folder download speed override in KB/s — same convention as <see cref="UploadLimitKBps"/>.</summary>
    public int? DownloadLimitKBps { get; set; }
}
