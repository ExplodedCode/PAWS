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
/// Supplies a placeholder's content on demand. Given the file-identity blob stored on the placeholder
/// (the remote revision uid), write the full file content to <paramref name="output"/>. The engine then
/// serves whatever byte range Windows requested from it.
/// </summary>
public delegate Task FetchPlaceholderData(string fileIdentity, Stream output, CancellationToken cancellationToken);

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
    /// Connects the provider and starts serving the folder on demand: directory enumeration is populated
    /// lazily from <paramref name="snapshot"/>, and opening a file invokes <paramref name="fetchData"/>
    /// to download its content. Returns a handle that disconnects the provider when disposed — the
    /// provider only serves callbacks while connected (the process must stay alive).
    /// </summary>
    IDisposable Connect(string localRoot, RemoteSnapshot snapshot, FetchPlaceholderData fetchData);
}
