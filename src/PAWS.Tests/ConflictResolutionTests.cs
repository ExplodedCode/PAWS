using PAWS.Core.Sync;

namespace PAWS.Tests;

/// <summary>
/// Pure coverage for conflict-resolution planning: every conflict shape the reconciler can emit x every
/// <see cref="ConflictResolution"/> must map to exactly the right concrete steps, plus the conflict-copy
/// rename (name shape + uniquification). Ported from PAWS.AuthTest's --conflicttest.
/// </summary>
public class ConflictResolutionTests
{
    private static readonly RemoteEntry RemoteFile = new() { RelativePath = "doc.txt", Name = "doc.txt", IsFolder = false, Size = 10, Uid = "vol~u", RevisionUid = "vol~u~r1" };
    private static readonly LocalEntry LocalFile = new() { RelativePath = "doc.txt", Name = "doc.txt", IsFolder = false, Size = 12, ModifiedUtc = DateTimeOffset.UtcNow };
    private static readonly RemoteEntry RemoteFolder = new() { RelativePath = "doc.txt", Name = "doc.txt", IsFolder = true, Uid = "vol~d" };

    private static SyncOperation Conflict(RemoteEntry? r, LocalEntry? l) => new()
    {
        Kind = SyncOperationKind.Conflict, RelativePath = "doc.txt", IsFolder = false, Remote = r, Local = l,
    };

    private static readonly SyncOperation BothChanged = Conflict(RemoteFile, LocalFile);        // changed on both sides / no-history different size
    private static readonly SyncOperation DeletedLocally = Conflict(RemoteFile, null);          // deleted locally, remote changed
    private static readonly SyncOperation DeletedRemotely = Conflict(null, LocalFile);          // deleted remotely, local changed
    private static readonly SyncOperation TypeMismatch = Conflict(RemoteFolder, LocalFile);     // file vs folder

    private static ConflictPlan? Plan(SyncOperation c, ConflictResolution r) => SyncExecutor.PlanResolution(c, r);

    private static bool StepsMatch(ConflictPlan? p, bool rename, params SyncOperationKind[] kinds)
        => p is not null && p.RenameLocalToConflictCopy == rename
           && p.Operations.Select(o => o.Kind).SequenceEqual(kinds);

    [Fact]
    public void BothChanged_KeepLocal_Uploads()
        => Assert.True(StepsMatch(Plan(BothChanged, ConflictResolution.KeepLocal), false, SyncOperationKind.UploadFile));

    [Fact]
    public void BothChanged_KeepRemote_Downloads()
        => Assert.True(StepsMatch(Plan(BothChanged, ConflictResolution.KeepRemote), false, SyncOperationKind.DownloadFile));

    [Fact]
    public void BothChanged_KeepBoth_RenamesThenDownloads()
        => Assert.True(StepsMatch(Plan(BothChanged, ConflictResolution.KeepBoth), true, SyncOperationKind.DownloadFile));

    [Fact]
    public void BothChanged_KeepBoth_DownloadsFreshNotStaleLocal()
        => Assert.Null(Plan(BothChanged, ConflictResolution.KeepBoth)!.Operations[0].Local);

    [Fact]
    public void DeletedLocally_KeepLocal_TrashesRemote()
        => Assert.True(StepsMatch(Plan(DeletedLocally, ConflictResolution.KeepLocal), false, SyncOperationKind.DeleteRemote));

    [Fact]
    public void DeletedLocally_KeepRemote_Downloads()
        => Assert.True(StepsMatch(Plan(DeletedLocally, ConflictResolution.KeepRemote), false, SyncOperationKind.DownloadFile));

    [Fact]
    public void DeletedLocally_KeepBoth_DownloadsOnly_NothingLocalToRename()
        => Assert.True(StepsMatch(Plan(DeletedLocally, ConflictResolution.KeepBoth), false, SyncOperationKind.DownloadFile));

    [Fact]
    public void DeletedRemotely_KeepLocal_Uploads()
        => Assert.True(StepsMatch(Plan(DeletedRemotely, ConflictResolution.KeepLocal), false, SyncOperationKind.UploadFile));

    [Fact]
    public void DeletedRemotely_KeepRemote_DeletesLocal()
        => Assert.True(StepsMatch(Plan(DeletedRemotely, ConflictResolution.KeepRemote), false, SyncOperationKind.DeleteLocal));

    [Fact]
    public void DeletedRemotely_KeepBoth_RenamesOnly()
        => Assert.True(StepsMatch(Plan(DeletedRemotely, ConflictResolution.KeepBoth), true));

    [Fact]
    public void TypeMismatch_IsUnresolvableForAllThreeResolutions()
    {
        Assert.Null(Plan(TypeMismatch, ConflictResolution.KeepLocal));
        Assert.Null(Plan(TypeMismatch, ConflictResolution.KeepRemote));
        Assert.Null(Plan(TypeMismatch, ConflictResolution.KeepBoth));
    }

    [Fact]
    public void NonConflictOperation_IsNotPlanned()
        => Assert.Null(SyncExecutor.PlanResolution(BothChanged with { Kind = SyncOperationKind.UploadFile }, ConflictResolution.KeepLocal));

    [Fact]
    public void RenameToConflictCopy_MovesFileAsideAndUniquifiesRepeatedCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), "paws-conflicttest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "one");
            var first = SyncExecutor.RenameToConflictCopy(root, "a.txt");
            File.WriteAllText(Path.Combine(root, "a.txt"), "two");
            var second = SyncExecutor.RenameToConflictCopy(root, "a.txt");

            Assert.False(File.Exists(Path.Combine(root, "a.txt")));
            Assert.True(File.Exists(first));

            Assert.StartsWith("a (conflict copy ", Path.GetFileName(first), StringComparison.Ordinal);
            Assert.EndsWith(".txt", first, StringComparison.Ordinal);

            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.NotEqual(first, second, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("one", File.ReadAllText(first));
            Assert.Equal("two", File.ReadAllText(second));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
