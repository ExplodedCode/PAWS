using PAWS.Core.Abstractions;
using PAWS.Core.Drive;
using PAWS.Core.Security;
using PAWS.Core.Sync;

namespace PAWS.Tests;

/// <summary>In-memory ISecretStore for tests that need account persistence without DPAPI/disk.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, ProtonSecrets> _store = new();

    public bool HasSecrets(string accountId) => _store.ContainsKey(accountId);

    public void SaveSecrets(string accountId, ProtonSecrets secrets) => _store[accountId] = secrets;

    public ProtonSecrets? LoadSecrets(string accountId) => _store.TryGetValue(accountId, out var s) ? s : null;

    public void ClearSecrets(string accountId) => _store.Remove(accountId);
}

/// <summary>
/// Minimal IProtonDriveClient test double for retry tests: only UploadAsync does anything (fails
/// <paramref name="failCount"/> times, then succeeds) — SyncExecutor's UploadFile path for a brand-new
/// file (no existing remote entry) only calls UploadAsync, so every other member throws if ever called.
/// </summary>
internal sealed class FakeUploadClient(int failCount, Func<Exception> exceptionFactory) : IProtonDriveClient
{
    public int Attempts { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<RemoteNode> GetRootAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RemoteNode?> ResolvePathAsync(string remotePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IAsyncEnumerable<RemoteNode> ListChildrenAsync(RemoteNode folder, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DownloadAsync(RemoteNode file, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RemoteNode> UploadAsync(
        RemoteNode parentFolder,
        string name,
        Stream content,
        DateTimeOffset? lastModifiedUtc = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Attempts++;
        if (Attempts <= failCount)
        {
            throw exceptionFactory();
        }

        return Task.FromResult(new RemoteNode
        {
            Uid = "vol~new", ParentUid = parentFolder.Uid, Name = name, IsFolder = false, RevisionUid = "vol~new~r1",
        });
    }

    public Task<RemoteNode> UploadRevisionAsync(
        RemoteNode existingFile,
        Stream content,
        DateTimeOffset? lastModifiedUtc = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RemoteNode> CreateFolderAsync(RemoteNode parentFolder, string name, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task RenameAsync(RemoteNode node, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task MoveAsync(RemoteNode node, RemoteNode newParent, string? nameAtDestination = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task TrashAsync(RemoteNode node, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<string?> GetWebUrlAsync(RemoteNode node, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>IProtonDriveClientFactory test double: CreateAsync never completes on its own — it only
/// ever returns via the passed-in CancellationToken being cancelled, simulating a Drive session that
/// hangs instead of responding.</summary>
internal sealed class FakeHangingClientFactory : IProtonDriveClientFactory
{
    // Explicit empty accessors (rather than a field-like event) — this test double never raises it, and
    // a field-like event here would warn CS0067 ("event is never used").
    public event Action<string>? SessionExpired { add { } remove { } }

    public async Task<IProtonDriveClient> CreateAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable — Task.Delay(Infinite) only returns via cancellation");
    }
}

/// <summary>In-memory ISyncStateStore test double — Load returning null means "first-time setup".</summary>
internal sealed class FakeSyncStateStore : ISyncStateStore
{
    public SyncState? Load(string pairId) => null;

    public void Save(SyncState state)
    {
    }

    public void Clear(string pairId)
    {
    }
}

/// <summary>In-memory IPopulatedFolderStore test double.</summary>
internal sealed class FakePopulatedFolderStore : IPopulatedFolderStore
{
    public ISet<string> Load(string pairId) => new HashSet<string>(StringComparer.Ordinal);

    public void Save(string pairId, IReadOnlySet<string> folders)
    {
    }

    public void Clear(string pairId)
    {
    }
}
