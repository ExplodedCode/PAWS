using System.Runtime.CompilerServices;
using PAWS.Core.Drive;
using Proton.Sdk;
using Proton.Drive.Sdk;
using Proton.Drive.Sdk.Nodes;
using Proton.Drive.Sdk.Nodes.Upload;

namespace PAWS.Proton.Drive;

/// <summary>
/// Adapter implementing PAWS's <see cref="IProtonDriveClient"/> port over Proton's official
/// <see cref="ProtonDriveClient"/>. The user's syncable tree hangs off the "My files" folder
/// (<see cref="ConnectAsync"/>), and every node is addressed by its composite <see cref="NodeUid"/> —
/// no share/volume bookkeeping needed. Translates between SDK <see cref="Node"/>s and PAWS's
/// opaque-string <see cref="RemoteNode"/>.
/// </summary>
public sealed class ProtonDriveClientAdapter(ProtonApiSession session) : IProtonDriveClient
{
    private readonly ProtonDriveClient _client = new(session);

    private RemoteNode? _root;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var myFiles = await _client.GetMyFilesFolderAsync(cancellationToken).ConfigureAwait(false);
        _root = Map(myFiles);
    }

    public Task<RemoteNode> GetRootAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return Task.FromResult(_root!);
    }

    public async Task<RemoteNode?> ResolvePathAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var segments = (remotePath ?? string.Empty).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        var current = _root!;
        for (var i = 0; i < segments.Length; i++)
        {
            RemoteNode? match = null;
            await foreach (var child in ListChildrenAsync(current, cancellationToken).ConfigureAwait(false))
            {
                // Proton Drive names are case-sensitive.
                if (string.Equals(child.Name, segments[i], StringComparison.Ordinal))
                {
                    match = child;
                    break;
                }
            }

            // Missing segment, or an intermediate segment that is a file rather than a folder.
            if (match is null || (i < segments.Length - 1 && !match.IsFolder))
            {
                return null;
            }

            current = match;
        }

        return current;
    }

    public async IAsyncEnumerable<RemoteNode> ListChildrenAsync(
        RemoteNode folder,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var folderUid = NodeUid.Parse(folder.Uid);
        await foreach (var node in _client.EnumerateFolderChildrenAsync(folderUid, cancellationToken).ConfigureAwait(false))
        {
            // Only active files and folders: the children listing can also return trashed nodes
            // (TrashTime set), draft files (FileDraftNode), and photos — none of which we sync.
            if (node.TrashTime is null && node is FolderNode or FileNode)
            {
                yield return Map(node);
            }
        }
    }

    public async Task DownloadAsync(
        RemoteNode file,
        Stream destination,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        if (file.RevisionUid is null)
        {
            throw new InvalidOperationException($"'{file.Name}' is not a file with a downloadable revision.");
        }

        var revisionUid = RevisionUid.Parse(file.RevisionUid);

        using var downloader = await _client.GetFileDownloaderAsync(revisionUid, cancellationToken).ConfigureAwait(false);
        await using var controller = downloader.DownloadToStream(destination, DownloadProgress(progress), cancellationToken);
        await controller.Completion.ConfigureAwait(false);
    }

    public async Task<RemoteNode> UploadAsync(
        RemoteNode parentFolder,
        string name,
        Stream content,
        DateTimeOffset? lastModifiedUtc = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        if (!content.CanSeek)
        {
            throw new ArgumentException("Upload content stream must be seekable (its length is needed up front).", nameof(content));
        }

        var parentUid = NodeUid.Parse(parentFolder.Uid);
        var size = content.Length;
        var metadata = new FileUploadMetadata { LastModificationTime = lastModifiedUtc };

        // overrideExistingDraftByOtherClient: true — an upload interrupted earlier (a crash, a lost
        // connection, a killed process) leaves an incomplete DRAFT node on Drive holding this name. Drafts
        // are invisible to our listings, so the reconciler keeps treating the file as new and re-calls this
        // create-new-file path; with `false` it would collide forever with the orphaned draft
        // (NodeWithSameNameExistsException). `true` lets the retry take over the stale draft. It does NOT
        // affect a COMPLETED file of the same name (that still requires a revision upload), so it can't
        // clobber real remote content.
        var uploader = await _client.GetFileUploaderAsync(
            parentUid, name, GuessMediaType(name), size, metadata, overrideExistingDraftByOtherClient: true, cancellationToken).ConfigureAwait(false);

        var result = await CompleteUploadAsync(uploader, content, progress, cancellationToken).ConfigureAwait(false);

        return new RemoteNode
        {
            Uid = result.NodeUid.ToString(),
            ParentUid = parentFolder.Uid,
            Name = name,
            IsFolder = false,
            Size = size,
            ModifiedUtc = lastModifiedUtc,
            RevisionUid = result.RevisionUid.ToString(),
        };
    }

    public async Task<RemoteNode> UploadRevisionAsync(
        RemoteNode existingFile,
        Stream content,
        DateTimeOffset? lastModifiedUtc = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        if (existingFile.RevisionUid is null)
        {
            throw new InvalidOperationException($"'{existingFile.Name}' has no current revision to update.");
        }

        if (!content.CanSeek)
        {
            throw new ArgumentException("Upload content stream must be seekable (its length is needed up front).", nameof(content));
        }

        var size = content.Length;
        var metadata = new FileUploadMetadata { LastModificationTime = lastModifiedUtc };
        var currentRevision = RevisionUid.Parse(existingFile.RevisionUid);

        // Uploads a NEW revision of the existing node (vs. GetFileUploaderAsync, which creates a new
        // file and would collide with the existing name — NodeWithSameNameExistsException).
        var uploader = await _client.GetFileRevisionUploaderAsync(currentRevision, size, metadata, cancellationToken).ConfigureAwait(false);

        var result = await CompleteUploadAsync(uploader, content, progress, cancellationToken).ConfigureAwait(false);

        return new RemoteNode
        {
            Uid = result.NodeUid.ToString(),
            ParentUid = existingFile.ParentUid,
            Name = existingFile.Name,
            IsFolder = false,
            Size = size,
            ModifiedUtc = lastModifiedUtc,
            RevisionUid = result.RevisionUid.ToString(),
        };
    }

    private static async Task<UploadResult> CompleteUploadAsync(
        FileUploader uploader, Stream content, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        using (uploader)
        {
            await using var controller = uploader.UploadFromStream(content, [], UploadProgress(progress), expectedSha1Provider: null, forPhotos: false, cancellationToken);
            return await controller.Completion.ConfigureAwait(false);
        }
    }

    public async Task<RemoteNode> CreateFolderAsync(RemoteNode parentFolder, string name, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var folder = await _client.CreateFolderAsync(NodeUid.Parse(parentFolder.Uid), name, lastModificationTime: null, cancellationToken).ConfigureAwait(false);
        return Map(folder);
    }

    public Task RenameAsync(RemoteNode node, string newName, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return _client.RenameNodeAsync(NodeUid.Parse(node.Uid), newName, GuessMediaType(newName), cancellationToken).AsTask();
    }

    public async Task MoveAsync(RemoteNode node, RemoteNode newParent, string? nameAtDestination = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var uid = NodeUid.Parse(node.Uid);
        await _client.MoveNodesAsync([uid], NodeUid.Parse(newParent.Uid), cancellationToken).ConfigureAwait(false);

        // The batch move endpoint doesn't rename; do it as a follow-up (the uid is unchanged by a move).
        if (nameAtDestination is not null && !string.Equals(nameAtDestination, node.Name, StringComparison.Ordinal))
        {
            await _client.RenameNodeAsync(uid, nameAtDestination, GuessMediaType(nameAtDestination), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task TrashAsync(RemoteNode node, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var uid = NodeUid.Parse(node.Uid);
        var results = await _client.TrashNodesAsync([uid], cancellationToken).ConfigureAwait(false);

        // TrashNodesAsync reports per-node success/failure rather than throwing — surface failures.
        if (results.TryGetValue(uid, out var result) && result.TryGetError(out var error))
        {
            throw new InvalidOperationException($"Failed to trash '{node.Name}': {error.Message}", error);
        }
    }

    public ValueTask DisposeAsync()
    {
        // The ProtonApiSession is owned by the caller (connector/app); nothing adapter-owned to release.
        return ValueTask.CompletedTask;
    }

    private static RemoteNode Map(Node node)
    {
        var name = node.Name.TryGetValue(out var decoded) ? decoded : "(unreadable name)";

        long? size = null;
        DateTimeOffset? modified = null;
        string? revisionUid = null;

        if (node is FileNode file)
        {
            var revision = file.ActiveRevision;
            revisionUid = revision.Uid.ToString();
            size = revision.ClaimedSize ?? revision.SizeOnCloudStorage;
            modified = revision.ClaimedModificationTime is { } m
                ? new DateTimeOffset(DateTime.SpecifyKind(m, DateTimeKind.Utc))
                : null;
        }

        return new RemoteNode
        {
            Uid = node.Uid.ToString(),
            ParentUid = node.ParentUid?.ToString(),
            Name = name,
            IsFolder = node is FolderNode,
            Size = size,
            ModifiedUtc = modified,
            RevisionUid = revisionUid,
        };
    }

    private void EnsureConnected()
    {
        if (_root is null)
        {
            throw new InvalidOperationException($"{nameof(ConnectAsync)} must be called before using the Drive client.");
        }
    }

    private static Action<long, long>? UploadProgress(IProgress<TransferProgress>? progress)
        => progress is null ? null : (transferred, total) => progress.Report(new TransferProgress(transferred, total));

    private static Action<long, long?> DownloadProgress(IProgress<TransferProgress>? progress)
        => (transferred, total) => progress?.Report(new TransferProgress(transferred, total ?? 0));

    private static string GuessMediaType(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
}
