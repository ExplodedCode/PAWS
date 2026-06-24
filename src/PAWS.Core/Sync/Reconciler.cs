namespace PAWS.Core.Sync;

/// <summary>
/// The sync brain: a pure three-way diff of the current remote snapshot, the current local snapshot,
/// and the last-known <see cref="SyncState"/>, producing the <see cref="SyncOperation"/>s that would
/// make the two sides consistent. No I/O — fully deterministic and unit-testable. The executor applies
/// the operations and then records a new <see cref="SyncState"/>.
///
/// Change detection: a remote file changed if its active-revision UID differs from last sync; a local
/// file changed if its size or last-write time differs. Conflicts (both sides changed, or both created
/// different content) are flagged rather than auto-resolved.
/// </summary>
public sealed class Reconciler
{
    // Filesystem timestamps lose precision and can skew slightly; treat near-equal times as unchanged.
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    public IReadOnlyList<SyncOperation> Reconcile(RemoteSnapshot remote, LocalSnapshot local, SyncState lastKnown)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(lastKnown);

        var remoteByPath = remote.Entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var localByPath = local.Entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var stateByPath = lastKnown.Entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);

        var allPaths = new HashSet<string>(StringComparer.Ordinal);
        allPaths.UnionWith(remoteByPath.Keys);
        allPaths.UnionWith(localByPath.Keys);
        allPaths.UnionWith(stateByPath.Keys);

        var ops = new List<SyncOperation>();

        foreach (var path in allPaths)
        {
            var hasRemote = remoteByPath.TryGetValue(path, out var r);
            var hasLocal = localByPath.TryGetValue(path, out var l);
            var hasState = stateByPath.TryGetValue(path, out var s);

            // A path that is a folder on one live side and a file on the other can't be merged.
            if (hasRemote && hasLocal && r!.IsFolder != l!.IsFolder)
            {
                ops.Add(Conflict(path, r!.IsFolder, "remote and local disagree on file vs folder", r, l));
                continue;
            }

            switch (hasRemote, hasLocal, hasState)
            {
                case (true, false, false): // appeared on remote since last sync
                    ops.Add(r!.IsFolder
                        ? Op(SyncOperationKind.CreateLocalFolder, path, true, "new folder on remote", remote: r)
                        : Op(SyncOperationKind.DownloadFile, path, false, "new file on remote", remote: r));
                    break;

                case (false, true, false): // appeared on local since last sync
                    ops.Add(l!.IsFolder
                        ? Op(SyncOperationKind.CreateRemoteFolder, path, true, "new folder on local", local: l)
                        : Op(SyncOperationKind.UploadFile, path, false, "new file on local", local: l));
                    break;

                case (true, true, false): // independently present on both, no shared history
                    if (r!.IsFolder)
                    {
                        break; // both folders — adopt, nothing to do
                    }

                    if (SameSize(r, l!))
                    {
                        break; // same size — assume identical and adopt (heuristic; a hash would be stronger)
                    }

                    ops.Add(Conflict(path, false, "exists on both with different size and no sync history", r, l));
                    break;

                case (true, false, true): // was synced, now missing locally — deleted locally
                    if (!r!.IsFolder && RemoteChanged(r, s!))
                    {
                        ops.Add(Conflict(path, false, "deleted locally, but remote changed since last sync", r, null));
                    }
                    else
                    {
                        ops.Add(Op(SyncOperationKind.DeleteRemote, path, r!.IsFolder, "deleted locally", remote: r));
                    }

                    break;

                case (false, true, true): // was synced, now missing remotely — deleted remotely
                    if (!l!.IsFolder && LocalChanged(l, s!))
                    {
                        ops.Add(Conflict(path, false, "deleted remotely, but local changed since last sync", null, l));
                    }
                    else
                    {
                        ops.Add(Op(SyncOperationKind.DeleteLocal, path, l!.IsFolder, "deleted remotely", local: l));
                    }

                    break;

                case (true, true, true): // present on both and in history
                    if (r!.IsFolder)
                    {
                        break; // folders have no content to compare
                    }

                    var remoteChanged = RemoteChanged(r, s!);
                    var localChanged = LocalChanged(l!, s!);

                    if (remoteChanged && localChanged)
                    {
                        ops.Add(Conflict(path, false, "changed on both sides since last sync", r, l));
                    }
                    else if (remoteChanged)
                    {
                        ops.Add(Op(SyncOperationKind.DownloadFile, path, false, "remote changed", remote: r, local: l));
                    }
                    else if (localChanged)
                    {
                        ops.Add(Op(SyncOperationKind.UploadFile, path, false, "local changed", remote: r, local: l));
                    }

                    break;

                case (false, false, true): // gone on both — drop from state, no action
                    break;
            }
        }

        // Execution order: creates/transfers parent-first (ascending path); deletes child-first (descending).
        ops.Sort(CompareForExecution);
        return ops;
    }

    private static bool RemoteChanged(RemoteEntry r, SyncStateEntry s)
        => !string.Equals(r.RevisionUid, s.RemoteRevisionUid, StringComparison.Ordinal);

    private static bool LocalChanged(LocalEntry l, SyncStateEntry s)
        => l.Size != s.Size || !TimesClose(l.ModifiedUtc, s.LocalModifiedUtc);

    private static bool SameSize(RemoteEntry r, LocalEntry l) => r.Size == l.Size;

    private static bool TimesClose(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return (a.Value - b.Value).Duration() <= TimeTolerance;
    }

    private static int CompareForExecution(SyncOperation a, SyncOperation b)
    {
        static int Phase(SyncOperation o) =>
            o.Kind is SyncOperationKind.DeleteLocal or SyncOperationKind.DeleteRemote ? 1 : 0;

        var phaseA = Phase(a);
        var phaseB = Phase(b);
        if (phaseA != phaseB)
        {
            return phaseA - phaseB;
        }

        // Non-deletes: parents before children. Deletes: children before parents.
        return phaseA == 0
            ? string.CompareOrdinal(a.RelativePath, b.RelativePath)
            : string.CompareOrdinal(b.RelativePath, a.RelativePath);
    }

    private static SyncOperation Op(
        SyncOperationKind kind, string path, bool isFolder, string reason, RemoteEntry? remote = null, LocalEntry? local = null)
        => new() { Kind = kind, RelativePath = path, IsFolder = isFolder, Reason = reason, Remote = remote, Local = local };

    private static SyncOperation Conflict(string path, bool isFolder, string reason, RemoteEntry? remote, LocalEntry? local)
        => Op(SyncOperationKind.Conflict, path, isFolder, reason, remote, local);
}
