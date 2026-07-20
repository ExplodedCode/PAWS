using PAWS.Core.Abstractions;
using PAWS.Core.Configuration;
using PAWS.Core.Diagnostics;
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
    ISyncStateStore stateStore,
    IPopulatedFolderStore populatedStore,
    SemaphoreSlim driveGate,
    TransferThrottle? throttle = null,
    TimeSpan? driveMetadataCallTimeout = null) : IAsyncDisposable
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
    private readonly Dictionary<string, FolderWatcher> _pinWatchers = new(StringComparer.Ordinal); // pairId -> pin-state (attribute) watcher
    private readonly Dictionary<string, Timer> _pullTimers = new(StringComparer.Ordinal); // pairId -> periodic pull timer
    private readonly HashSet<string> _pullInFlight = new(StringComparer.Ordinal); // pairIds with a pull running
    // Folders (relative, '/'-separated, "" = root) that have been POPULATED (materialized locally), per
    // pair. Loaded from populatedStore, grows as folders are browsed. Push/pull scope to this so lazy
    // population is never mistaken for a local deletion. Guarded by its own lock.
    private readonly Dictionary<string, HashSet<string>> _populated = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _enableGate = new(1, 1);

    // Bounds EnableAsync's own initial root listing (see the try/catch around fetchChildren below) — a
    // degraded/hung Drive session would otherwise block this call indefinitely, leaving the sync root
    // REGISTERED but never CONNECTED (Explorer shows "The cloud file provider exited unexpectedly" the
    // whole time — there's no live connection to answer it). Because _enableGate is ONE gate shared by
    // every pair, a single stuck pair would also block every other pair's EnableAsync from ever running
    // — and StartOnDemandPairsAsync awaits pairs sequentially at launch, so it's not just this pair's
    // problem. Same bound and rationale as CloudFilterPlaceholderEngine.PopulateAsync's listing timeout.
    // 180s (not the original 60s): with time-sliced capture gating, waiting on the GATE is no longer
    // what consumes this budget — a listing that runs this long is genuinely enumerating a large folder
    // (per-child metadata + name decryption, cold cache), and failing it at 60s just made every big
    // folder a permanent error. Genuine wedges are the watchdog's job now, not this bound's.
    private const int EnableListingTimeoutSeconds = 180;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    // The native crypto (proton_crypto.dll) is process-global and NOT concurrency-safe, so serialize every
    // operation that touches it — hydration downloads, snapshot captures, uploads — against each other AND
    // against full-sync work. This is the SAME shared gate the SyncEngine uses (injected), so on-demand and
    // full-sync never call the SDK concurrently.
    private readonly SemaphoreSlim _clientUseGate = driveGate;

    // Serializes the LOGICAL state-mutating sync flows (push / pull / conflict-resolve) against each
    // other. Historically this mutual exclusion fell out of the pull holding _clientUseGate for its whole
    // body — but that whole-body hold is exactly what starved Explorer's browse listings for the entire
    // duration of a large-tree capture (see RemoteSnapshotBuilder.CaptureAsync's gate remarks), so the
    // crypto gate is now only ever held per SDK step. This gate restores the flow-level exclusion those
    // methods still need: without it a watcher push could reconcile mid-pull and the pull would then apply
    // placeholder ops computed from a pre-push snapshot (e.g. resetting a just-pushed file's placeholder
    // to its OLD revision). Read-only flows (Explorer browse listings, hydration) never take this gate —
    // staying responsive during a long push/pull is the whole point of the split.
    private readonly SemaphoreSlim _syncOpGate = new(1, 1);
    private bool _disposed;

    // Bounds every metadata call (single-folder listing, path resolve) that runs under _clientUseGate —
    // see RunGatedDriveCallAsync's remarks for why a plain CancellationToken isn't enough on its own
    // (observed live 2026-07-17: a stuck call can fail to honor cancellation at all). 180s to match
    // EnableListingTimeoutSeconds (see its remarks: a listing genuinely CAN run minutes on a large,
    // cold folder — this bound exists to end a degraded session's wait, not to police folder size).
    // Overridable only so tests can exercise the watchdog firing without a real minutes-long wait.
    private readonly TimeSpan _driveMetadataCallTimeout = driveMetadataCallTimeout ?? TimeSpan.FromSeconds(180);

    // Set (permanently, for the rest of this process's life) the first time RunGatedDriveCallAsync's
    // watchdog fires — i.e. a Drive call didn't return even after we gave up waiting and cancelled it.
    // Once true, _clientUseGate is NEVER released again on that abandoned call's behalf (it may still be
    // running against the shared client/native crypto library in the background — releasing the gate
    // and letting a NEW call proceed concurrently would risk exactly the native-crypto concurrency
    // corruption documented elsewhere in this class), so every current and future gate-waiter must fail
    // fast instead of queuing behind a lock that will never open again. _drivePoisonCts is what makes
    // "fail fast" immediate for anyone already waiting, not just new callers.
    private volatile bool _drivePoisoned;
    private readonly CancellationTokenSource _drivePoisonCts = new();

    /// <summary>Raised (off the UI thread) when a watcher-triggered push for a pair begins.</summary>
    public event Action<string>? AutoSyncStarted;

    /// <summary>Raised (off the UI thread) when a watcher-triggered push for a pair finishes.</summary>
    public event Action<AutoSyncEventArgs>? AutoSyncCompleted;

    /// <summary>Raised (off the UI thread) when a periodic auto-pull for a pair begins.</summary>
    public event Action<string>? AutoPullStarted;

    /// <summary>Raised (off the UI thread) when a periodic auto-pull for a pair finishes.</summary>
    public event Action<AutoPullEventArgs>? AutoPullCompleted;

    /// <summary>
    /// Raised (off the UI thread, args: accountId, pairId) when an on-demand Drive call — the root
    /// listing at enable-time, or a live Explorer folder browse — hit its bounded timeout instead of
    /// completing: a stuck/degraded session, not a clean success or a clean rejection. Unlike <see
    /// cref="IProtonDriveClientFactory.SessionExpired"/> (fires only when Proton explicitly rejects a
    /// refresh), a timeout gives no such confirmation, so nothing about the stored session is touched
    /// here — this is a nudge ("maybe try signing in again"), not a declaration that it's dead.
    /// </summary>
    public event Action<string, string>? DriveTimeout;

    /// <summary>
    /// Raised (off the UI thread, args: accountId, pairId) EXACTLY ONCE per process lifetime, the first
    /// time a Drive metadata call fails to return even after being cancelled — see
    /// <see cref="RunGatedDriveCallAsync{T}"/>. Unlike <see cref="DriveTimeout"/> (which can fire
    /// repeatedly for an ordinary, possibly-transient wait), this means Drive access is now permanently
    /// disabled for the rest of this session: restarting PAWS is the only recovery, so the message shown
    /// for this should say that plainly rather than suggesting a retry.
    /// </summary>
    public event Action<string, string>? DriveSessionWedged;

    public bool IsSupported => placeholderEngine.IsSupported;

    public bool IsEnabled(string pairId)
    {
        lock (_connections)
        {
            return _connections.ContainsKey(pairId);
        }
    }

    /// <summary>
    /// Sets up (or refreshes) on-demand for a pair with SCALABLE, lazy population: only the root's direct
    /// children are materialized up front (the sync root doesn't fire FETCH_PLACEHOLDERS for its own
    /// children); every sub-folder is populated on first browse via the connected provider. Re-entrant —
    /// replaces any existing connection. Returns the number of items at the root.
    /// </summary>
    public async Task<int> EnableAsync(string accountId, SyncPair pair, CancellationToken cancellationToken = default)
    {
        await _enableGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisposeConnection(pair.Id);

            placeholderEngine.RegisterSyncRoot(new SyncRootInfo(pair.LocalPath, ProviderId, "PAWS", "1.0.0.0"));

            var fetchChildren = CreateFetchChildren(accountId, pair);

            // Materialize only the root's direct children (one folder listing, not the whole tree). This
            // also marks "" populated. Sub-folders come in lazily on browse via the provider.
            IReadOnlyList<RemoteEntry> rootChildren;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(EnableListingTimeoutSeconds));
                rootChildren = await fetchChildren(string.Empty, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our own bound fired, not the caller's — surface a real, actionable failure instead of a
                // plain cancellation a caller might otherwise treat as "nothing to report" (see
                // StartOnDemandPairsAsync's catch, which previously swallowed every EnableAsync failure
                // silently regardless of cause).
                PawsLog.Write($"Enabling on-demand sync for '{pair.LocalPath}' timed out listing the root of '{pair.RemotePath}' after {EnableListingTimeoutSeconds}s.");
                throw new TimeoutException(
                    $"Listing the root of '{pair.RemotePath}' timed out after {EnableListingTimeoutSeconds}s — the Drive session may be degraded; try again or sign in again.");
            }

            var rootSnapshot = new RemoteSnapshot
            {
                RootPath = pair.RemotePath,
                CapturedUtc = DateTimeOffset.UtcNow,
                Entries = rootChildren,
            };
            placeholderEngine.CreatePlaceholders(pair.LocalPath, rootSnapshot);

            // Record the adopt-everything baseline ONLY on true first-time setup (no saved state yet).
            // On every later enable — i.e. every app launch — the existing baseline MUST survive: it is
            // the only evidence of what was actually synced before, and rebuilding it from the current
            // trees would bless whatever is on disk right now as "already uploaded". That exact bug hid
            // files added or edited while the app was closed, and marked uploads that were cut short by
            // an exit as complete (their new local size/mtime got adopted, so no change was ever
            // detected — not even as a conflict). The catch-up push at auto-sync start reconciles any
            // offline delta against the preserved baseline instead.
            if (stateStore.Load(pair.Id) is null)
            {
                var localState = new LocalSnapshotBuilder().Capture(pair.LocalPath, GetPopulated(pair.Id), cancellationToken);
                if (localState is not null)
                {
                    stateStore.Save(SyncStateBuilder.Build(pair.Id, rootSnapshot, localState));
                }
            }

            var connection = placeholderEngine.Connect(pair.LocalPath, fetchChildren, CreateFetchCallback(accountId, pair));

            lock (_connections)
            {
                _connections[pair.Id] = connection;
            }

            StartPinStateWatcher(accountId, pair);

            // Catch-up: honor pin-state changes made while no provider was connected (files pinned while
            // the app was closed, or freed up while a previous session was wedged). Fire-and-forget —
            // hydrating pinned content can take as long as the downloads take.
            _ = Task.Run(() => ApplyPinStatesAsync(accountId, pair, CancellationToken.None), CancellationToken.None);

            return rootChildren.Count;
        }
        finally
        {
            _enableGate.Release();
        }
    }

    // Explorer's "Always keep on this device" / "Free up space" verbs only WRITE PIN-STATE ATTRIBUTES
    // (FILE_ATTRIBUTE_PINNED 0x80000 / FILE_ATTRIBUTE_UNPINNED 0x100000) — confirmed empirically
    // 2026-07-19 with a live provider attached: neither verb hydrates or dehydrates anything itself, and
    // the provider sees no callback. Reacting to those attributes IS the feature; a provider that
    // doesn't watch for them (as PAWS didn't) leaves both menu items as visually-working no-ops.
    private void StartPinStateWatcher(string accountId, SyncPair pair)
    {
        lock (_pinWatchers)
        {
            if (_disposed || _pinWatchers.ContainsKey(pair.Id))
            {
                return;
            }

            _pinWatchers[pair.Id] = new FolderWatcher(
                pair.LocalPath,
                ct => ApplyPinStatesAsync(accountId, pair, ct),
                quietPeriod: TimeSpan.FromSeconds(1),
                notifyFilter: NotifyFilters.Attributes);
        }
    }

    private void StopPinStateWatcher(string pairId)
    {
        FolderWatcher? watcher;
        lock (_pinWatchers)
        {
            _pinWatchers.Remove(pairId, out watcher);
        }

        watcher?.Dispose();
    }

    /// <summary>
    /// Applies the tree's current pin states: pinned-but-dehydrated files are downloaded (hydrated
    /// through the connected provider), files explicitly marked "free up space" (unpinned) that still
    /// hold content are dehydrated, and a pinned folder that was never browsed is populated first so its
    /// children exist to hydrate. Pin state inherits downward: everything under a pinned folder is kept
    /// local unless a child is itself explicitly unpinned. Runs debounced off the attribute watcher and
    /// once at enable (catch-up); re-runs triggered by its own attribute changes settle as no-ops.
    /// </summary>
    public async Task ApplyPinStatesAsync(string accountId, SyncPair pair, CancellationToken cancellationToken)
    {
        const int PinnedAttr = 0x80000;        // FILE_ATTRIBUTE_PINNED
        const int UnpinnedAttr = 0x100000;     // FILE_ATTRIBUTE_UNPINNED
        const int CloudOnlyAttr = 0x400000;    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS

        var hydrated = 0;
        var dehydrated = 0;
        var failures = 0;

        try
        {
            var pending = new Queue<(string Path, bool InheritedPinned)>();
            pending.Enqueue((pair.LocalPath, false));

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (directory, inheritedPinned) = pending.Dequeue();

                List<(string Path, int Attrs, bool IsDir)> children;
                try
                {
                    children = Directory.EnumerateFileSystemEntries(directory)
                        .Select(p => (p, (int)File.GetAttributes(p), Directory.Exists(p)))
                        .ToList();
                }
                catch (Exception ex)
                {
                    PawsLog.Write($"Pin sweep could not enumerate '{directory}': {ex.Message}");
                    continue;
                }

                foreach (var (path, attrs, isDir) in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Explicit state on the item wins; otherwise a pinned ancestor's intent flows down.
                    var pinned = (attrs & PinnedAttr) != 0 || (inheritedPinned && (attrs & UnpinnedAttr) == 0);

                    if (!isDir)
                    {
                        if (pinned && (attrs & CloudOnlyAttr) != 0)
                        {
                            if (placeholderEngine.HydrateFile(path))
                            {
                                hydrated++;
                            }
                            else
                            {
                                failures++;
                            }
                        }
                        else if ((attrs & UnpinnedAttr) != 0 && (attrs & CloudOnlyAttr) == 0)
                        {
                            // Explicit "free up space". DehydrateTree on a single file carries the
                            // platform's own safety: pinned or not-in-sync (unpushed edits) are refused.
                            var result = placeholderEngine.DehydrateTree(path);
                            dehydrated += result.Dehydrated;
                        }

                        continue;
                    }

                    if ((attrs & CloudOnlyAttr) != 0)
                    {
                        // Un-browsed (never populated) folder. Only a PINNED one warrants forcing
                        // population — "keep on this device" means everything under it, browsed or not.
                        // Everything else stays lazy.
                        if (pinned)
                        {
                            try
                            {
                                var relative = Path.GetRelativePath(pair.LocalPath, path).Replace(Path.DirectorySeparatorChar, '/');
                                var entries = await CreateFetchChildren(accountId, pair)(relative, cancellationToken).ConfigureAwait(false);
                                if (entries.Count > 0)
                                {
                                    placeholderEngine.CreatePlaceholders(pair.LocalPath, new RemoteSnapshot
                                    {
                                        RootPath = pair.RemotePath,
                                        CapturedUtc = DateTimeOffset.UtcNow,
                                        Entries = entries,
                                    });
                                }

                                pending.Enqueue((path, true));
                            }
                            catch (Exception ex)
                            {
                                failures++;
                                PawsLog.Write($"Pin sweep could not populate pinned folder '{path}': {ex.GetType().Name}: {ex.Message}");
                            }
                        }

                        continue;
                    }

                    pending.Enqueue((path, pinned));
                }
            }

            if (hydrated > 0 || dehydrated > 0 || failures > 0)
            {
                PawsLog.Write($"Pin sweep for '{pair.LocalPath}': downloaded {hydrated}, freed {dehydrated}, failures {failures}.");
            }
        }
        catch (OperationCanceledException)
        {
            // Watcher disposed / shutdown — nothing to report.
        }
        catch (Exception ex)
        {
            PawsLog.Write($"Pin sweep failed for '{pair.LocalPath}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-runs shell sync-root registration for an on-demand pair — the repair for when Explorer's
    /// "Always keep on this device" / "Free up space" context menu items have gone missing (e.g. the
    /// shell's AllowPinning capability silently failing to stick on a prior registration — see the
    /// placeholder engine's shell-registration remarks). For a pair with a LIVE provider connection this
    /// goes through the full disconnect → register → reconnect cycle rather than registering under the
    /// live connection: the registration's AllowPinning self-heal can unregister-and-re-register the
    /// root, and yanking a root out from under a connected provider strands it (the long-standing
    /// "--shellfix only while the app is closed" rule). A pair that isn't currently being served just
    /// gets a plain re-registration.
    /// </summary>
    public async Task RepairContextMenuAsync(string accountId, SyncPair pair, CancellationToken cancellationToken = default)
    {
        if (IsEnabled(pair.Id))
        {
            await ReconnectAsync(accountId, pair, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            placeholderEngine.RegisterSyncRoot(new SyncRootInfo(pair.LocalPath, ProviderId, "PAWS", "1.0.0.0"));
        }
    }

    // Builds the lazy per-folder listing callback for a pair: lists ONE remote folder's children (relative
    // to the sync root; "" = root), maps them to sync-root-relative RemoteEntry, and marks that folder
    // populated (persisted). Serialized on the shared client gate (native crypto). This is the engine that
    // makes population scalable — only browsed folders are ever listed/materialized.
    private FetchFolderChildren CreateFetchChildren(string accountId, SyncPair pair) => async (relativeFolder, ct) =>
    {
        var remotePath = string.IsNullOrEmpty(relativeFolder)
            ? pair.RemotePath
            : $"{pair.RemotePath.TrimEnd('/')}/{relativeFolder}";

        try
        {
            var client = await GetClientAsync(accountId, ct).ConfigureAwait(false);

            var entries = await RunGatedDriveCallAsync(accountId, pair.Id, $"listing '{remotePath}'", async innerCt =>
            {
                var list = new List<RemoteEntry>();
                var folder = await client.ResolvePathAsync(remotePath, innerCt).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Remote folder not found: {remotePath}");

                await foreach (var child in client.ListChildrenAsync(folder, innerCt).ConfigureAwait(false))
                {
                    var relativePath = string.IsNullOrEmpty(relativeFolder) ? child.Name : $"{relativeFolder}/{child.Name}";
                    list.Add(new RemoteEntry
                    {
                        RelativePath = relativePath,
                        Name = child.Name,
                        IsFolder = child.IsFolder,
                        Size = child.Size,
                        ModifiedUtc = child.ModifiedUtc,
                        Uid = child.Uid,
                        ParentUid = child.ParentUid,
                        RevisionUid = child.RevisionUid,
                    });
                }

                return (IReadOnlyList<RemoteEntry>)list;
            }, ct).ConfigureAwait(false);

            MarkPopulated(pair.Id, relativeFolder);
            return entries;
        }
        catch (OperationCanceledException)
        {
            // The only cancellation this delegate ever sees today is one of our OWN bounded timeouts
            // (EnableAsync's initial listing, or HydrationConnection.PopulateAsync's live-browse listing —
            // both pass a fresh CancellationTokenSource, never a token tied to a genuine user action) — so
            // this always means "the Drive call didn't come back in time", not "someone asked to cancel".
            // Raised here (this delegate is the ONE place both timeout call sites funnel through) rather
            // than at each call site, so the signal covers both without duplicating the detection logic.
            // Deliberately does NOT touch the stored session (unlike SessionExpired, which fires only on
            // an explicit rejection from Proton) — a timeout doesn't confirm the token is actually bad.
            DriveTimeout?.Invoke(accountId, pair.Id);
            throw;
        }
    };

    // The live populated set for a pair (loaded on first use; the root "" is always populated). Caller
    // must hold the _populated lock.
    private HashSet<string> GetOrLoadPopulated(string pairId)
    {
        if (!_populated.TryGetValue(pairId, out var set))
        {
            set = new HashSet<string>(populatedStore.Load(pairId), StringComparer.Ordinal) { string.Empty };
            _populated[pairId] = set;
        }

        return set;
    }

    /// <summary>A snapshot of the populated-folder set for a pair (safe to read while it's being updated).</summary>
    private IReadOnlySet<string> GetPopulated(string pairId)
    {
        lock (_populated)
        {
            return new HashSet<string>(GetOrLoadPopulated(pairId), StringComparer.Ordinal);
        }
    }

    private void MarkPopulated(string pairId, string relativeFolder)
    {
        bool added;
        lock (_populated)
        {
            added = GetOrLoadPopulated(pairId).Add(relativeFolder);
        }

        if (added)
        {
            populatedStore.Save(pairId, GetPopulated(pairId));
        }
    }

    // Parent folder (relative, '/'-separated) of a relative path; "" for a root-level item.
    private static string ParentFolderOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    /// <summary>
    /// The hydration callback for a connected provider: on file open, download the whole file from Drive
    /// into <c>output</c>. Serialized behind <see cref="_clientUseGate"/> because the shared client's
    /// native crypto is not concurrency-safe (see the class remarks).
    /// </summary>
    private FetchPlaceholderData CreateFetchCallback(string accountId, SyncPair pair) => async (identity, output, ct) =>
    {
        var downloadClient = await GetClientAsync(accountId, ct).ConfigureAwait(false);
        await _clientUseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Hydration honors the download speed limit too (the pair's own override, if it has one).
            // leaveOpen: the hydration connection owns the sink; the wrapper holds no state of its own.
            // Throttled hydration stays timeout-safe — the sink streams each chunk to Cloud Filter as it
            // arrives, so progress keeps reporting.
            var destination = throttle?.WrapDownloadDestination(output, pair.DownloadLimitKBps, leaveOpen: true) ?? output;
            await downloadClient.DownloadAsync(NodeFromIdentity(identity), destination, cancellationToken: ct).ConfigureAwait(false);
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
        await _syncOpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SyncChangesCoreAsync(accountId, pair, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncOpGate.Release();
        }
    }

    private async Task<SyncResult> SyncChangesCoreAsync(string accountId, SyncPair pair, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(accountId, cancellationToken).ConfigureAwait(false);
        var populated = GetPopulated(pair.Id);

        // Local reads are plain filesystem — no SDK/crypto involved, so they never need the gate.
        // Scope the walk to populated folders: un-browsed folders aren't materialized, so walking them
        // would (a) trigger on-demand population and (b) make the reconciler see their remote contents
        // as "deleted locally". Their contents are therefore never in this snapshot.
        var local = new LocalSnapshotBuilder().Capture(pair.LocalPath, populated, cancellationToken)
            ?? throw new InvalidOperationException($"Local folder not found: {pair.LocalPath}");

        RemoteNode root = null!;
        await GateAsync(async ct =>
        {
            root = await client.ResolvePathAsync(pair.RemotePath, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Remote folder not found: {pair.RemotePath}");
        }, cancellationToken).ConfigureAwait(false);

        // Chunk-gated (one gate hold per folder listing, not one around the whole walk) so a large tree's
        // capture can't starve Explorer browses for its whole duration — see the builder's gate remarks.
        var remote = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, gate: GateAsync, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Remote path is not a folder: {pair.RemotePath}");

        var lastKnown = stateStore.Load(pair.Id) ?? SyncState.Empty(pair.Id);
        var operations = new Reconciler().Reconcile(remote, local, lastKnown);

        // On-demand pushes local->remote only; new/updated remote content stays as on-demand
        // placeholders. Conflicts ride along so the executor counts them as skipped — the UI can
        // then tell the user there is something to resolve (Manual ▸ Resolve conflicts).
        var pushOperations = operations
            .Where(o => o.Kind is SyncOperationKind.UploadFile or SyncOperationKind.CreateRemoteFolder or SyncOperationKind.DeleteRemote or SyncOperationKind.Conflict)
            // Hard safety guard: only trash on Drive when the item's parent folder is actually
            // populated (fully materialized) locally, so lazy population can never be read as a delete.
            .Where(o => o.Kind != SyncOperationKind.DeleteRemote || populated.Contains(ParentFolderOf(o.RelativePath)))
            .ToList();

        // Gated PER OPERATION (see SyncExecutor's remarks on `gate`) rather than one lock held for the
        // whole batch: a large/slow push (many files, or one big one under a low speed limit) must not
        // starve pending Explorer-triggered hydration/browse callbacks for its entire duration — that is
        // what previously froze Explorer and produced "the cloud operation was not completed before the
        // time-out period expired". Releasing the lock between files lets those requests through.
        var result = await new SyncExecutor(client, throttle, pairUploadLimitKBps: pair.UploadLimitKBps, pairDownloadLimitKBps: pair.DownloadLimitKBps)
            .ExecuteAsync(pair.LocalPath, root, remote, pushOperations, gate: GateAsync, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Re-capture and persist state so the next run starts from a clean baseline. Local is again
        // scoped to populated folders, which keeps the saved state "materialized-only" — the property
        // that makes un-browsed remote content read as new (ignored by push), never as a deletion.
        // Chunk-gated like the initial capture.
        var newRemote = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, gate: GateAsync, cancellationToken: cancellationToken).ConfigureAwait(false);

        var newLocal = new LocalSnapshotBuilder().Capture(pair.LocalPath, populated, cancellationToken);
        if (newRemote is not null && newLocal is not null)
        {
            stateStore.Save(SyncStateBuilder.Build(pair.Id, newRemote, newLocal));
        }

        // Turn each successfully-uploaded file into a dehydratable, in-sync placeholder carrying its NEW
        // revision id: brand-new local files convert in place; edited placeholders get their identity
        // refreshed (else a later dehydrate + open would download the old revision). Pure cfapi/local
        // filesystem work — no SDK call, so it never needs the gate.
        if (newRemote is not null)
        {
            var failedPaths = result.Failures
                .Select(f => f.Operation.RelativePath)
                .ToHashSet(StringComparer.Ordinal);
            var remoteFiles = newRemote.Entries
                .Where(e => !e.IsFolder)
                .ToDictionary(e => e.RelativePath, StringComparer.Ordinal);

            foreach (var op in pushOperations)
            {
                if (op.Kind != SyncOperationKind.UploadFile || failedPaths.Contains(op.RelativePath))
                {
                    continue;
                }

                if (remoteFiles.TryGetValue(op.RelativePath, out var entry) && (entry.RevisionUid ?? entry.Uid) is { } identity)
                {
                    var localFile = Path.Combine(pair.LocalPath, op.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    placeholderEngine.FinalizeUploadedFile(localFile, identity);
                }
            }
        }

        return result;
    }

    // Acquires the shared crypto/SDK gate for exactly the duration of one inner action — used both
    // directly (for a single capture/resolve) and as the `gate` handed to SyncExecutor (for each
    // individual upload/download/create/trash) — rather than one lock held across an entire multi-step
    // push/pull/resolve. See SyncExecutor's remarks for why: a continuously-held lock lets a large batch
    // starve Explorer's own cloud-file callbacks (60s fixed timeout each) for its whole duration.
    private async Task GateAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await _clientUseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _clientUseGate.Release();
        }
    }

    /// <summary>
    /// Runs a single-folder-listing/path-resolve style Drive call — metadata only, never a file transfer
    /// — under <see cref="_clientUseGate"/>, with a hard watchdog on top of the passed-in
    /// <paramref name="cancellationToken"/> rather than trusting it alone.
    /// <para><b>Why a plain cancellation token isn't enough:</b> every gated call already gets a
    /// bounded token from its caller (e.g. <see cref="EnableAsync"/>'s/<c>PopulateAsync</c>'s own 60s
    /// timeouts), and relying on the SDK call itself to notice that token and return promptly is exactly
    /// what this method stops assuming. Confirmed live 2026-07-17: a stuck root-folder listing kept
    /// failing every ~25-35s (Explorer's own retry cadence) with a bare
    /// <c>OperationCanceledException: The operation was canceled</c> for MINUTES, while an out-of-process
    /// probe against the very same account/session/folder resolved and listed in under a second — proving
    /// the account and Drive session were fine, and the ALREADY-RUNNING PAWS process's own
    /// <see cref="_clientUseGate"/> was never being released: something had acquired it and never
    /// returned, so every later caller was really just waiting out ITS OWN budget for a lock that would
    /// never open again, not genuinely retrying against Drive at all.</para>
    /// <para><b>Why this races the call instead of just cancelling it:</b> once a call is holding the
    /// gate and doesn't return, cancelling its token again changes nothing if it already isn't observing
    /// that token — <see cref="Task.WhenAny(Task,Task)"/> against a hard timer is the only way to
    /// guarantee OUR OWN code regains control. Doing so does NOT make it safe to let a new call proceed:
    /// the abandoned task may still be running in the background, and this class's own remarks elsewhere
    /// establish that the native crypto library (proton_crypto.dll) is process-global and corrupts state
    /// under ANY concurrent use, even across what looks like two independent operations. So instead of
    /// releasing the gate for someone else to use, the FIRST watchdog firing poisons it PERMANENTLY (see
    /// <see cref="_drivePoisoned"/>) — every current and future caller then fails fast with a clear,
    /// accurate message instead of each separately waiting up to their own timeout only to hit the exact
    /// same generic cancellation, over and over, with no indication that waiting will never help.</para>
    /// </summary>
    private async Task<T> RunGatedDriveCallAsync<T>(
        string accountId,
        string pairId,
        string operationDescription,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        if (_drivePoisoned)
        {
            // NOT an OperationCanceledException: callers translate a cancellation here into the softer
            // "couldn't reach Proton Drive — wait and retry" DriveTimeout signal, which is exactly wrong
            // for this state (waiting is known not to help; the one-time DriveSessionWedged notification
            // already told the user to restart).
            throw new InvalidOperationException(
                $"Proton Drive access is stuck in this session ({operationDescription}) — quit and reopen PAWS to recover.");
        }

        // Linked so a caller waiting for the gate unblocks immediately if some OTHER concurrent call
        // poisons it while this one is still queued, instead of sitting out its own full budget first.
        using var gateWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _drivePoisonCts.Token);
        try
        {
            await _clientUseGate.WaitAsync(gateWait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_drivePoisonCts.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Proton Drive access is stuck in this session ({operationDescription}) — quit and reopen PAWS to recover.");
        }
        catch (OperationCanceledException)
        {
            // The caller's own bound expired while still QUEUED for the shared drive gate — Drive was
            // never even contacted, so surfacing this as "couldn't reach Proton Drive" (what an
            // OperationCanceledException becomes upstream) would be false. Confirmed live 2026-07-18:
            // a large folder's whole-tree capture held the gate for minutes and every Explorer browse
            // "timed out" this way while Drive itself was healthy. Chunked capture gating makes long
            // waits rare; this rewrap keeps the diagnosis honest for whatever contention remains.
            throw new TimeoutException(
                $"Timed out waiting for another sync operation to finish before {operationDescription} could start — Proton Drive itself was never contacted.");
        }

        using var callBound = new CancellationTokenSource(_driveMetadataCallTimeout);
        using var callToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, callBound.Token);

        // The watchdog waits DOUBLE the call bound: cancellation is requested at 1x, and the call then
        // gets a full extra budget to observe it and unwind. Racing both at the same instant would make
        // an ordinary bounded timeout (work returning cancelled right at the deadline) a coin flip
        // against being declared permanently stuck.
        var workTask = work(callToken.Token);
        var winner = await Task.WhenAny(workTask, Task.Delay(_driveMetadataCallTimeout * 2, CancellationToken.None))
            .ConfigureAwait(false);

        if (winner == workTask)
        {
            _clientUseGate.Release();
            return await workTask.ConfigureAwait(false); // observes the real result, or rethrows the real failure
        }

        // The watchdog fired and the call STILL hasn't returned control, even though it was handed a
        // token bound to this exact deadline — deliberately do NOT release the gate (see remarks above).
        _drivePoisoned = true;
        PawsLog.Write(
            $"Proton Drive call appears permanently stuck ({operationDescription}, account '{accountId}', pair '{pairId}') " +
            $"— did not return within {_driveMetadataCallTimeout.TotalSeconds:0}s even after cancellation. Drive access is now " +
            "disabled for the rest of this session; restart PAWS to recover.");
        DriveSessionWedged?.Invoke(accountId, pairId);
        _drivePoisonCts.Cancel();

        // Best-effort diagnostic: if the abandoned call eventually does complete on its own, log it — the
        // only way to later tell a genuinely infinite hang apart from one that's merely very slow, without
        // blocking on it now.
        _ = workTask.ContinueWith(
            t => PawsLog.Write(
                $"Drive call abandoned as stuck ({operationDescription}) later {(t.IsFaulted ? $"failed: {t.Exception?.GetBaseException().GetType().Name}" : t.IsCanceled ? "was cancelled" : "actually completed")}, {DateTimeOffset.UtcNow:O}."),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // InvalidOperationException, not OperationCanceledException — see the poisoned fail-fast above.
        throw new InvalidOperationException(
            $"Proton Drive access appears stuck in this session ({operationDescription}) — quit and reopen PAWS to recover.");
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
        PullResult result;

        // Flow-level exclusion vs push/resolve only — NOT the crypto gate. Holding _clientUseGate for
        // this whole body (as this method originally did) starved every Explorer browse for the entire
        // multi-minute capture of a large tree (they died at their 60s bound with a false "couldn't
        // reach Proton Drive" — confirmed live 2026-07-18). SDK calls below take the crypto gate
        // per step instead.
        await _syncOpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The SDK serves each child's metadata from its per-session entity cache, so a long-lived
            // client keeps returning a node exactly as it first saw it — it picks up brand-new children
            // (cache miss → fetched) but NEVER notices a file trashed or re-revisioned on Drive after it
            // was first seen. Diagnostics (2026-06-30) confirmed this: a cached client missed a deletion
            // for 5 min, while a fresh client saw it within ~7s. So drop the cached client and resume a
            // fresh one with a cold cache to read current Drive truth; it becomes the new cached client so
            // hydration reuses the same single session afterwards. One gated chunk: the eviction must not
            // overlap an in-flight hydration on the client being disposed.
            IProtonDriveClient client = null!;
            await GateAsync(async ct =>
            {
                await EvictClientAsync(accountId).ConfigureAwait(false);
                client = await GetClientAsync(accountId, ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            var populated = GetPopulated(pair.Id);

            // Chunk-gated walk (one gate hold per folder listing) — see RemoteSnapshotBuilder's remarks.
            var remote = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, gate: GateAsync, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Remote folder not found: {pair.RemotePath}");
            var local = new LocalSnapshotBuilder().Capture(pair.LocalPath, populated, cancellationToken)
                ?? throw new InvalidOperationException($"Local folder not found: {pair.LocalPath}");

            var lastKnown = stateStore.Load(pair.Id) ?? SyncState.Empty(pair.Id);
            var operations = new Reconciler().Reconcile(remote, local, lastKnown);

            // Pull is remote->local only: bring down creations/changes/deletions. Uploads and conflicts are
            // ignored (local work is never discarded). Restricted to POPULATED folders so we don't eagerly
            // materialize un-browsed folders — those stay lazy and populate on first browse.
            var pullOperations = operations
                .Where(o => o.Kind is SyncOperationKind.DownloadFile or SyncOperationKind.CreateLocalFolder or SyncOperationKind.DeleteLocal)
                .Where(o => populated.Contains(ParentFolderOf(o.RelativePath)))
                .ToList();

            // Deletions: remote-deleted items, plus changed files (delete the stale placeholder so the
            // recreate below points it at the new revision). Apply child-first (the ops are sorted so).
            foreach (var op in pullOperations.Where(o =>
                o.Kind == SyncOperationKind.DeleteLocal ||
                (o.Kind == SyncOperationKind.DownloadFile && o.Local is not null)))
            {
                DeleteLocalPlaceholder(pair.LocalPath, op.RelativePath, op.IsFolder);
            }

            // Creations + recreations: create placeholders for ONLY the pulled entries (new remote items in
            // populated folders + the just-deleted changed files) — NOT the whole remote tree, which would
            // defeat lazy population. A newly-created sub-folder is itself just a placeholder; its children
            // populate when it's browsed.
            var toCreate = pullOperations
                .Where(o => o.Kind is SyncOperationKind.DownloadFile or SyncOperationKind.CreateLocalFolder && o.Remote is not null)
                .Select(o => o.Remote!)
                .ToList();
            if (toCreate.Count > 0)
            {
                placeholderEngine.CreatePlaceholders(pair.LocalPath, new RemoteSnapshot
                {
                    RootPath = pair.RemotePath,
                    CapturedUtc = DateTimeOffset.UtcNow,
                    Entries = toCreate,
                });
            }

            // Re-capture the local tree so the new state records each pulled placeholder's ACTUAL on-disk
            // size/mtime. Using the remote entry's values would mismatch when the remote modified-time is
            // unknown (the placeholder then gets "now"), making the next reconcile see a phantom local edit
            // and treat a later remote delete as a conflict instead of applying it.
            var localAfter = new LocalSnapshotBuilder().Capture(pair.LocalPath, populated, cancellationToken);

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
            _syncOpGate.Release();
        }

        // Ensure a provider is connected so the folder is browsable / hydratable. No snapshot to refresh —
        // the connection lists folders live on browse — so a reconnect is only needed when not yet
        // connected (first-time pull). Done OUTSIDE the client gate so a hydration mid-flight on the old
        // connection can drain before any disconnect (avoids a callback deadlock).
        if (!IsEnabled(pair.Id))
        {
            await ReconnectAsync(accountId, pair, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Captures the recursive remote snapshot of a folder for the UI ("check drive status") — each step
    /// of the walk serialized on the shared drive gate (chunked, so a large tree can't starve Explorer
    /// browses; see RemoteSnapshotBuilder's remarks) and never overlapping a sync/hydration SDK call
    /// (two concurrent high-level SDK operations corrupt the transfer). Uses a fresh session so it
    /// reflects current Drive truth. Returns null if the path is not a folder.
    /// </summary>
    public Task<RemoteSnapshot?> CaptureSnapshotAsync(string accountId, string remotePath, CancellationToken cancellationToken = default)
        => WithFreshClientAsync(
            accountId,
            (client, ct) => new RemoteSnapshotBuilder(client).CaptureAsync(remotePath, gate: GateAsync, cancellationToken: ct),
            cancellationToken);

    /// <summary>
    /// Lists a remote folder's immediate children for the UI, serialized on the shared drive gate (see
    /// <see cref="CaptureSnapshotAsync"/>). Returns null if the folder does not exist.
    /// </summary>
    public Task<IReadOnlyList<RemoteNode>?> ListChildrenAsync(string accountId, string remotePath, CancellationToken cancellationToken = default)
        => WithFreshClientAsync<IReadOnlyList<RemoteNode>?>(
            accountId,
            async (client, ct) =>
            {
                List<RemoteNode>? children = null;
                await GateAsync(async innerCt =>
                {
                    var folder = await client.ResolvePathAsync(remotePath, innerCt).ConfigureAwait(false);
                    if (folder is null)
                    {
                        return;
                    }

                    children = [];
                    await foreach (var child in client.ListChildrenAsync(folder, innerCt).ConfigureAwait(false))
                    {
                        children.Add(child);
                    }
                }, ct).ConfigureAwait(false);

                return (IReadOnlyList<RemoteNode>?)children;
            },
            cancellationToken);

    /// <summary>
    /// Resolves the drive.proton.me web URL for a remote path (the UI's "view online" action), or null if
    /// it can't be determined. Uses the cached client, serialized on the shared drive gate like every
    /// other Drive read.
    /// </summary>
    public async Task<string?> GetWebUrlAsync(string accountId, string remotePath, string? pairId = null, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(accountId, cancellationToken).ConfigureAwait(false);

        return await RunGatedDriveCallAsync(accountId, pairId ?? string.Empty, $"resolving web URL for '{remotePath}'", async innerCt =>
        {
            var node = await client.ResolvePathAsync(remotePath, innerCt).ConfigureAwait(false);
            if (node is null)
            {
                return null;
            }

            return await client.GetWebUrlAsync(node, innerCt).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    // Runs a read against a FRESH Drive session. The evict + resume happens as one gated chunk (the old
    // client must not be disposed under an in-flight hydration); the operation itself runs UNGATED and is
    // responsible for gating its own SDK steps (pass GateAsync into the capture, or wrap a single listing
    // in one GateAsync chunk) — holding the gate across a whole recursive capture starves Explorer
    // browses for its entire duration (see RemoteSnapshotBuilder's gate remarks). Fresh session = current
    // Drive truth (the SDK's per-session cache would otherwise hide remote deletions/revisions); it
    // becomes the new cached client.
    private async Task<T> WithFreshClientAsync<T>(
        string accountId, Func<IProtonDriveClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        IProtonDriveClient client = null!;
        await GateAsync(async ct =>
        {
            await EvictClientAsync(accountId).ConfigureAwait(false);
            client = await GetClientAsync(accountId, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return await operation(client, cancellationToken).ConfigureAwait(false);
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
        FolderWatcher watcher;
        lock (_watchers)
        {
            if (_disposed || _watchers.ContainsKey(pair.Id))
            {
                return;
            }

            watcher = new FolderWatcher(pair.LocalPath, async ct =>
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

        // Catch-up push: the watcher only sees LIVE events, so anything that happened while it wasn't
        // running (files added/edited while the app was closed, an upload cut short by an exit) would
        // otherwise sit unnoticed until the next local change. One poked run reconciles the current
        // tree against the preserved baseline and pushes the delta — or plans nothing if there is none.
        watcher.Poke();

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
    public void Disable(string pairId)
    {
        StopPinStateWatcher(pairId);
        DisposeConnection(pairId);
    }

    /// <summary>
    /// Fully removes a pair's sync machinery from this PC, returning the local folder to an ordinary
    /// folder: stops auto-sync, disconnects the provider, cleans the placeholder tree (see
    /// <see cref="IPlaceholderEngine.DecommissionTree"/> — with <paramref name="keepLocalFiles"/>,
    /// on-disk content is kept as plain files and cloud-only placeholders are removed; without it, the
    /// folder's contents are deleted), unregisters the sync root, and clears the pair's persisted
    /// sync/populated state. Nothing on the remote is touched either way. The tree MUST be cleaned before
    /// the sync root is unregistered, or dehydrated placeholders would be stranded unopenable.
    /// Purely local (no Drive/crypto), so it needs no drive gate. Safe for non-on-demand pairs too — with
    /// no placeholders present the cleanup keeps everything and the unregister is a no-op.
    /// </summary>
    public async Task<DecommissionResult> DecommissionAsync(SyncPair pair, bool keepLocalFiles, CancellationToken cancellationToken = default)
    {
        StopAutoSync(pair.Id);
        Disable(pair.Id); // waits (bounded) for an in-flight hydration to drain before disconnecting

        var result = await Task.Run(
            () => placeholderEngine.DecommissionTree(pair.LocalPath, keepLocalFiles), cancellationToken).ConfigureAwait(false);

        try
        {
            placeholderEngine.UnregisterSyncRoot(pair.LocalPath);
        }
        catch (Exception ex)
        {
            // Never registered (full-sync pair) or already gone — fine. A real failure on an on-demand
            // root only leaves a stale Explorer entry behind; report it rather than failing the removal.
            if (pair.Mode == SyncMode.OnDemand)
            {
                result = result with { Errors = [.. result.Errors, $"unregister sync root: {ex.Message}"] };
            }
        }

        stateStore.Clear(pair.Id);
        populatedStore.Clear(pair.Id);
        lock (_populated)
        {
            _populated.Remove(pair.Id);
        }

        return result;
    }

    /// <summary>
    /// "Free up space": dehydrates the pair's local files back to cloud-only placeholders — all of them,
    /// or only those unused for <paramref name="notUsedFor"/> when given. Pinned files ("Always keep on
    /// this device"), files with unpushed local edits, and files already cloud-only are skipped. Purely
    /// local (no Drive/crypto involved), so it needs no gate and is safe alongside syncing.
    /// </summary>
    public DehydrateResult FreeUpSpace(SyncPair pair, TimeSpan? notUsedFor = null)
        => placeholderEngine.DehydrateTree(pair.LocalPath, notUsedFor);

    /// <summary>
    /// Lists the paths currently in conflict for an on-demand pair (changed on both sides, or a
    /// deletion racing an edit) — a fresh reconcile against current Drive truth, scoped to populated
    /// folders. Nothing is changed; feed the user's decisions to <see cref="ResolveConflictsAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<SyncOperation>> GetConflictsAsync(string accountId, SyncPair pair, CancellationToken cancellationToken = default)
        => WithFreshClientAsync<IReadOnlyList<SyncOperation>>(
            accountId,
            async (client, ct) =>
            {
                var populated = GetPopulated(pair.Id);

                var remote = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, cancellationToken: ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Remote folder not found: {pair.RemotePath}");
                var local = new LocalSnapshotBuilder().Capture(pair.LocalPath, populated, ct)
                    ?? throw new InvalidOperationException($"Local folder not found: {pair.LocalPath}");
                var lastKnown = stateStore.Load(pair.Id) ?? SyncState.Empty(pair.Id);

                return new Reconciler().Reconcile(remote, local, lastKnown)
                    .Where(o => o.Kind == SyncOperationKind.Conflict)
                    .Where(o => populated.Contains(ParentFolderOf(o.RelativePath)))
                    .ToList();
            },
            cancellationToken);

    /// <summary>
    /// Applies the user's per-path conflict decisions on an on-demand pair. "Keep this PC's version"
    /// uploads the local file (or keeps a local deletion by trashing the remote copy); "keep Drive's
    /// version" replaces the local file with a fresh placeholder at Drive's revision — <b>no content is
    /// downloaded</b>, it stays on-demand; "keep both" renames the local file to a conflict-copy sibling
    /// (uploaded as its own file on the next push) and gives the original name to Drive's version.
    /// Re-reconciles first, so a path that is no longer conflicted is simply left alone. State is merged
    /// surgically — untouched paths keep their baseline, so unrelated pending edits stay detected.
    /// </summary>
    public async Task<ConflictResolveResult> ResolveConflictsAsync(
        string accountId,
        SyncPair pair,
        IReadOnlyDictionary<string, ConflictResolution> resolutions,
        CancellationToken cancellationToken = default)
    {
        // Flow-level exclusion vs push/pull (see _syncOpGate's remarks) — resolution reconciles and
        // mutates state just like they do.
        await _syncOpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ResolveConflictsCoreAsync(accountId, pair, resolutions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncOpGate.Release();
        }
    }

    private async Task<ConflictResolveResult> ResolveConflictsCoreAsync(
        string accountId,
        SyncPair pair,
        IReadOnlyDictionary<string, ConflictResolution> resolutions,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var resolved = 0;
        var populated = GetPopulated(pair.Id);

        IProtonDriveClient client = null!;
        RemoteNode root = null!;
        await GateAsync(async ct =>
        {
            // Fresh session for current Drive truth (per-session cache would hide remote-side changes).
            await EvictClientAsync(accountId).ConfigureAwait(false);
            client = await GetClientAsync(accountId, ct).ConfigureAwait(false);

            root = await client.ResolvePathAsync(pair.RemotePath, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Remote folder not found: {pair.RemotePath}");
        }, cancellationToken).ConfigureAwait(false);

        // Chunk-gated walk (one gate hold per folder listing) — see RemoteSnapshotBuilder's remarks.
        var remote = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, gate: GateAsync, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Remote path is not a folder: {pair.RemotePath}");

        var local = new LocalSnapshotBuilder().Capture(pair.LocalPath, populated, cancellationToken)
            ?? throw new InvalidOperationException($"Local folder not found: {pair.LocalPath}");
        var lastKnown = stateStore.Load(pair.Id) ?? SyncState.Empty(pair.Id);

        {
            var conflicts = new Reconciler().Reconcile(remote, local, lastKnown)
                .Where(o => o.Kind == SyncOperationKind.Conflict && resolutions.ContainsKey(o.RelativePath))
                .Where(o => populated.Contains(ParentFolderOf(o.RelativePath)))
                .ToList();

            // Split each decision into its steps. Local steps run right here (deletes, placeholder
            // recreations); remote-affecting steps (uploads, trash) batch through the executor below.
            var executorOps = new List<SyncOperation>();
            var placeholderEntries = new List<RemoteEntry>();
            var stateOps = new List<SyncOperation>(); // consumed by MergePulledState afterwards

            foreach (var conflict in conflicts)
            {
                var plan = SyncExecutor.PlanResolution(conflict, resolutions[conflict.RelativePath]);
                if (plan is null)
                {
                    errors.Add($"{conflict.RelativePath}: can't resolve a file-vs-folder mismatch automatically — rename one side manually");
                    continue;
                }

                try
                {
                    if (plan.RenameLocalToConflictCopy)
                    {
                        SyncExecutor.RenameToConflictCopy(pair.LocalPath, conflict.RelativePath);
                        if (plan.Operations.Count == 0)
                        {
                            // Keep-both with no remote side: the rename is the whole resolution; the
                            // original path is now gone locally, so drop its state entry.
                            stateOps.Add(conflict with { Kind = SyncOperationKind.DeleteLocal });
                        }
                    }

                    foreach (var step in plan.Operations)
                    {
                        switch (step.Kind)
                        {
                            case SyncOperationKind.UploadFile:
                            case SyncOperationKind.DeleteRemote:
                                executorOps.Add(step);
                                break;

                            case SyncOperationKind.DownloadFile:
                                // On-demand: Drive's version comes back as a fresh placeholder at the
                                // new revision (content downloads when opened), not a file transfer.
                                DeleteLocalPlaceholder(pair.LocalPath, step.RelativePath, isFolder: false);
                                placeholderEntries.Add(step.Remote!);
                                stateOps.Add(step);
                                break;

                            case SyncOperationKind.DeleteLocal:
                                DeleteLocalPlaceholder(pair.LocalPath, step.RelativePath, step.IsFolder);
                                stateOps.Add(step);
                                break;
                        }
                    }

                    resolved++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{conflict.RelativePath}: {ex.Message}");
                }
            }

            var failedPaths = new HashSet<string>(StringComparer.Ordinal);
            if (executorOps.Count > 0)
            {
                // Gated per operation (see SyncExecutor's remarks) rather than one lock held across the
                // whole batch — a conflict resolution can include a large upload, which must not starve
                // pending Explorer callbacks any more than an ordinary push would.
                var result = await new SyncExecutor(client, throttle, pairUploadLimitKBps: pair.UploadLimitKBps, pairDownloadLimitKBps: pair.DownloadLimitKBps)
                    .ExecuteAsync(pair.LocalPath, root, remote, executorOps, gate: GateAsync, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                foreach (var failure in result.Failures)
                {
                    errors.Add($"{failure.Operation.RelativePath}: {failure.Error}");
                    failedPaths.Add(failure.Operation.RelativePath);
                    resolved--;
                }
            }

            if (placeholderEntries.Count > 0)
            {
                placeholderEngine.CreatePlaceholders(pair.LocalPath, new RemoteSnapshot
                {
                    RootPath = pair.RemotePath,
                    CapturedUtc = DateTimeOffset.UtcNow,
                    Entries = placeholderEntries,
                });
            }

            // Re-capture remote so uploads get their NEW revision recorded (and their placeholders
            // finalized as dehydratable, identity-refreshed — same as a normal push). Chunk-gated.
            var newRemote = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, gate: GateAsync, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (newRemote is not null)
            {
                var remoteFiles = newRemote.Entries
                    .Where(e => !e.IsFolder)
                    .ToDictionary(e => e.RelativePath, StringComparer.Ordinal);

                foreach (var op in executorOps)
                {
                    if (failedPaths.Contains(op.RelativePath) || !remoteFiles.TryGetValue(op.RelativePath, out var entry))
                    {
                        // Trashed (keep-deleted) paths land here too: drop them from state.
                        if (op.Kind == SyncOperationKind.DeleteRemote && !failedPaths.Contains(op.RelativePath))
                        {
                            stateOps.Add(op with { Kind = SyncOperationKind.DeleteLocal });
                        }

                        continue;
                    }

                    if (op.Kind == SyncOperationKind.UploadFile && (entry.RevisionUid ?? entry.Uid) is { } identity)
                    {
                        var localFile = Path.Combine(pair.LocalPath, op.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        placeholderEngine.FinalizeUploadedFile(localFile, identity);
                        stateOps.Add(op with { Remote = entry }); // record the fresh revision as the baseline
                    }
                }
            }

            var localAfter = new LocalSnapshotBuilder().Capture(pair.LocalPath, populated, cancellationToken);
            stateStore.Save(MergePulledState(lastKnown, pair.Id, stateOps, localAfter));
        }

        return new ConflictResolveResult(resolved, errors);
    }

    // Disconnects any existing provider for the pair and connects a fresh one over the given snapshot.
    // Registration is idempotent. Holds only _enableGate (NOT _clientUseGate) so disposing the old
    // connection can wait for an in-flight hydration callback to finish acquiring/releasing _clientUseGate.
    private async Task ReconnectAsync(string accountId, SyncPair pair, CancellationToken cancellationToken)
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
            var connection = placeholderEngine.Connect(pair.LocalPath, CreateFetchChildren(accountId, pair), CreateFetchCallback(accountId, pair));

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

        List<FolderWatcher> pinWatchers;
        lock (_pinWatchers)
        {
            pinWatchers = [.. _pinWatchers.Values];
            _pinWatchers.Clear();
        }

        foreach (var pinWatcher in pinWatchers)
        {
            pinWatcher.Dispose();
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
        _drivePoisonCts.Dispose();
    }
}
