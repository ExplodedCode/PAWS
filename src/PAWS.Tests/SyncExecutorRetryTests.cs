using PAWS.Core.Drive;
using PAWS.Core.Sync;

namespace PAWS.Tests;

/// <summary>
/// Coverage for <see cref="SyncExecutor"/>'s transient-failure retry: a blip (HttpRequestException,
/// TimeoutException, SocketException, IOException) gets a bounded number of retries before being
/// recorded as failed; a non-transient (logic/data) error is never retried. Ported from
/// PAWS.AuthTest's --retrytest.
/// </summary>
public class SyncExecutorRetryTests
{
    private static readonly TimeSpan[] NoDelay = [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5)];

    private static (string root, RemoteNode remoteRoot, RemoteSnapshot snapshot, List<SyncOperation> ops) MakeFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "paws-retrytest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");

        var uploadOp = new SyncOperation { Kind = SyncOperationKind.UploadFile, RelativePath = "a.txt", IsFolder = false };
        var remoteRoot = new RemoteNode { Uid = "vol~root", Name = string.Empty, IsFolder = true };
        var remoteSnapshot = new RemoteSnapshot { RootPath = "/", CapturedUtc = DateTimeOffset.UtcNow, Entries = new List<RemoteEntry>() };
        return (root, remoteRoot, remoteSnapshot, [uploadOp]);
    }

    [Fact]
    public async Task TransientFailure_RetriesThenSucceeds()
    {
        var (root, remoteRoot, snapshot, ops) = MakeFixture();
        try
        {
            var client = new FakeUploadClient(failCount: 2, () => new HttpRequestException("simulated transport failure"));
            var executor = new SyncExecutor(client, transientRetryDelays: NoDelay);
            var result = await executor.ExecuteAsync(root, remoteRoot, snapshot, ops);

            Assert.Equal(1, result.Completed);
            Assert.Empty(result.Failures);
            Assert.Equal(3, client.Attempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistentTransientFailure_RetriesFullBudgetThenFails()
    {
        var (root, remoteRoot, snapshot, ops) = MakeFixture();
        try
        {
            var client = new FakeUploadClient(failCount: int.MaxValue, () => new HttpRequestException("simulated transport failure"));
            var executor = new SyncExecutor(client, transientRetryDelays: NoDelay);
            var result = await executor.ExecuteAsync(root, remoteRoot, snapshot, ops);

            Assert.Equal(0, result.Completed);
            Assert.Single(result.Failures);
            Assert.Equal(3, client.Attempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NonTransientFailure_IsNotRetried()
    {
        var (root, remoteRoot, snapshot, ops) = MakeFixture();
        try
        {
            var client = new FakeUploadClient(failCount: int.MaxValue, () => new InvalidOperationException("simulated logic error"));
            var executor = new SyncExecutor(client, transientRetryDelays: NoDelay);
            var result = await executor.ExecuteAsync(root, remoteRoot, snapshot, ops);

            Assert.Equal(0, result.Completed);
            Assert.Single(result.Failures);
            Assert.Equal(1, client.Attempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
