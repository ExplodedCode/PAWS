using PAWS.Core.Abstractions;
using PAWS.Core.Configuration;
using PAWS.Core.Drive;

namespace PAWS.Core.Sync;

/// <summary>
/// Manages the on-demand (Cloud Filter) side of syncing: for each <see cref="SyncMode.OnDemand"/> pair
/// it registers the local folder as a sync root, mirrors the remote tree as placeholders, and keeps a
/// live provider connected so files hydrate (download) when opened. Connections stay open for the app's
/// lifetime; a Drive client is cached per account to serve hydration without re-resuming each time.
/// </summary>
public sealed class CloudSyncService(
    IPlaceholderEngine placeholderEngine,
    IProtonDriveClientFactory clientFactory,
    ISyncStateStore stateStore) : IAsyncDisposable
{
    // Stable provider identity for PAWS sync roots.
    private const string ProviderId = "30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64";

    private readonly Dictionary<string, IDisposable> _connections = new(StringComparer.Ordinal); // pairId -> provider
    private readonly Dictionary<string, IProtonDriveClient> _clients = new(StringComparer.Ordinal); // accountId -> client
    private readonly SemaphoreSlim _enableGate = new(1, 1);
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    // The Drive client (and its native crypto) is shared across folders — serialize all hydration
    // downloads so two on-demand folders can't drive concurrent downloads on the same client.
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public bool IsSupported => placeholderEngine.IsSupported;

    public bool IsEnabled(string pairId)
    {
        lock (_connections)
        {
            return _connections.ContainsKey(pairId);
        }
    }

    /// <summary>
    /// Sets up (or refreshes) on-demand for a pair: register the sync root, capture the remote tree, and
    /// connect the provider (folders populate lazily on enumeration; files hydrate on open). Re-entrant —
    /// replaces any existing connection for the pair. Returns the number of remote items available.
    /// </summary>
    public async Task<int> EnableAsync(string accountId, SyncPair pair, CancellationToken cancellationToken = default)
    {
        await _enableGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisposeConnection(pair.Id);

            placeholderEngine.RegisterSyncRoot(new SyncRootInfo(pair.LocalPath, ProviderId, "PAWS", "1.0.0.0"));

            var client = await GetClientAsync(accountId, cancellationToken).ConfigureAwait(false);
            var snapshot = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Remote folder not found: {pair.RemotePath}");

            // Create placeholders so the folder shows content (the sync root itself doesn't fire
            // FETCH_PLACEHOLDERS for its children); the connected provider then serves enumeration of
            // sub-folders and hydrates files on open.
            placeholderEngine.CreatePlaceholders(pair.LocalPath, snapshot);

            // Record the synced state (placeholders == remote) so a later SyncChangesAsync can tell what
            // the user changed locally. Hydration doesn't alter size/mtime, so it won't look like a change.
            var localState = new LocalSnapshotBuilder().Capture(pair.LocalPath, cancellationToken);
            if (localState is not null)
            {
                stateStore.Save(SyncStateBuilder.Build(pair.Id, snapshot, localState));
            }

            var connection = placeholderEngine.Connect(pair.LocalPath, snapshot, async (identity, output, ct) =>
            {
                var downloadClient = await GetClientAsync(accountId, ct).ConfigureAwait(false);
                await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await downloadClient.DownloadAsync(NodeFromIdentity(identity), output, cancellationToken: ct).ConfigureAwait(false);
                }
                finally
                {
                    _downloadGate.Release();
                }
            });

            lock (_connections)
            {
                _connections[pair.Id] = connection;
            }

            return snapshot.Entries.Count;
        }
        finally
        {
            _enableGate.Release();
        }
    }

    /// <summary>
    /// Pushes local changes in an on-demand folder up to Drive — new files, edits, and deletes, detected
    /// by reconciling the current local tree against the last-known state. Remote-side changes are not
    /// pulled here (they surface as placeholders via <see cref="EnableAsync"/>). Feedback-loop safe:
    /// hydrating a file doesn't change its size/mtime, so a downloaded-but-unedited file is never re-sent.
    /// </summary>
    public async Task<SyncResult> SyncChangesAsync(string accountId, SyncPair pair, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(accountId, cancellationToken).ConfigureAwait(false);

        var root = await client.ResolvePathAsync(pair.RemotePath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Remote folder not found: {pair.RemotePath}");
        var remote = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Remote path is not a folder: {pair.RemotePath}");
        var local = new LocalSnapshotBuilder().Capture(pair.LocalPath, cancellationToken)
            ?? throw new InvalidOperationException($"Local folder not found: {pair.LocalPath}");

        var lastKnown = stateStore.Load(pair.Id) ?? SyncState.Empty(pair.Id);
        var operations = new Reconciler().Reconcile(remote, local, lastKnown);

        // On-demand pushes local->remote only; new/updated remote content stays as on-demand placeholders.
        var pushOperations = operations
            .Where(o => o.Kind is SyncOperationKind.UploadFile or SyncOperationKind.CreateRemoteFolder or SyncOperationKind.DeleteRemote)
            .ToList();

        SyncResult result;
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false); // serialize vs. hydration (native crypto)
        try
        {
            result = await new SyncExecutor(client)
                .ExecuteAsync(pair.LocalPath, root, remote, pushOperations, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _downloadGate.Release();
        }

        // Re-capture and persist state so the next run starts from a clean baseline.
        var newRemote = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newLocal = new LocalSnapshotBuilder().Capture(pair.LocalPath, cancellationToken);
        if (newRemote is not null && newLocal is not null)
        {
            stateStore.Save(SyncStateBuilder.Build(pair.Id, newRemote, newLocal));
        }

        return result;
    }

    /// <summary>Disconnects the provider for a pair (placeholders and registration are left in place).</summary>
    public void Disable(string pairId) => DisposeConnection(pairId);

    private async Task<IProtonDriveClient> GetClientAsync(string accountId, CancellationToken cancellationToken)
    {
        await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(accountId, out var existing))
            {
                return existing;
            }

            var client = await clientFactory.CreateAsync(accountId, cancellationToken).ConfigureAwait(false);
            _clients[accountId] = client;
            return client;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private void DisposeConnection(string pairId)
    {
        IDisposable? connection;
        lock (_connections)
        {
            if (!_connections.Remove(pairId, out connection))
            {
                return;
            }
        }

        connection.Dispose();
    }

    // Rebuilds a minimal RemoteNode from a placeholder's stored identity (a revision uid "vol~link~rev").
    private static RemoteNode NodeFromIdentity(string identity)
    {
        var lastTilde = identity.LastIndexOf('~');
        var nodeUid = lastTilde > 0 ? identity[..lastTilde] : identity;
        return new RemoteNode { Uid = nodeUid, RevisionUid = identity, Name = string.Empty, IsFolder = false };
    }

    public async ValueTask DisposeAsync()
    {
        List<IDisposable> connections;
        lock (_connections)
        {
            connections = [.. _connections.Values];
            _connections.Clear();
        }

        foreach (var connection in connections)
        {
            connection.Dispose();
        }

        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _clients.Clear();
    }
}
