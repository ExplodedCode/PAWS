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

    // How often auto-sync polls Drive for remote-side changes (the watcher only sees LOCAL changes), and
    // the short delay before the first poll so launch isn't slowed by an immediate pull.
    private static readonly TimeSpan AutoPullInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AutoPullStartDelay = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, IDisposable> _connections = new(StringComparer.Ordinal); // pairId -> provider
    private readonly Dictionary<string, IProtonDriveClient> _clients = new(StringComparer.Ordinal); // accountId -> client
    private readonly Dictionary<string, FolderWatcher> _watchers = new(StringComparer.Ordinal); // pairId -> auto push watcher
    private readonly Dictionary<string, Timer> _pullTimers = new(StringComparer.Ordinal); // pairId -> periodic pull timer
    private readonly HashSet<string> _pullInFlight = new(StringComparer.Ordinal); // pairIds with a pull running
    private readonly SemaphoreSlim _enableGate = new(1, 1);
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    // The Drive client (and its native crypto) is shared across folders and is NOT concurrency-safe, so
    // serialize every operation that touches it — hydration downloads, snapshot captures, and uploads —
    // against each other. This also keeps two on-demand folders (or auto-sync vs. a manual push) off the
    // client at the same time.
    private readonly SemaphoreSlim _clientUseGate = new(1, 1);
    private bool _disposed;

    /// <summary>Raised (off the UI thread) when a watcher-triggered push for a pair begins.</summary>
    public event Action<string>? AutoSyncStarted;

    /// <summary>Raised (off the UI thread) when a watcher-triggered push for a pair finishes.</summary>
    public event Action<AutoSyncEventArgs>? AutoSyncCompleted;

    /// <summary>Raised (off the UI thread) when a periodic auto-pull for a pair begins.</summary>
    public event Action<string>? AutoPullStarted;

    /// <summary>Raised (off the UI thread) when a periodic auto-pull for a pair finishes.</summary>
    public event Action<AutoPullEventArgs>? AutoPullCompleted;

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

            RemoteSnapshot snapshot;
            await _clientUseGate.WaitAsync(cancellationToken).ConfigureAwait(false); // capture uses native crypto
            try
            {
                snapshot = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Remote folder not found: {pair.RemotePath}");
            }
            finally
            {
                _clientUseGate.Release();
            }

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

            var connection = placeholderEngine.Connect(pair.LocalPath, snapshot, CreateFetchCallback(accountId));

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
    /// The hydration callback for a connected provider: on file open, download the whole file from Drive
    /// into <c>output</c>. Serialized behind <see cref="_clientUseGate"/> because the shared client's
    /// native crypto is not concurrency-safe (see the class remarks).
    /// </summary>
    private FetchPlaceholderData CreateFetchCallback(string accountId) => async (identity, output, ct) =>
    {
        var downloadClient = await GetClientAsync(accountId, ct).ConfigureAwait(false);
        await _clientUseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await downloadClient.DownloadAsync(NodeFromIdentity(identity), output, cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            _clientUseGate.Release();
        }
    };

    /// <summary>
    /// Pushes local changes in an on-demand folder up to Drive — new files, edits, and deletes, detected
    /// by reconciling the current local tree against the last-known state. Remote-side changes are not
    /// pulled here (they surface as placeholders via <see cref="EnableAsync"/>). Feedback-loop safe:
    /// hydrating a file doesn't change its size/mtime, so a downloaded-but-unedited file is never re-sent.
    /// </summary>
    public async Task<SyncResult> SyncChangesAsync(string accountId, SyncPair pair, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(accountId, cancellationToken).ConfigureAwait(false);

        // Hold the client gate for the entire operation: capture, upload, and re-capture all use the
        // shared client's native crypto (not concurrency-safe), and this serializes a push against
        // hydration, against other pairs on the same account, and against overlapping auto-sync runs.
        await _clientUseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

            var result = await new SyncExecutor(client)
                .ExecuteAsync(pair.LocalPath, root, remote, pushOperations, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Re-capture and persist state so the next run starts from a clean baseline.
            var newRemote = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, cancellationToken: cancellationToken).ConfigureAwait(false);
            var newLocal = new LocalSnapshotBuilder().Capture(pair.LocalPath, cancellationToken);
            if (newRemote is not null && newLocal is not null)
            {
                stateStore.Save(SyncStateBuilder.Build(pair.Id, newRemote, newLocal));
            }

            return result;
        }
        finally
        {
            _clientUseGate.Release();
        }
    }

    /// <summary>
    /// Pulls remote-side changes for an on-demand pair down into the local folder as placeholders — new
    /// remote files/folders appear as on-demand placeholders, files changed on Drive are reset to fresh
    /// placeholders (so the next open downloads the new content), and items deleted on Drive are removed
    /// locally. <b>No content is downloaded</b> (it stays on-demand). Local-only changes are left untouched
    /// (push them with <see cref="SyncChangesAsync"/>), and conflicts (changed on both sides) are skipped,
    /// so a pull can never clobber an unpushed local edit. The provider is (re)connected with the refreshed
    /// tree afterwards (only if something changed), so the folder shows the new contents.
    /// <para>To see Drive-side deletions/revisions reliably the pull resumes a fresh Drive session (the
    /// SDK's per-session cache otherwise hides changes to already-seen files); detection still follows
    /// Proton's eventual consistency (a just-trashed file can take seconds to surface).</para>
    /// </summary>
    public async Task<PullResult> PullChangesAsync(string accountId, SyncPair pair, CancellationToken cancellationToken = default)
    {
        RemoteSnapshot remote;
        PullResult result;

        await _clientUseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The SDK serves each child's metadata from its per-session entity cache, so a long-lived
            // client keeps returning a node exactly as it first saw it — it picks up brand-new children
            // (cache miss → fetched) but NEVER notices a file trashed or re-revisioned on Drive after it
            // was first seen. Diagnostics (2026-06-30) confirmed this: a cached client missed a deletion
            // for 5 min, while a fresh client saw it within ~7s. So drop the cached client and resume a
            // fresh one with a cold cache to read current Drive truth; it becomes the new cached client so
            // hydration reuses the same single session afterwards.
            await EvictClientAsync(accountId).ConfigureAwait(false);
            var client = await GetClientAsync(accountId, cancellationToken).ConfigureAwait(false);

            remote = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Remote folder not found: {pair.RemotePath}");
            var local = new LocalSnapshotBuilder().Capture(pair.LocalPath, cancellationToken)
                ?? throw new InvalidOperationException($"Local folder not found: {pair.LocalPath}");

            var lastKnown = stateStore.Load(pair.Id) ?? SyncState.Empty(pair.Id);
            var operations = new Reconciler().Reconcile(remote, local, lastKnown);

            // Pull is remote->local only: bring down creations/changes/deletions. Uploads and conflicts
            // are deliberately ignored here so local work is never discarded.
            var pullOperations = operations
                .Where(o => o.Kind is SyncOperationKind.DownloadFile or SyncOperationKind.CreateLocalFolder or SyncOperationKind.DeleteLocal)
                .ToList();

            // Deletions: remote-deleted items, plus changed files (delete the stale placeholder so the
            // recreate below points it at the new revision). Apply child-first (the ops are sorted so).
            foreach (var op in pullOperations.Where(o =>
                o.Kind == SyncOperationKind.DeleteLocal ||
                (o.Kind == SyncOperationKind.DownloadFile && o.Local is not null)))
            {
                DeleteLocalPlaceholder(pair.LocalPath, op.RelativePath, op.IsFolder);
            }

            // Creations + recreations: CreatePlaceholders adds every entry now missing on disk (it skips
            // ones that already exist), which covers both brand-new remote items and the just-deleted
            // changed files.
            placeholderEngine.CreatePlaceholders(pair.LocalPath, remote);

            // Re-capture the local tree so the new state records each pulled placeholder's ACTUAL on-disk
            // size/mtime. Using the remote entry's values would mismatch when the remote modified-time is
            // unknown (the placeholder then gets "now"), making the next reconcile see a phantom local edit
            // and treat a later remote delete as a conflict instead of applying it.
            var localAfter = new LocalSnapshotBuilder().Capture(pair.LocalPath, cancellationToken);

            // Merge the pulled paths into last-known state surgically — paths we didn't touch (e.g. files
            // with unpushed local edits) keep their old baseline, so the next push still detects them.
            stateStore.Save(MergePulledState(lastKnown, pair.Id, pullOperations, localAfter));

            var created = pullOperations.Count(o =>
                o.Kind == SyncOperationKind.CreateLocalFolder ||
                (o.Kind == SyncOperationKind.DownloadFile && o.Local is null));
            var updated = pullOperations.Count(o => o.Kind == SyncOperationKind.DownloadFile && o.Local is not null);
            var deleted = pullOperations.Count(o => o.Kind == SyncOperationKind.DeleteLocal);
            result = new PullResult(created, updated, deleted);
        }
        finally
        {
            _clientUseGate.Release();
        }

        // (Re)connect the provider with the refreshed tree so Explorer shows the new contents and
        // sub-folder enumeration uses up-to-date data. We must connect if not yet connected (first-time
        // pull → make the folder browsable); if already connected, only reconnect when something actually
        // changed, so an idle periodic poll doesn't needlessly churn the connection. Done OUTSIDE the
        // client gate so a hydration mid-flight on the old connection can drain before it's disconnected
        // (avoids a callback deadlock).
        if (!IsEnabled(pair.Id) || result.Total > 0)
        {
            await ReconnectAsync(accountId, pair, remote, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Starts (if not already running) automatic two-way sync for a pair: a debounced watcher pushes
    /// settled LOCAL changes up via <see cref="SyncChangesAsync"/>, and a periodic timer pulls REMOTE
    /// changes down via <see cref="PullChangesAsync"/> (the watcher can't see Drive-side changes).
    /// Idempotent per pair. The pair should already be enabled (so a state baseline exists). Progress is
    /// reported through <see cref="AutoSyncStarted"/>/<see cref="AutoSyncCompleted"/> (push) and
    /// <see cref="AutoPullStarted"/>/<see cref="AutoPullCompleted"/> (pull).
    /// </summary>
    public void StartAutoSync(string accountId, SyncPair pair)
    {
        lock (_watchers)
        {
            if (_disposed || _watchers.ContainsKey(pair.Id))
            {
                return;
            }

            var watcher = new FolderWatcher(pair.LocalPath, async ct =>
            {
                AutoSyncStarted?.Invoke(pair.Id);
                try
                {
                    var result = await SyncChangesAsync(accountId, pair, ct).ConfigureAwait(false);
                    AutoSyncCompleted?.Invoke(new AutoSyncEventArgs(pair.Id, result, null));
                }
                catch (OperationCanceledException)
                {
                    // Watcher disposed mid-run — nothing to report.
                }
                catch (Exception ex)
                {
                    AutoSyncCompleted?.Invoke(new AutoSyncEventArgs(pair.Id, null, ex));
                }
            });

            _watchers[pair.Id] = watcher;
        }

        lock (_pullTimers)
        {
            if (_disposed || _pullTimers.ContainsKey(pair.Id))
            {
                return;
            }

            var timer = new Timer(_ => _ = RunAutoPullAsync(accountId, pair), null, AutoPullStartDelay, AutoPullInterval);
            _pullTimers[pair.Id] = timer;
        }
    }

    /// <summary>Stops auto-sync (push watcher + periodic pull) for a pair. The provider stays connected.</summary>
    public void StopAutoSync(string pairId)
    {
        FolderWatcher? watcher;
        lock (_watchers)
        {
            _watchers.Remove(pairId, out watcher);
        }

        watcher?.Dispose();

        Timer? timer;
        lock (_pullTimers)
        {
            _pullTimers.Remove(pairId, out timer);
        }

        timer?.Dispose();
    }

    // Runs one periodic pull for a pair, guarding against overlap (a slow pull must not pile up behind the
    // timer). Swallows errors — it's best-effort background work; the next tick retries.
    private async Task RunAutoPullAsync(string accountId, SyncPair pair)
    {
        lock (_pullInFlight)
        {
            if (_disposed || !_pullInFlight.Add(pair.Id))
            {
                return;
            }
        }

        try
        {
            AutoPullStarted?.Invoke(pair.Id);
            var result = await PullChangesAsync(accountId, pair).ConfigureAwait(false);
            AutoPullCompleted?.Invoke(new AutoPullEventArgs(pair.Id, result, null));
        }
        catch (Exception ex)
        {
            AutoPullCompleted?.Invoke(new AutoPullEventArgs(pair.Id, null, ex));
        }
        finally
        {
            lock (_pullInFlight)
            {
                _pullInFlight.Remove(pair.Id);
            }
        }
    }

    /// <summary>True if a watcher is currently auto-syncing this pair.</summary>
    public bool IsAutoSyncing(string pairId)
    {
        lock (_watchers)
        {
            return _watchers.ContainsKey(pairId);
        }
    }

    /// <summary>Disconnects the provider for a pair (placeholders and registration are left in place).</summary>
    public void Disable(string pairId) => DisposeConnection(pairId);

    // Disconnects any existing provider for the pair and connects a fresh one over the given snapshot.
    // Registration is idempotent. Holds only _enableGate (NOT _clientUseGate) so disposing the old
    // connection can wait for an in-flight hydration callback to finish acquiring/releasing _clientUseGate.
    private async Task ReconnectAsync(string accountId, SyncPair pair, RemoteSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _enableGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            DisposeConnection(pair.Id);
            placeholderEngine.RegisterSyncRoot(new SyncRootInfo(pair.LocalPath, ProviderId, "PAWS", "1.0.0.0"));
            var connection = placeholderEngine.Connect(pair.LocalPath, snapshot, CreateFetchCallback(accountId));

            lock (_connections)
            {
                _connections[pair.Id] = connection;
            }
        }
        finally
        {
            _enableGate.Release();
        }
    }

    // Removes a local placeholder whose remote node was deleted (or that we're about to recreate). Plain
    // filesystem delete — un-hydrated placeholders are 0 bytes on disk. In-use/missing paths are ignored;
    // the next pull retries.
    private static void DeleteLocalPlaceholder(string localRoot, string relativePath, bool isFolder)
    {
        var fullPath = Path.Combine(localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (isFolder)
            {
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: true);
                }
            }
            else if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (IOException)
        {
            // Open / mid-hydration — leave it; the next pull will retry.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Updates last-known state for ONLY the pulled paths: removes deleted ones and upserts created/changed
    // ones. Remote identity (Uid/RevisionUid) comes from the remote entry; local Size/mtime come from the
    // freshly-captured placeholder (<paramref name="localAfter"/>) so state matches what the reconciler
    // will read next time — otherwise a placeholder created with a "now" mtime (remote time unknown) looks
    // like a local edit on the next pass. Untouched paths keep their existing baseline, so a later push
    // still sees any unpushed local edits as changes.
    private static SyncState MergePulledState(
        SyncState current, string pairId, IReadOnlyList<SyncOperation> pulledOperations, LocalSnapshot? localAfter)
    {
        var byPath = current.Entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var localByPath = (localAfter?.Entries ?? []).ToDictionary(e => e.RelativePath, StringComparer.Ordinal);

        foreach (var op in pulledOperations)
        {
            if (op.Kind == SyncOperationKind.DeleteLocal)
            {
                byPath.Remove(op.RelativePath);
                continue;
            }

            if (op.Remote is not { } r)
            {
                continue;
            }

            // The placeholder we just created/updated, if it's actually on disk. Fall back to the remote
            // values only if the local capture missed it (e.g. a creation that failed).
            var hasLocal = localByPath.TryGetValue(r.RelativePath, out var l);

            byPath[r.RelativePath] = new SyncStateEntry
            {
                RelativePath = r.RelativePath,
                IsFolder = r.IsFolder,
                RemoteUid = r.Uid,
                RemoteRevisionUid = r.RevisionUid,
                Size = r.IsFolder ? null : (hasLocal ? l!.Size : r.Size),
                LocalModifiedUtc = r.IsFolder ? null : (hasLocal ? l!.ModifiedUtc : r.ModifiedUtc),
            };
        }

        var entries = byPath.Values
            .OrderBy(e => e.RelativePath, StringComparer.Ordinal)
            .ToList();

        return new SyncState { PairId = pairId, LastSyncUtc = DateTimeOffset.UtcNow, Entries = entries };
    }

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

    // Drops and disposes the cached client for an account so the next GetClientAsync resumes a fresh
    // session with a COLD entity cache (which reads current Drive state — see PullChangesAsync). Existing
    // connections keep working: their hydration callback re-resolves the client lazily. Call only while
    // holding _clientUseGate so no hydration is mid-flight on the client being disposed.
    private async Task EvictClientAsync(string accountId)
    {
        IProtonDriveClient? client;
        await _clientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_clients.Remove(accountId, out client))
            {
                return;
            }
        }
        finally
        {
            _clientGate.Release();
        }

        await client.DisposeAsync().ConfigureAwait(false);
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
        List<FolderWatcher> watchers;
        lock (_watchers)
        {
            _disposed = true;
            watchers = [.. _watchers.Values];
            _watchers.Clear();
        }

        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        List<Timer> pullTimers;
        lock (_pullTimers)
        {
            pullTimers = [.. _pullTimers.Values];
            _pullTimers.Clear();
        }

        foreach (var timer in pullTimers)
        {
            timer.Dispose();
        }

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
