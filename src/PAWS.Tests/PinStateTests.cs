using System.Runtime.CompilerServices;
using PAWS.CloudFilter;
using PAWS.Core.Configuration;
using PAWS.Core.Drive;
using PAWS.Core.Sync;

namespace PAWS.Tests;

/// <summary>
/// Coverage for honoring Explorer's pin-state verbs. Ground truth (probed live 2026-07-19 with a
/// connected provider): "Always keep on this device" and "Free up space" only WRITE the pinned/unpinned
/// attributes — neither hydrates nor dehydrates anything itself, and the provider sees no callback. The
/// sync engine must react to the attributes, which is what <c>CloudSyncService.ApplyPinStatesAsync</c>
/// does. Without it both menu items are visually-working no-ops (the original user report).
/// </summary>
[Collection("CloudFilter")]
public class PinStateTests
{
    private const int PinnedAttr = 0x80000;
    private const int UnpinnedAttr = 0x100000;
    private const int CloudOnlyAttr = 0x400000;
    private const string ProviderId = "30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64";

    private static string NewTempRoot() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "paws-pinstate-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void AddAttr(string path, int attr)
        => File.SetAttributes(path, (FileAttributes)((int)File.GetAttributes(path) | attr));

    private static bool IsCloudOnly(string path) => ((int)File.GetAttributes(path) & CloudOnlyAttr) != 0;

    private static RemoteEntry FileEntry(string relativePath, int size, DateTimeOffset now) => new()
    {
        RelativePath = relativePath, Name = relativePath.Split('/')[^1], IsFolder = false, Size = size,
        ModifiedUtc = now, Uid = "vol~" + relativePath, RevisionUid = "vol~" + relativePath + "~r1",
    };

