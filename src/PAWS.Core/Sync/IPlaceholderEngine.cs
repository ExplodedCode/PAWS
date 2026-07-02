namespace PAWS.Core.Sync;

/// <summary>Identifying details for a Cloud Filter sync root (the on-demand folder).</summary>
public sealed record SyncRootInfo(
    string LocalPath,
    string ProviderId,
    string ProviderName,
    string Version);

/// <summary>Outcome of populating placeholders.</summary>
public sealed record PlaceholderResult(int Created, int Skipped, IReadOnlyList<string> Errors);

/// <summary>
/// Outcome of a dehydration sweep. <see cref="Skipped"/> counts files intentionally left alone:
/// pinned ("Always keep on this device"), already dehydrated, recently used, or not yet synced
/// (dehydrating those would lose unpushed edits — the platform refuses, and so do we).
/// </summary>
public sealed record DehydrateResult(int Dehydrated, int Skipped, IReadOnlyList<string> Errors);

/// <summary>
/// Supplies a placeholder's content on demand. Given the file-identity blob stored on the placeholder
/// (the remote revision uid), write the full file content to <paramref name="output"/>. The engine then
/// serves whatever byte range Windows requested from it.
/// </summary>
public delegate Task FetchPlaceholderData(string fileIdentity, Stream output, CancellationToken cancellationToken);

/// <summary>
/// Lists the immediate children of the folder at <paramref name="relativeFolderPath"/> (relative to the
/// sync root, '/'-separated, empty string = the root itself). Called when a folder is first browsed so
/// the provider can populate that folder's placeholders on demand — enabling scalable, lazy population
/// (only browsed folders are materialized, not the whole tree up front). Each returned
/// <see cref="RemoteEntry"/> carries a full <see cref="RemoteEntry.RelativePath"/> under the sync root.
/// </summary>
public delegate Task<IReadOnlyList<RemoteEntry>> FetchFolderChildren(string relativeFolderPath, CancellationToken cancellationToken);

/// <summary>
/// Files-on-demand engine over the Windows Cloud Filter API: register a local folder as a sync root and
/// mirror a remote tree into it as on-demand placeholders (files that appear with their real size/name
/// but occupy no disk space until opened). Hydration-on-access is handled separately by the connected
/// provider. Windows-only; the implementation lives in PAWS.CloudFilter.
/// </summary>
public interface IPlaceholderEngine
{
    /// <summary>True if the Cloud Filter API is available on this OS.</summary>
    bool IsSupported { get; }

    /// <summary>Registers <paramref name="info"/>.LocalPath as a cloud sync root (idempotent).</summary>
    void RegisterSyncRoot(SyncRootInfo info);

    /// <summary>Unregisters the sync root at <paramref name="localPath"/>.</summary>
    void UnregisterSyncRoot(string localPath);

    /// <summary>
    /// Creates on-demand placeholders under <paramref name="localRoot"/> mirroring <paramref name="remoteSnapshot"/>.
    /// Each placeholder carries a file-identity blob (the remote node/revision uid) so the provider can
    /// hydrate it on access. Existing entries are left alone.
    /// </summary>
    PlaceholderResult CreatePlaceholders(string localRoot, RemoteSnapshot remoteSnapshot);

    /// <summary>
    /// Connects the provider and starts serving the folder on demand: when a folder is first enumerated,
    /// its placeholders are populated lazily via <paramref name="fetchChildren"/> (a live listing of that
    /// one folder), and opening a file invokes <paramref name="fetchData"/> to download its content.
    /// Returns a handle that disconnects the provider when disposed — the provider only serves callbacks
    /// while connected (the process must stay alive).
    /// </summary>
    IDisposable Connect(string localRoot, FetchFolderChildren fetchChildren, FetchPlaceholderData fetchData);

    /// <summary>
    /// Dehydrates ("frees up space" on) the file at <paramref name="path"/>, or every file under it when
    /// it is a folder. Skips pinned files, files already dehydrated, files used more recently than
    /// <paramref name="notUsedFor"/> (when given), and files whose local changes haven't been synced yet
    /// (the platform refuses those, so no unpushed edit can be lost). Local-only; safe while syncing.
    /// </summary>
    DehydrateResult DehydrateTree(string path, TimeSpan? notUsedFor = null);

    /// <summary>
    /// Marks a just-uploaded local file as a synced placeholder so it can be dehydrated later: a plain
    /// local file (created by the user, then pushed) is converted in place to a hydrated placeholder, and
    /// an existing placeholder (edited locally, then pushed) gets its identity refreshed to
    /// <paramref name="fileIdentity"/> (the new revision) and is marked in-sync. Best-effort — a failure
    /// only means the file stays non-dehydratable until the next full enable.
    /// </summary>
    void FinalizeUploadedFile(string fullPath, string fileIdentity);
}
