using PAWS.Core.Configuration;
using PAWS.Core.Drive;

namespace PAWS.Core.Sync;

/// <summary>
/// Manages the on-demand (Cloud Filter) side of syncing: for each <see cref="SyncMode.OnDemand"/> pair
/// it registers the local folder as a sync root, mirrors the remote tree as placeholders, and keeps a
/// live provider connected so files hydrate (download) when opened. Connections stay open for the app's
/// lifetime; a Drive client is cached per account to serve hydration without re-resuming each time.
/// </summary>
public sealed class CloudSyncService(IPlaceholderEngine placeholderEngine, IProtonDriveClientFactory clientFactory) : IAsyncDisposable
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
