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
}
