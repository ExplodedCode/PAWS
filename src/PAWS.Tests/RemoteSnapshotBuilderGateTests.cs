using System.Diagnostics;
using System.Runtime.CompilerServices;
using PAWS.Core.Drive;
using PAWS.Core.Sync;

namespace PAWS.Tests;

/// <summary>
/// Regression for the gate-starvation incident (2026-07-18): a whole-tree remote capture used to run
/// under one continuous hold of the shared drive gate, so every Explorer browse listing queued behind it
/// for the capture's full multi-minute duration and died at its 60s bound with a false "couldn't reach
/// Proton Drive". With <see cref="RemoteSnapshotBuilder.CaptureAsync"/>'s chunked gating, a short gated
/// call must be able to slot in BETWEEN the walk's per-folder steps.
/// </summary>
public class RemoteSnapshotBuilderGateTests
{
    /// <summary>
    /// A folder tree where every ListChildrenAsync call takes <paramref name="perFolderDelay"/> —
    /// slow enough that a whole-capture gate hold would visibly block an interleaved call.
    /// </summary>
    private sealed class SlowTreeClient(TimeSpan perFolderDelay) : IProtonDriveClient
    {
        private const int TopLevelFolders = 6;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RemoteNode> GetRootAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RemoteNode?> ResolvePathAsync(string remotePath, CancellationToken cancellationToken = default)
            => Task.FromResult<RemoteNode?>(new RemoteNode { Uid = "vol~root", Name = string.Empty, IsFolder = true });

        public async IAsyncEnumerable<RemoteNode> ListChildrenAsync(
            RemoteNode folder, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(perFolderDelay, cancellationToken);

            if (folder.Uid == "vol~root")
            {
                for (var i = 0; i < TopLevelFolders; i++)
                {
                    yield return new RemoteNode { Uid = $"vol~d{i}", ParentUid = folder.Uid, Name = $"dir{i}", IsFolder = true };
                }
            }

            // Sub-folders are empty (the per-folder delay is still paid for each).
        }

        public Task DownloadAsync(RemoteNode file, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RemoteNode> UploadAsync(RemoteNode parentFolder, string name, Stream content, DateTimeOffset? lastModifiedUtc = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RemoteNode> UploadRevisionAsync(RemoteNode existingFile, Stream content, DateTimeOffset? lastModifiedUtc = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RemoteNode> CreateFolderAsync(RemoteNode parentFolder, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RenameAsync(RemoteNode node, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MoveAsync(RemoteNode node, RemoteNode newParent, string? nameAtDestination = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task TrashAsync(RemoteNode node, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> GetWebUrlAsync(RemoteNode node, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ChunkedCapture_LetsAShortGatedCallInterleave_InsteadOfWaitingForTheWholeWalk()
    {
        var driveGate = new SemaphoreSlim(1, 1);
        async Task Gate(Func<CancellationToken, Task> action, CancellationToken ct)
        {
            await driveGate.WaitAsync(ct);
            try
            {
                await action(ct);
            }
            finally
            {
                driveGate.Release();
            }
        }

        var perFolder = TimeSpan.FromMilliseconds(150);
        var client = new SlowTreeClient(perFolder);

        // 1 resolve + 7 listings (root + 6 subdirs) ≈ 1.2s total capture time.
        var capture = new RemoteSnapshotBuilder(client).CaptureAsync("/", gate: Gate, cancellationToken: CancellationToken.None);

        // Give the capture time to be mid-walk, then race a short gated call against it — the shape of
        // an Explorer browse listing arriving during a pull. It must get the gate between chunks
        // (bounded by roughly ONE per-folder step), not wait for the entire capture.
        await Task.Delay(200);
        var sw = Stopwatch.StartNew();
        await Gate(_ => Task.CompletedTask, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(600),
            $"a short gated call should interleave into the capture within ~one chunk, waited {sw.Elapsed.TotalMilliseconds:0}ms");

        var snapshot = await capture;
        Assert.NotNull(snapshot);
        Assert.Equal(6, snapshot!.Entries.Count);
    }

    /// <summary>
    /// One folder whose enumeration alone takes many seconds (per-child latency, the cold-cache shape a
    /// large folder produces in real use). Folder-level chunking is NOT enough here — the second live
    /// incident (2026-07-18) was exactly this: a browse starved behind a single folder's minutes-long
    /// listing. Time-sliced gating must release the gate DURING the listing.
    /// </summary>
    private sealed class LargeFolderClient(int childCount, TimeSpan perChildDelay) : IProtonDriveClient
    {
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RemoteNode> GetRootAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RemoteNode?> ResolvePathAsync(string remotePath, CancellationToken cancellationToken = default)
            => Task.FromResult<RemoteNode?>(new RemoteNode { Uid = "vol~root", Name = string.Empty, IsFolder = true });

        public async IAsyncEnumerable<RemoteNode> ListChildrenAsync(
            RemoteNode folder, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < childCount; i++)
            {
                await Task.Delay(perChildDelay, cancellationToken);
                yield return new RemoteNode { Uid = $"vol~f{i}", ParentUid = folder.Uid, Name = $"file{i}.bin", IsFolder = false, Size = 1, RevisionUid = $"vol~f{i}~r1" };
            }
        }

        public Task DownloadAsync(RemoteNode file, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RemoteNode> UploadAsync(RemoteNode parentFolder, string name, Stream content, DateTimeOffset? lastModifiedUtc = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RemoteNode> UploadRevisionAsync(RemoteNode existingFile, Stream content, DateTimeOffset? lastModifiedUtc = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RemoteNode> CreateFolderAsync(RemoteNode parentFolder, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RenameAsync(RemoteNode node, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MoveAsync(RemoteNode node, RemoteNode newParent, string? nameAtDestination = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task TrashAsync(RemoteNode node, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> GetWebUrlAsync(RemoteNode node, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SingleLargeFolderListing_YieldsTheGatePeriodically_NotOnlyAtFolderBoundaries()
    {
        var driveGate = new SemaphoreSlim(1, 1);
        async Task Gate(Func<CancellationToken, Task> action, CancellationToken ct)
        {
            await driveGate.WaitAsync(ct);
            try
            {
                await action(ct);
            }
            finally
            {
                driveGate.Release();
            }
        }

        // ONE folder, ~9s to enumerate end to end (30 children x 300ms) — far longer than the 2s gate
        // slice. A folder-boundary-only chunker would hold the gate for all ~9s.
        var client = new LargeFolderClient(childCount: 30, perChildDelay: TimeSpan.FromMilliseconds(300));
        var capture = new RemoteSnapshotBuilder(client).CaptureAsync("/", gate: Gate, cancellationToken: CancellationToken.None);

        await Task.Delay(500); // land mid-listing
        var sw = Stopwatch.StartNew();
        await Gate(_ => Task.CompletedTask, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"a short gated call should get the gate at a slice boundary (~2s), waited {sw.Elapsed.TotalSeconds:0.0}s — " +
            "anything near the ~9s full-listing duration means slicing regressed to folder-boundary chunking");

        var snapshot = await capture;
        Assert.NotNull(snapshot);
        Assert.Equal(30, snapshot!.Entries.Count);
    }
}
