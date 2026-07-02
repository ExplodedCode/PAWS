using System;
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

        public App()
        {
            InitializeComponent();

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
        }

        /// <summary>Convenience accessor for the strongly-typed application instance.</summary>
        public static App Instance => (App)Current;

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
                    catch
                    {
                        // Surfaced when the user opens the folder card and clicks "Set up on-demand".
                    }
                }
            }
        }
    }
}
