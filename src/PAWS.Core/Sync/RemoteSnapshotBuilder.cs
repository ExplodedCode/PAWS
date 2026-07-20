using System.Diagnostics;
using PAWS.Core.Diagnostics;
using PAWS.Core.Drive;

namespace PAWS.Core.Sync;

/// <summary>
/// Walks a remote sync-root subtree via <see cref="IProtonDriveClient"/> and produces a flat, sorted
/// <see cref="RemoteSnapshot"/>. Breadth-first so large trees stream a folder at a time; the result is
/// sorted by relative path for deterministic, diff-friendly snapshots.
/// </summary>
public sealed class RemoteSnapshotBuilder(IProtonDriveClient client)
{
    // Longest a single gated slice of the walk may hold the drive gate before releasing it for anyone
    // else waiting (an Explorer browse listing, a hydration). Folder-level chunking alone proved NOT
    // fine enough (confirmed live 2026-07-18, second round): ONE folder with many children can take
    // minutes to enumerate cold — per-child metadata + name decryption — so the browse starved behind a
    // single "chunk" exactly as it used to starve behind the whole capture. The enumerator is only ever
    // advanced while the gate is held, so pausing between slices leaves the SDK dormant — interleaved
    // calls stay strictly sequential, never concurrent (the adapter's listing is a plain pull-based
    // enumerable with no background prefetch).
    private static readonly TimeSpan MaxGateSlice = TimeSpan.FromSeconds(2);

    // A capture slower than this gets a log line with its size/duration — turns "sync feels stuck /
    // browses are slow" into a diagnosable fact instead of guesswork.
    private static readonly TimeSpan SlowCaptureLogThreshold = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Captures the subtree rooted at <paramref name="remotePath"/>. Returns null if the path does not
    /// resolve to a folder. Reports each entry to <paramref name="onEntry"/> as it is discovered (e.g.
    /// for progress), if provided.
    /// <para>When <paramref name="gate"/> is supplied, the walk acquires and releases it in short
    /// TIME-SLICED holds (the initial resolve, then at most <see cref="MaxGateSlice"/> of enumeration at
    /// a time) instead of the caller holding one lock around the whole capture. A whole-capture hold
    /// starves everything else sharing the drive gate — most visibly Explorer's own on-demand folder
    /// browses, which queue behind it and die at their 60s bound with a misleading "couldn't reach
    /// Proton Drive" (confirmed live 2026-07-18, in two rounds: first the whole-capture hold, then a
    /// single large folder's listing, each starved every root browse for minutes). Same
    /// per-operation-gating idea as SyncExecutor's `gate`, at sub-listing granularity. Without a gate
    /// the behavior is unchanged (caller owns serialization).</para>
    /// </summary>
    public async Task<RemoteSnapshot?> CaptureAsync(
        string remotePath,
        Action<RemoteEntry>? onEntry = null,
        SyncGate? gate = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var folderCount = 0;

        // No gate: run each step directly (caller is holding the drive gate, or is a harness/test that
        // doesn't need serialization).
        gate ??= static (action, ct) => action(ct);

        RemoteNode? root = null;
        await gate(async ct => root = await client.ResolvePathAsync(remotePath, ct).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        if (root is null || !root.IsFolder)
        {
            return null;
        }

        var entries = new List<RemoteEntry>();
        var folders = new Queue<(RemoteNode Node, string RelativeBase)>();
        folders.Enqueue((root, string.Empty));

        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, relativeBase) = folders.Dequeue();
            folderCount++;

            // Drive the enumerator manually so the gate can be released and re-acquired PART-WAY through
            // one folder's listing. The enumerator only advances inside a gate hold; between slices it is
            // suspended (no SDK activity), so whoever slots in runs strictly sequentially with the walk.
            var enumerator = client.ListChildrenAsync(folder, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                var more = true;
                while (more)
                {
                    await gate(async _ =>
                    {
                        var slice = Stopwatch.StartNew();
                        while (more && slice.Elapsed < MaxGateSlice)
                        {
                            more = await enumerator.MoveNextAsync().ConfigureAwait(false);
                            if (!more)
                            {
                                break;
                            }

                            var child = enumerator.Current;
                            var relativePath = relativeBase.Length == 0 ? child.Name : $"{relativeBase}/{child.Name}";

                            var entry = new RemoteEntry
                            {
                                RelativePath = relativePath,
                                Name = child.Name,
                                IsFolder = child.IsFolder,
                                Size = child.Size,
                                ModifiedUtc = child.ModifiedUtc,
                                Uid = child.Uid,
                                ParentUid = child.ParentUid,
                                RevisionUid = child.RevisionUid,
                            };

                            entries.Add(entry);
                            onEntry?.Invoke(entry);

                            if (child.IsFolder)
                            {
                                folders.Enqueue((child, relativePath));
                            }
                        }
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (stopwatch.Elapsed > SlowCaptureLogThreshold)
        {
            PawsLog.Write(
                $"Remote capture of '{remotePath}' took {stopwatch.Elapsed.TotalSeconds:0}s " +
                $"({folderCount} folders, {entries.Count} entries) — background syncs of this folder will each take about this long.");
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        return new RemoteSnapshot
        {
            RootPath = remotePath,
            CapturedUtc = DateTimeOffset.UtcNow,
            Entries = entries,
        };
    }
}
