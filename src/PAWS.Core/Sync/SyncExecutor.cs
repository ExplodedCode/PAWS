using PAWS.Core.Diagnostics;
using PAWS.Core.Drive;

namespace PAWS.Core.Sync;

/// <summary>
/// Applies a reconciled list of <see cref="SyncOperation"/>s against the local filesystem and Proton
/// Drive (via <see cref="IProtonDriveClient"/>). Operations are assumed already ordered by the
/// reconciler (creates/transfers parent-first, deletes child-first). Each operation is independent:
/// a failure is recorded and the run continues. Conflicts are skipped (never auto-resolved).
///
/// Safety: downloads are written to a temp file and atomically moved into place, so a failed download
/// never leaves a half-written file. First sync (empty state) yields no deletes.
/// </summary>
public sealed class SyncExecutor(IProtonDriveClient client)
{
    public async Task<SyncResult> ExecuteAsync(
        string localRoot,
        RemoteNode remoteRoot,
        RemoteSnapshot remoteSnapshot,
        IReadOnlyList<SyncOperation> operations,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Relative folder path -> remote node: existing folders from the snapshot, plus ones we create
        // as we go (the reconciler ordered CreateRemoteFolder ops parent-first, so lookups resolve).
        var remoteFolders = new Dictionary<string, RemoteNode>(StringComparer.Ordinal) { [string.Empty] = remoteRoot };
        foreach (var entry in remoteSnapshot.Entries)
        {
            if (entry.IsFolder)
            {
                remoteFolders[entry.RelativePath] = ToNode(entry);
            }
        }

        var completed = 0;
        var skipped = 0;
        var failures = new List<SyncFailure>();

        for (var i = 0; i < operations.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var op = operations[i];
            progress?.Report(new SyncProgress(i, operations.Count, op));

            if (op.Kind == SyncOperationKind.Conflict)
            {
                skipped++;
                continue;
            }

            try
            {
                await ApplyAsync(op, localRoot, remoteFolders, cancellationToken).ConfigureAwait(false);
                completed++;
            }
            catch (Exception ex)
            {
                // Full detail (type + message chain + stack) to the log; a concise chain to the UI.
                PawsLog.Write($"Sync op FAILED: {op.Kind} \"{op.RelativePath}\"{Environment.NewLine}{ex}");
                failures.Add(new SyncFailure(op, Describe(ex)));
            }
        }

        return new SyncResult { Completed = completed, Skipped = skipped, Failures = failures };
    }

    // A concise one-line description that follows the InnerException chain — the top-level message alone
    // (e.g. "Error while copying content to a stream") usually hides the real cause underneath.
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            parts.Add($"{e.GetType().Name}: {e.Message}");
        }

        return string.Join(" → ", parts);
    }

    private async Task ApplyAsync(
        SyncOperation op,
        string localRoot,
        Dictionary<string, RemoteNode> remoteFolders,
        CancellationToken cancellationToken)
    {
        switch (op.Kind)
        {
            case SyncOperationKind.CreateLocalFolder:
                Directory.CreateDirectory(LocalPath(localRoot, op.RelativePath));
                break;

            case SyncOperationKind.DownloadFile:
            {
                var target = LocalPath(localRoot, op.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                var temp = target + ".paws-partial";
                using (var stream = File.Create(temp))
                {
                    await client.DownloadAsync(ToNode(op.Remote!), stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                File.Move(temp, target, overwrite: true);

                if (op.Remote!.ModifiedUtc is { } modified)
                {
                    File.SetLastWriteTimeUtc(target, modified.UtcDateTime);
                }

                break;
            }

            case SyncOperationKind.CreateRemoteFolder:
            {
                var parent = remoteFolders[ParentPath(op.RelativePath)];
                var created = await client.CreateFolderAsync(parent, NameOf(op.RelativePath), cancellationToken).ConfigureAwait(false);
                remoteFolders[op.RelativePath] = created;
                break;
            }

            case SyncOperationKind.UploadFile:
            {
                var source = LocalPath(localRoot, op.RelativePath);
                using var stream = File.OpenRead(source);

                if (op.Remote is { RevisionUid: not null } existing)
                {
                    // The remote file already exists and changed locally — upload a new revision.
                    await client.UploadRevisionAsync(ToNode(existing), stream, op.Local?.ModifiedUtc, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Brand-new file — create it under its parent folder.
                    var parent = remoteFolders[ParentPath(op.RelativePath)];
                    await client.UploadAsync(parent, NameOf(op.RelativePath), stream, op.Local?.ModifiedUtc, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            case SyncOperationKind.DeleteLocal:
            {
                var target = LocalPath(localRoot, op.RelativePath);
                if (op.IsFolder)
                {
                    if (Directory.Exists(target))
                    {
                        Directory.Delete(target, recursive: true);
                    }
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }

                break;
            }

            case SyncOperationKind.DeleteRemote:
                await client.TrashAsync(ToNode(op.Remote!), cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static RemoteNode ToNode(RemoteEntry e) => new()
    {
        Uid = e.Uid,
        ParentUid = e.ParentUid,
        Name = e.Name,
        IsFolder = e.IsFolder,
        Size = e.Size,
        ModifiedUtc = e.ModifiedUtc,
        RevisionUid = e.RevisionUid,
    };

    private static string LocalPath(string root, string relativePath)
        => Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ParentPath(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    private static string NameOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }
}
