namespace PAWS.Core.Configuration;

/// <summary>How a sync pair materializes cloud files on the local disk.</summary>
public enum SyncMode
{
    /// <summary>
    /// OneDrive-style: cloud files appear as on-demand placeholders and are hydrated
    /// (downloaded) only when opened. This is PAWS's headline mode.
    /// </summary>
    OnDemand,

    /// <summary>Every file is kept fully local and in the cloud (classic Dropbox/rclone behavior).</summary>
    FullSync,

    /// <summary>Files live only in the cloud; nothing is downloaded unless explicitly pinned.</summary>
    CloudOnly,
}
