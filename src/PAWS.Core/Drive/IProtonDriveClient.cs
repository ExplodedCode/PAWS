namespace PAWS.Core.Drive;

/// <summary>
/// Progress of a single file transfer. <see cref="TotalBytes"/> may be 0 if the size is unknown
/// until the transfer starts.
/// </summary>
public readonly record struct TransferProgress(long BytesTransferred, long TotalBytes);

/// <summary>
/// Port for the Proton Drive file operations PAWS's sync engine needs. The adapter (in PAWS.Proton)
/// wraps Proton's official <c>Proton.Sdk.Drive.ProtonDriveClient</c>; this interface keeps Core free of
/// SDK and crypto types so the reconciler and the (planned) Cloud Filter engine can be unit-tested
/// against a fake.
///
/// Identity model follows rclone's path-addressed view: a <see cref="RemoteNode"/> is resolved once and
/// then passed back in for subsequent operations. All operations assume a connected client
/// (<see cref="ConnectAsync"/> first).
/// </summary>
public interface IProtonDriveClient : IAsyncDisposable
{
    /// <summary>
    /// Establishes the Drive context for the authenticated session: selects the user's volume and root
    /// share so subsequent calls have a share to act within. Must be called before any other method.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>The root folder of the user's main Drive volume.</summary>
    Task<RemoteNode> GetRootAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a Drive path (e.g. <c>"/Documents/Work"</c>, slashes or backslashes, leading slash
    /// optional; empty/"/" means the root) to its node, or null if any segment is missing.
    /// </summary>
    Task<RemoteNode?> ResolvePathAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Lists the immediate children of a folder (active, non-hidden nodes).</summary>
    IAsyncEnumerable<RemoteNode> ListChildrenAsync(RemoteNode folder, CancellationToken cancellationToken = default);

    /// <summary>Downloads the active revision of <paramref name="file"/> into <paramref name="destination"/>.</summary>
    Task DownloadAsync(RemoteNode file, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads <paramref name="content"/> as a NEW file named <paramref name="name"/> under
    /// <paramref name="parentFolder"/>. Throws if a file with that name already exists — use
    /// <see cref="UploadRevisionAsync"/> to update an existing file. <paramref name="content"/> must be
    /// readable and seekable (its length is needed).
    /// </summary>
    Task<RemoteNode> UploadAsync(
        RemoteNode parentFolder,
        string name,
        Stream content,
        DateTimeOffset? lastModifiedUtc = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads <paramref name="content"/> as a new revision of an existing file (<paramref name="existingFile"/>
    /// must carry a current <see cref="RemoteNode.RevisionUid"/>). Use this for the "local file changed"
    /// case, where the remote node already exists.
    /// </summary>
    Task<RemoteNode> UploadRevisionAsync(
        RemoteNode existingFile,
        Stream content,
        DateTimeOffset? lastModifiedUtc = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a sub-folder under <paramref name="parentFolder"/>.</summary>
    Task<RemoteNode> CreateFolderAsync(RemoteNode parentFolder, string name, CancellationToken cancellationToken = default);

    /// <summary>Renames a node in place.</summary>
    Task RenameAsync(RemoteNode node, string newName, CancellationToken cancellationToken = default);

    /// <summary>Moves a node into <paramref name="newParent"/>, optionally renaming it.</summary>
    Task MoveAsync(RemoteNode node, RemoteNode newParent, string? nameAtDestination = null, CancellationToken cancellationToken = default);

    /// <summary>Moves a node to the trash (recoverable). Use for the local-delete → remote propagation.</summary>
    Task TrashAsync(RemoteNode node, CancellationToken cancellationToken = default);

    /// <summary>
    /// The drive.proton.me web-app URL that shows <paramref name="node"/> (deep link into the folder or
    /// file), or null if it cannot be determined — callers should fall back to the site root.
    /// </summary>
    Task<string?> GetWebUrlAsync(RemoteNode node, CancellationToken cancellationToken = default);
}
