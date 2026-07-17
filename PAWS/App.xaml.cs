using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PAWS.CloudFilter;
using PAWS.Core.Abstractions;
using PAWS.Core.Configuration;
using PAWS.Core.Diagnostics;
using PAWS.Core.Drive;
using PAWS.Core.Proton;
using PAWS.Core.Setup;
using PAWS.Core.Sync;
using PAWS.Infrastructure.Proton;
using PAWS.Infrastructure.Startup;
using PAWS.Infrastructure.Storage;
using PAWS.Proton.Drive;

namespace PAWS
{
    /// <summary>
    /// Application entry point. Owns the shared stores/services and routes the first launch to
    /// setup (capture credentials) versus the normal home screen.
    /// </summary>
    public partial class App : Application
    {
        private MainWindow? _window;
        private readonly SemaphoreSlim _driveGate;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiDispatcher;
        private readonly HashSet<string> _sessionExpiredAccounts = new(StringComparer.Ordinal);
        private readonly object _sessionExpiredLock = new();
        private readonly Dictionary<string, DateTime> _lastDriveTimeoutNotified = new(StringComparer.Ordinal);
        private readonly object _driveTimeoutLock = new();

        // A stuck/degraded session can time out repeatedly (every periodic pull, every folder browse) —
        // one tray balloon per pair per cooldown window is a useful nudge; one per occurrence would spam.
        private static readonly TimeSpan DriveTimeoutNotifyCooldown = TimeSpan.FromMinutes(10);

        public App()
        {
            InitializeComponent();

            // By construction this is the single instance for the session (Program.Main already bounced
            // any second launch before constructing anything) — start listening for a FUTURE second
            // launch to ask us to come to the foreground instead of starting its own copy.
            _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            SingleInstanceGuard.ListenForActivation(() => _uiDispatcher.TryEnqueue(() => _window?.ShowFromTray()));

            Paths = new PawsPaths();

            // Route Core/sync diagnostics to a dated log file so background sync failures are recoverable.
            Log = new FileSyncLog(Paths);
            PawsLog.Writer = Log.Append;
            PawsLog.Write($"PAWS started (log at {Log.CurrentFile}).");

            // Last-resort safety nets: anything that would otherwise take the process down gets logged
            // with its full stack, and UI-thread exceptions are marked handled so a background hiccup
            // (e.g. a transfer aborted by pausing mid-upload) can never kill the tray app silently.
            UnhandledException += (_, e) =>
            {
                PawsLog.Write($"UNHANDLED UI exception: {e.Message}{System.Environment.NewLine}{e.Exception}");
                e.Handled = true;
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                PawsLog.Write($"Unobserved task exception: {e.Exception}");
                e.SetObserved();
            };
            System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                PawsLog.Write($"UNHANDLED domain exception (fatal={e.IsTerminating}): {e.ExceptionObject}");

            SettingsStore = new JsonSettingsStore(Paths);
            SecretStore = new DpapiSecretStore(Paths);

            // Login is browser-based (session forking) — no password handled in-app, supports
            // passkeys/2FA, and avoids Proton's anti-abuse blocks. There is no SRP/password path.
            WebAuthenticator = new WebProtonAuthenticator();

            // Builds a connected Proton Drive client for an account by resuming its stored session.
            DriveClientFactory = new ProtonDriveClientFactory(SecretStore);

            // A dead refresh token means the user must sign in again — no automatic recovery is possible
            // past that point. Detected lazily wherever it's next discovered (any pair's on-demand
            // hydration/push/pull, a full-sync cycle), and can fire from a background thread and more
            // than once for the same account (several sessions can be active for one account at once) —
            // de-duplicated here so the user gets ONE notification per expiry, not one per sync path
            // that happens to notice it; ClearSessionExpired re-arms it once they sign back in.
            DriveClientFactory.SessionExpired += accountId =>
            {
                bool isNew;
                lock (_sessionExpiredLock)
                {
                    isNew = _sessionExpiredAccounts.Add(accountId);
                }

                if (isNew)
                {
                    _uiDispatcher.TryEnqueue(() => AccountSessionExpired?.Invoke(accountId));
                }
            };

            // Shared across every Drive-touching service: the native crypto (proton_crypto.dll) is
            // process-global and not concurrency-safe, so on-demand hydration/push/pull and full-sync
            // plan/apply must all serialize on this one gate.
            _driveGate = new SemaphoreSlim(1, 1);

            // App-wide transfer speed limits, seeded from settings; the Settings page updates the live
            // instance so changes apply immediately, including to in-flight transfers.
            var settings = SettingsStore.Load();
            Throttle = new TransferThrottle
            {
                UploadLimitKBps = settings.UploadLimitKBps,
                DownloadLimitKBps = settings.DownloadLimitKBps,
            };

            // Sync engine: plan + apply, persisting last-known state per pair.
            SyncStateStore = new JsonSyncStateStore(Paths);
            SyncEngine = new SyncEngine(DriveClientFactory, SyncStateStore, _driveGate, Throttle);

            // Files-on-demand: registers sync roots + serves hydration for On-demand pairs. Population is
            // lazy/scalable (only browsed folders materialize); the populated-folder store records which
            // folders are materialized so push/pull never mistake un-browsed content for a deletion.
            CloudSync = new CloudSyncService(
                new CloudFilterPlaceholderEngine(), DriveClientFactory, SyncStateStore, new JsonPopulatedFolderStore(Paths), _driveGate, Throttle);

            // Automatic two-way sync for Full-sync pairs (over the shared SyncEngine).
            FullSync = new FullSyncService(SyncEngine);

            // A Drive call timed out rather than completing (see CloudSyncService.DriveTimeout's remarks)
            // — nudge the user toward re-authenticating without declaring the session dead (that's
            // SessionExpired's job, on an actual rejection from Proton). Throttled per pair so a
            // persistent or repeated timeout doesn't produce a tray balloon every single time it happens.
            CloudSync.DriveTimeout += (accountId, pairId) =>
            {
                var notify = false;
                lock (_driveTimeoutLock)
                {
                    var now = DateTime.UtcNow;
                    if (!_lastDriveTimeoutNotified.TryGetValue(pairId, out var last) || now - last > DriveTimeoutNotifyCooldown)
                    {
                        _lastDriveTimeoutNotified[pairId] = now;
                        notify = true;
                    }
                }

                if (notify)
                {
                    _uiDispatcher.TryEnqueue(() => DriveTimedOut?.Invoke(accountId, pairId));
                }
            };
        }

