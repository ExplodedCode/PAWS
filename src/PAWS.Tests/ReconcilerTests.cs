using PAWS.Core.Sync;

namespace PAWS.Tests;

/// <summary>
/// Pure three-way diff coverage for <see cref="Reconciler"/> — every branch of remote-vs-local-vs-last-
/// known-state comparison the sync engine relies on. Ported from PAWS.AuthTest's --plantest.
/// </summary>
public class ReconcilerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddHours(1);

    private static RemoteEntry RFile(string path, string rev, long size) => new()
    { RelativePath = path, Name = path, IsFolder = false, Size = size, Uid = "node-" + path, RevisionUid = rev };

    private static RemoteEntry RDir(string path) => new() { RelativePath = path, Name = path, IsFolder = true, Uid = "node-" + path };

    private static LocalEntry LFile(string path, long size, DateTimeOffset m) => new()
    { RelativePath = path, Name = path, IsFolder = false, Size = size, ModifiedUtc = m };

    private static SyncStateEntry SFile(string path, string rev, long size, DateTimeOffset m) => new()
    { RelativePath = path, IsFolder = false, RemoteUid = "node-" + path, RemoteRevisionUid = rev, Size = size, LocalModifiedUtc = m };

    [Fact]
    public void Reconcile_ProducesExpectedOperationForEveryShape()
    {
        var remote = new RemoteSnapshot
        {
            RootPath = "/",
            CapturedUtc = DateTimeOffset.UtcNow,
            Entries =
            [
                RFile("shared.txt", "rev-shared-1", 10),
                RFile("remote-changed.txt", "rev-rc-2", 22),
                RFile("local-changed.txt", "rev-lc-1", 30),
                RFile("both-changed.txt", "rev-bc-2", 40),
                RFile("deleted-local.txt", "rev-dl-1", 50),
                RFile("new-remote.txt", "rev-nr-1", 7),
                RDir("newdir"),
                RFile("adopt-same.txt", "rev-as-1", 100),
                RFile("nohist-conflict.txt", "rev-nc-1", 100),
            ],
        };

        var local = new LocalSnapshot
        {
            RootPath = @"C:\local",
            CapturedUtc = DateTimeOffset.UtcNow,
            Entries =
            [
                LFile("shared.txt", 10, T0),
                LFile("remote-changed.txt", 20, T0),
                LFile("local-changed.txt", 33, T1),
                LFile("both-changed.txt", 44, T1),
                LFile("deleted-remote.txt", 60, T0),
                LFile("new-local.txt", 5, T1),
                LFile("adopt-same.txt", 100, T0),
                LFile("nohist-conflict.txt", 200, T0),
            ],
        };

        var state = new SyncState
        {
            PairId = "p1",
            Entries =
            [
                SFile("shared.txt", "rev-shared-1", 10, T0),
                SFile("remote-changed.txt", "rev-rc-1", 20, T0),
                SFile("local-changed.txt", "rev-lc-1", 30, T0),
                SFile("both-changed.txt", "rev-bc-1", 40, T0),
                SFile("deleted-local.txt", "rev-dl-1", 50, T0),
                SFile("deleted-remote.txt", "rev-dr-1", 60, T0),
            ],
        };

        var ops = new Reconciler().Reconcile(remote, local, state);
        var actual = ops.ToDictionary(o => o.RelativePath, o => (SyncOperationKind?)o.Kind, StringComparer.Ordinal);

        var expected = new Dictionary<string, SyncOperationKind?>(StringComparer.Ordinal)
        {
            ["shared.txt"] = null,                                       // unchanged both sides
            ["remote-changed.txt"] = SyncOperationKind.DownloadFile,     // remote newer
            ["local-changed.txt"] = SyncOperationKind.UploadFile,        // local newer
            ["both-changed.txt"] = SyncOperationKind.Conflict,           // both changed
            ["deleted-remote.txt"] = SyncOperationKind.DeleteLocal,      // gone on remote
            ["deleted-local.txt"] = SyncOperationKind.DeleteRemote,      // gone on local
            ["new-remote.txt"] = SyncOperationKind.DownloadFile,         // new on remote
            ["newdir"] = SyncOperationKind.CreateLocalFolder,            // new folder on remote
            ["new-local.txt"] = SyncOperationKind.UploadFile,            // new on local
            ["adopt-same.txt"] = null,                                   // no history, same size -> adopt
            ["nohist-conflict.txt"] = SyncOperationKind.Conflict,        // no history, different size
        };

        foreach (var (path, exp) in expected)
        {
            actual.TryGetValue(path, out var act);
            Assert.True(act == exp, $"{path}: expected {exp?.ToString() ?? "no-op"}, got {act?.ToString() ?? "no-op"}");
        }

        var unexpected = ops.Where(o => !expected.ContainsKey(o.RelativePath)).ToList();
        Assert.True(unexpected.Count == 0, "Unexpected ops: " + string.Join(", ", unexpected.Select(o => $"{o.Kind} {o.RelativePath}")));
    }
}
