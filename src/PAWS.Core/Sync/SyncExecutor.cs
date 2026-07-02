using PAWS.Core.Diagnostics;
using PAWS.Core.Drive;

namespace PAWS.Core.Sync;

/// <summary>
/// Applies a reconciled list of <see cref="SyncOperation"/>s against the local filesystem and Proton
/// Drive (via <see cref="IProtonDriveClient"/>). Operations are assumed already ordered by the
/// reconciler (creates/transfers parent-first, deletes child-first). Each operation is independent:
/// a failure is recorded and the run continues. Conflicts are skipped unless the caller supplies a
/// <see cref="ConflictResolution"/> for their path — never auto-resolved.
///
/// Safety: downloads are written to a temp file and atomically moved into place, so a failed download
/// never leaves a half-written file. First sync (empty state) yields no deletes.
/// </summary>
public sealed class SyncExecutor(IProtonDriveClient client, TransferThrottle? throttle = null)
{
    public async Task<SyncResult> ExecuteAsync(
        string localRoot,
        RemoteNode remoteRoot,
        RemoteSnapshot remoteSnapshot,
        IReadOnlyList<SyncOperation> operations,
        IProgress<SyncProgress>? progress = null,
        IReadOnlyDictionary<string, ConflictResolution>? conflictResolutions = null,
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
                ConflictPlan? plan = null;
                if (conflictResolutions is not null
                    && conflictResolutions.TryGetValue(op.RelativePath, out var resolution))
                {
                    plan = PlanResolution(op, resolution);
                }

                if (plan is null)
                {
                    skipped++; // no decision (or unresolvable file/folder mismatch) — leave both sides alone
                    continue;
                }

                try
                {
                    if (plan.RenameLocalToConflictCopy)
                    {
                        RenameToConflictCopy(localRoot, op.RelativePath);
                    }

                    foreach (var step in plan.Operations)
                    {
                        await ApplyAsync(step, localRoot, remoteFolders, cancellationToken).ConfigureAwait(false);
                    }

                    completed++;
                }
                catch (Exception ex)
                {
                    PawsLog.Write($"Conflict resolution FAILED: \"{op.RelativePath}\"{Environment.NewLine}{ex}");
                    failures.Add(new SyncFailure(op, Describe(ex)));
                }

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

    /// <summary>
    /// Translates a user's <see cref="ConflictResolution"/> for one conflicted path into the concrete
    /// steps that carry it out — pure planning, no I/O. Returns null when the conflict can't be
    /// auto-resolved (a file on one side vs a folder on the other: rename one side manually).
    /// <para>The deletion-flavored conflicts map naturally: "deleted locally but remote changed" +
    /// KeepLocal keeps it deleted (trashes remote); "deleted remotely but local changed" + KeepRemote
    /// accepts the deletion (removes local). KeepBoth renames the local file to a conflict-copy sibling
    /// (picked up as a new file by the next sync) and gives the original name to the remote version.</para>
    /// </summary>
    public static ConflictPlan? PlanResolution(SyncOperation conflict, ConflictResolution resolution)
    {
        if (conflict.Kind != SyncOperationKind.Conflict)
        {
            return null;
        }

        // A file/folder type mismatch has no safe automatic resolution.
        if (conflict is { Remote: not null, Local: not null } && conflict.Remote.IsFolder != conflict.Local.IsFolder)
        {
            return null;
        }

        return resolution switch
        {
            ConflictResolution.KeepLocal when conflict.Local is null =>
                new ConflictPlan(false, [conflict with { Kind = SyncOperationKind.DeleteRemote, Reason = "conflict resolved: keep deleted" }]),
            ConflictResolution.KeepLocal =>
                new ConflictPlan(false, [conflict with { Kind = SyncOperationKind.UploadFile, Reason = "conflict resolved: keep this PC's version" }]),

            ConflictResolution.KeepRemote when conflict.Remote is null =>
                new ConflictPlan(false, [conflict with { Kind = SyncOperationKind.DeleteLocal, Reason = "conflict resolved: accept remote deletion" }]),
            ConflictResolution.KeepRemote =>
                new ConflictPlan(false, [conflict with { Kind = SyncOperationKind.DownloadFile, Reason = "conflict resolved: keep Drive's version" }]),

            // Keep both: the local copy moves aside (uploads as its own file next sync); the remote
            // version — when there is one — comes down under the original name.
            ConflictResolution.KeepBoth when conflict.Remote is null =>
                new ConflictPlan(conflict.Local is not null, []),
            ConflictResolution.KeepBoth =>
                new ConflictPlan(
                    conflict.Local is not null,
                    [conflict with { Kind = SyncOperationKind.DownloadFile, Local = null, Reason = "conflict resolved: keep both" }]),

            _ => null,
        };
    }

    /// <summary>
    /// Moves the local file at <paramref name="relativePath"/> aside to "name (conflict copy
    /// yyyy-MM-dd HH-mm).ext" (uniquified if needed) and returns the new full path. The copy is a brand
    /// new local file, so the next sync uploads it as its own document.
    /// </summary>
    public static string RenameToConflictCopy(string localRoot, string relativePath)
    {
        var source = LocalPath(localRoot, relativePath);
        var directory = Path.GetDirectoryName(source)!;
        var name = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm");

        var target = Path.Combine(directory, $"{name} (conflict copy {stamp}){extension}");
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(directory, $"{name} (conflict copy {stamp}) ({i}){extension}");
        }

        File.Move(source, target);
        return target;
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
                using (var stream = ThrottledWrite(File.Create(temp)))
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
                using var stream = ThrottledRead(File.OpenRead(source));

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

    // Apply the app-wide speed limits (when configured) to the streams a transfer actually moves bytes
    // through: uploads read from the local file, downloads write to the temp file.
    private Stream ThrottledRead(Stream source) => throttle?.WrapUploadSource(source) ?? source;

    private Stream ThrottledWrite(Stream destination) => throttle?.WrapDownloadDestination(destination) ?? destination;

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
