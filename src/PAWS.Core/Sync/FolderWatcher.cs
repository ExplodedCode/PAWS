namespace PAWS.Core.Sync;

/// <summary>
/// Watches a local folder subtree and raises a single debounced callback after a burst of file-system
/// changes settles. Rapid events (a save that writes then sets timestamps, a multi-file copy, an editor's
/// temp-file dance) are coalesced into one trigger so callers don't run a sync per event. The callback is
/// never run concurrently with itself: if changes arrive while a run is in flight, exactly one more run is
/// queued for after it finishes. Callback exceptions are swallowed — the watcher keeps running and the next
/// change re-triggers, so background sync is best-effort and self-healing.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly TimeSpan _quietPeriod;
    private readonly Func<CancellationToken, Task> _onChanged;
    private readonly object _gate = new();

    private bool _disposed;
    private bool _running;
    private bool _pending;
    private CancellationTokenSource? _cts;

    /// <param name="path">Absolute path of the folder to watch (must exist).</param>
    /// <param name="onChanged">Invoked, debounced, after changes settle. Cancelled on dispose.</param>
    /// <param name="quietPeriod">How long the tree must be idle before the callback fires (default 3s).</param>
    public FolderWatcher(string path, Func<CancellationToken, Task> onChanged, TimeSpan? quietPeriod = null)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Cannot watch a folder that does not exist: {path}");
        }

        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(3);
        _debounce = new Timer(_ => OnQuiet(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };

        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Deleted += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
        // A buffer overflow (a huge burst we couldn't keep up with) just means "something changed" —
        // re-arm the debounce and let the full reconcile in the callback sort out the actual delta.
        _watcher.Error += OnFsError;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Arms the debounce as if a change had just been observed, so the callback runs once after the
    /// quiet period (through the same non-reentrant pipeline as real events). Used when the watcher
    /// starts, to catch changes made while it wasn't running — files added or edited while the app was
    /// closed, or an upload cut short by an exit. A no-op change costs one reconcile that plans zero
    /// operations.
    /// </summary>
    public void Poke() => Bump();

    private void OnFsEvent(object sender, FileSystemEventArgs e) => Bump();

    private void OnFsError(object sender, ErrorEventArgs e) => Bump();

    private void Bump()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounce.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnQuiet()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // A run is already in flight — mark that more changes arrived and let it re-run when it finishes.
            if (_running)
            {
                _pending = true;
                return;
            }

            _running = true;
            _cts = new CancellationTokenSource();
        }

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        CancellationToken token;
        lock (_gate)
        {
            token = _cts!.Token;
        }

        try
        {
            await _onChanged(token).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a failed run is dropped; the next change re-triggers a fresh attempt.
        }
        finally
        {
            bool runAgain;
            lock (_gate)
            {
                _running = false;
                runAgain = _pending && !_disposed;
                _pending = false;
            }

            if (runAgain)
            {
                Bump();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts?.Cancel();
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFsEvent;
        _watcher.Created -= OnFsEvent;
        _watcher.Deleted -= OnFsEvent;
        _watcher.Renamed -= OnFsEvent;
        _watcher.Error -= OnFsError;
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