        /// <summary>Convenience accessor for the strongly-typed application instance.</summary>
        public static App Instance => (App)Current;

        /// <summary>
        /// Raised (already marshalled to the UI thread) when an account's Proton session has expired and
        /// needs the browser sign-in flow again — at most once per expiry per account (see the ctor
        /// wiring). <see cref="MainWindow"/> shows a tray notification; <see cref="Views.HomePage"/>
        /// refreshes so the account's card reflects it immediately if the window is already open.
        /// </summary>
        public event Action<string>? AccountSessionExpired;

        /// <summary>
        /// Re-arms expiry notifications for an account after it successfully signs in again — otherwise
        /// a SECOND expiry later in the same app run would be silently swallowed by the de-dup guard.
        /// </summary>
        public void ClearSessionExpired(string accountId)
        {
            lock (_sessionExpiredLock)
            {
                _sessionExpiredAccounts.Remove(accountId);
            }
        }

        /// <summary>
        /// Raised (already marshalled to the UI thread, args: accountId, pairId; throttled per pair — see
        /// the ctor wiring) when a Drive call timed out instead of completing. <see cref="MainWindow"/>
        /// shows a tray notification suggesting the user try signing in again — a nudge, not a
        /// declaration that the session is dead (unlike <see cref="AccountSessionExpired"/>).
        /// </summary>
        public event Action<string, string>? DriveTimedOut;

        public PawsPaths Paths { get; }

        public FileSyncLog Log { get; }

        public ISettingsStore SettingsStore { get; }

        public ISecretStore SecretStore { get; }

        public IWebProtonAuthenticator WebAuthenticator { get; }

        public IProtonDriveClientFactory DriveClientFactory { get; }

        public ISyncStateStore SyncStateStore { get; }

        public SyncEngine SyncEngine { get; }

        public CloudSyncService CloudSync { get; }

        public FullSyncService FullSync { get; }

        /// <summary>Live transfer speed limits — every transfer path reads this instance.</summary>
        public TransferThrottle Throttle { get; }

        public MainWindow? Window => _window;

        public SetupWorkflow CreateSetupWorkflow() => new(SettingsStore, SecretStore);

        /// <summary>True once at least one Proton account has been added.</summary>
        public bool IsConfigured => SettingsStore.Load().Accounts.Count > 0;

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var settings = SettingsStore.Load();

            // "Start syncing all accounts when PAWS starts" means pausing a folder is session-scoped:
            // at launch every enabled sync folder is flipped back to auto so syncing resumes, paused or
            // not. (With the setting off, pause states persist and nothing starts in the background.)
            // Normalized BEFORE the window exists so the folder cards render the resumed state.
            if (settings.AutoSyncOnLaunch)
            {
                var changed = false;
                foreach (var account in settings.Accounts)
                {
                    foreach (var pair in account.SyncPairs.Where(p =>
                        p.Enabled && p.Mode is SyncMode.OnDemand or SyncMode.FullSync && !p.AutoSync))
                    {
                        pair.AutoSync = true;
                        changed = true;
                    }
                }

                if (changed)
                {
                    SettingsStore.Save(settings);
                }
            }