    [Fact]
    public async Task PinnedDehydratedFile_SweepDownloadsIt()
    {
        var root = NewTempRoot();
        var engine = new CloudFilterPlaceholderEngine();
        var content = new byte[64 * 1024];
        Random.Shared.NextBytes(content);
        try
        {
            engine.RegisterSyncRoot(new SyncRootInfo(root, ProviderId, "PAWS", "1.0.0.0"));
            var now = DateTimeOffset.UtcNow;
            engine.CreatePlaceholders(root, new RemoteSnapshot
            {
                RootPath = "/", CapturedUtc = now, Entries = [FileEntry("pinned.bin", content.Length, now)],
            });

            var fetches = 0;
            using (engine.Connect(
                root,
                (_, _) => Task.FromResult<IReadOnlyList<RemoteEntry>>([]),
                async (_, output, ct) => { Interlocked.Increment(ref fetches); await output.WriteAsync(content, ct); }))
            {
                var file = Path.Combine(root, "pinned.bin");
                Assert.True(IsCloudOnly(file));
                AddAttr(file, PinnedAttr); // what Explorer's "Always keep on this device" does

                var service = new CloudSyncService(
                    engine, new FakeHangingClientFactory(), new FakeSyncStateStore(), new FakePopulatedFolderStore(), new SemaphoreSlim(1, 1));
                var pair = new SyncPair { Id = "p1", LocalPath = root, RemotePath = "/", Mode = SyncMode.OnDemand };
                await service.ApplyPinStatesAsync("acct", pair, CancellationToken.None);

                Assert.False(IsCloudOnly(file));
                Assert.Equal(1, fetches);
                Assert.Equal(content, File.ReadAllBytes(file));
            }
        }
        finally
        {
            try { engine.UnregisterSyncRoot(root); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task UnpinnedHydratedFile_SweepFreesItsSpace()
    {
        var root = NewTempRoot();
        var engine = new CloudFilterPlaceholderEngine();
        var content = new byte[64 * 1024];
        Random.Shared.NextBytes(content);
        try
        {
            engine.RegisterSyncRoot(new SyncRootInfo(root, ProviderId, "PAWS", "1.0.0.0"));
            var now = DateTimeOffset.UtcNow;
            engine.CreatePlaceholders(root, new RemoteSnapshot
            {
                RootPath = "/", CapturedUtc = now, Entries = [FileEntry("freed.bin", content.Length, now)],
            });

            using (engine.Connect(
                root,
                (_, _) => Task.FromResult<IReadOnlyList<RemoteEntry>>([]),
                async (_, output, ct) => await output.WriteAsync(content, ct)))
            {
                var file = Path.Combine(root, "freed.bin");
                _ = File.ReadAllBytes(file); // hydrate
                Assert.False(IsCloudOnly(file));
                AddAttr(file, UnpinnedAttr); // what Explorer's "Free up space" does

                var service = new CloudSyncService(
                    engine, new FakeHangingClientFactory(), new FakeSyncStateStore(), new FakePopulatedFolderStore(), new SemaphoreSlim(1, 1));
                var pair = new SyncPair { Id = "p1", LocalPath = root, RemotePath = "/", Mode = SyncMode.OnDemand };
                await service.ApplyPinStatesAsync("acct", pair, CancellationToken.None);

                Assert.True(IsCloudOnly(file), "the unpinned file should have been dehydrated");
            }
        }
        finally
        {
            try { engine.UnregisterSyncRoot(root); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PinAttributeChange_TriggersTheWatcherPath_NoDirectCall()
    {
        var root = NewTempRoot();
        var engine = new CloudFilterPlaceholderEngine();
        var content = new byte[16 * 1024];
        Random.Shared.NextBytes(content);
        try
        {
            engine.RegisterSyncRoot(new SyncRootInfo(root, ProviderId, "PAWS", "1.0.0.0"));
            var now = DateTimeOffset.UtcNow;
            engine.CreatePlaceholders(root, new RemoteSnapshot
            {
                RootPath = "/", CapturedUtc = now, Entries = [FileEntry("watched.bin", content.Length, now)],
            });

            using (engine.Connect(
                root,
                (_, _) => Task.FromResult<IReadOnlyList<RemoteEntry>>([]),
                async (_, output, ct) => await output.WriteAsync(content, ct)))
            {
                var service = new CloudSyncService(
                    engine, new FakeHangingClientFactory(), new FakeSyncStateStore(), new FakePopulatedFolderStore(), new SemaphoreSlim(1, 1));
                var pair = new SyncPair { Id = "p1", LocalPath = root, RemotePath = "/", Mode = SyncMode.OnDemand };

                // The same wiring EnableAsync does, minus the Drive-touching parts this test doesn't need.
                var startWatcher = typeof(CloudSyncService).GetMethod("StartPinStateWatcher",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?? throw new InvalidOperationException("StartPinStateWatcher not found — renamed?");
                startWatcher.Invoke(service, ["acct", pair]);

                var file = Path.Combine(root, "watched.bin");
                AddAttr(file, PinnedAttr); // the attribute write is the ONLY stimulus — like Explorer's verb

                // Watcher debounce is 1s; poll generously beyond it.
                var hydratedInTime = false;
                for (var i = 0; i < 30 && !hydratedInTime; i++)
                {
                    await Task.Delay(500);
                    hydratedInTime = !IsCloudOnly(file);
                }

                Assert.True(hydratedInTime, "the attribute watcher should have triggered the sweep and downloaded the pinned file");
                Assert.Equal(content, File.ReadAllBytes(file));

                service.Disable(pair.Id);
            }
        }
        finally
        {
            try { engine.UnregisterSyncRoot(root); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>Drive client double serving one subfolder listing for the pinned-folder populate path.</summary>
    private sealed class FakeListingClient(byte[] content) : IProtonDriveClient
    {
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RemoteNode> GetRootAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RemoteNode?> ResolvePathAsync(string remotePath, CancellationToken cancellationToken = default)
            => Task.FromResult<RemoteNode?>(remotePath is "/sub"
                ? new RemoteNode { Uid = "vol~sub", Name = "sub", IsFolder = true }
                : null);

        public async IAsyncEnumerable<RemoteNode> ListChildrenAsync(
            RemoteNode folder, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new RemoteNode { Uid = "vol~sub-child", ParentUid = folder.Uid, Name = "child.bin", IsFolder = false, Size = content.Length, RevisionUid = "vol~sub-child~r1" };
        }

        public Task DownloadAsync(RemoteNode file, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RemoteNode> UploadAsync(RemoteNode parentFolder, string name, Stream content2, DateTimeOffset? lastModifiedUtc = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RemoteNode> UploadRevisionAsync(RemoteNode existingFile, Stream content2, DateTimeOffset? lastModifiedUtc = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RemoteNode> CreateFolderAsync(RemoteNode parentFolder, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RenameAsync(RemoteNode node, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MoveAsync(RemoteNode node, RemoteNode newParent, string? nameAtDestination = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task TrashAsync(RemoteNode node, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> GetWebUrlAsync(RemoteNode node, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeListingClientFactory(byte[] content) : IProtonDriveClientFactory
    {
        public event Action<string>? SessionExpired { add { } remove { } }

        public Task<IProtonDriveClient> CreateAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IProtonDriveClient>(new FakeListingClient(content));
    }

    [Fact]
    public async Task PinnedUnbrowsedFolder_SweepPopulatesAndDownloadsItsContents()
    {
        var root = NewTempRoot();
        var engine = new CloudFilterPlaceholderEngine();
        var content = new byte[32 * 1024];
        Random.Shared.NextBytes(content);
        try
        {
            engine.RegisterSyncRoot(new SyncRootInfo(root, ProviderId, "PAWS", "1.0.0.0"));
            var now = DateTimeOffset.UtcNow;
            engine.CreatePlaceholders(root, new RemoteSnapshot
            {
                RootPath = "/",
                CapturedUtc = now,
                Entries = [new RemoteEntry { RelativePath = "sub", Name = "sub", IsFolder = true, ModifiedUtc = now, Uid = "vol~sub" }],
            });

            using (engine.Connect(
                root,
                (_, _) => Task.FromResult<IReadOnlyList<RemoteEntry>>([]),
                async (_, output, ct) => await output.WriteAsync(content, ct)))
            {
                var sub = Path.Combine(root, "sub");
                AddAttr(sub, PinnedAttr); // "Always keep on this device" on a never-browsed folder

                var service = new CloudSyncService(
                    engine, new FakeListingClientFactory(content), new FakeSyncStateStore(), new FakePopulatedFolderStore(), new SemaphoreSlim(1, 1));
                var pair = new SyncPair { Id = "p1", LocalPath = root, RemotePath = "/", Mode = SyncMode.OnDemand };
                await service.ApplyPinStatesAsync("acct", pair, CancellationToken.None);

                var child = Path.Combine(sub, "child.bin");
                Assert.True(File.Exists(child), "the pinned folder should have been populated");
                Assert.False(IsCloudOnly(child), "the populated child should have been downloaded (pin inherits)");
                Assert.Equal(content, File.ReadAllBytes(child));
            }
        }
        finally
        {
            try { engine.UnregisterSyncRoot(root); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