            _window = new MainWindow();
            _window.Activate();

            // Keep the Windows sign-in autostart registration in line with the preference (best-effort).
            StartupRegistration.Apply(settings.RunOnStartup);

            // Bring any On-demand folders online in the background (placeholders + hydration provider) —
            // the folders must work regardless; the app-wide switch only gates background auto-sync.
            _ = StartOnDemandPairsAsync(settings.AutoSyncOnLaunch);

            // Resume automatic two-way sync for Full-sync folders the user left on auto.
            if (settings.AutoSyncOnLaunch)
            {
                StartFullSyncPairs();
            }

            // Startup storage sweep: dehydrate on-demand files not used recently (pinned files are never
            // touched). Purely local, so it can run alongside the provider/auto-sync startup above.
            if (settings.AutoDehydrateDays is > 0 and var days)
            {
                _ = Task.Run(() => AutoDehydrate(TimeSpan.FromDays(days)));
            }
        }

        /// <summary>
        /// Frees up space in every enabled on-demand folder: files whose last write AND last read are
        /// older than <paramref name="notUsedFor"/> go back to cloud-only placeholders. Skips pinned
        /// ("Always keep on this device") files, unpushed local edits, and already-dehydrated files.
        /// </summary>
        private void AutoDehydrate(TimeSpan notUsedFor)
        {
            foreach (var account in SettingsStore.Load().Accounts)
            {
                foreach (var pair in account.SyncPairs.Where(p => p.Enabled && p.Mode == SyncMode.OnDemand))
                {
                    try
                    {
                        var result = CloudSync.FreeUpSpace(pair, notUsedFor);
                        if (result.Dehydrated > 0 || result.Errors.Count > 0)
                        {
                            PawsLog.Write(
                                $"Auto free-up ({pair.LocalPath}): {result.Dehydrated} dehydrated, {result.Skipped} skipped"
                                + (result.Errors.Count > 0 ? $", {result.Errors.Count} error(s): {result.Errors[0]}" : string.Empty));
                        }
                    }
                    catch (System.Exception ex)
                    {
                        PawsLog.Write($"Auto free-up failed for {pair.LocalPath}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Re-establishes automatic two-way sync (watcher + poll) for every enabled Full-sync pair with
        /// auto-sync on. Best-effort and per-pair isolated — e.g. a missing local folder is skipped.
        /// </summary>
        private void StartFullSyncPairs()
        {
            foreach (var account in SettingsStore.Load().Accounts)
            {
                foreach (var pair in account.SyncPairs.Where(p => p.Enabled && p.Mode == SyncMode.FullSync && p.AutoSync))
                {
                    try
                    {
                        FullSync.StartAutoSync(account.Id, pair);
                    }
                    catch
                    {
                        // e.g. the local folder no longer exists; the user can re-enable from the card.
                    }
                }
            }
        }

        /// <summary>
        /// Re-establishes the Cloud Filter providers for every enabled On-demand pair on launch, so their
        /// folders show on-demand placeholders and hydrate on open. Best-effort and per-pair isolated —
        /// a failure (e.g. an expired session) is left for the user to fix via "Set up" / "Sign in again".
        /// When <paramref name="startAutoSync"/> is false (the "start syncing on launch" setting is off),
        /// providers still connect — the folders stay browsable — but no background watcher/poll starts.
        /// </summary>
        private async Task StartOnDemandPairsAsync(bool startAutoSync)
        {
            if (!CloudSync.IsSupported)
            {
                return;
            }

            foreach (var account in SettingsStore.Load().Accounts)
            {
                foreach (var pair in account.SyncPairs.Where(p => p.Enabled && p.Mode == SyncMode.OnDemand))
                {
                    try
                    {
                        await CloudSync.EnableAsync(account.Id, pair);

                        // Re-establish the background watcher for folders the user left on auto-sync.
                        if (startAutoSync && pair.AutoSync)
                        {
                            CloudSync.StartAutoSync(account.Id, pair);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Surfaced when the user opens the folder card and clicks "Set up on-demand". Was
                        // previously silent even for a real failure (e.g. EnableAsync's listing timeout) —
                        // logged so a launch-time enable failure leaves a trace instead of just an
                        // unexplained "provider exited unexpectedly" the next time Explorer touches it.
                        PawsLog.Write($"Enabling on-demand sync for pair '{pair.Id}' ({pair.LocalPath}) failed at launch: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
    }
}
