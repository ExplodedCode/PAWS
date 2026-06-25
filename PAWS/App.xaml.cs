using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PAWS.CloudFilter;
using PAWS.Core.Abstractions;
using PAWS.Core.Configuration;
using PAWS.Core.Drive;
using PAWS.Core.Proton;
using PAWS.Core.Setup;
using PAWS.Core.Sync;
using PAWS.Infrastructure.Proton;
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

        public App()
        {
            InitializeComponent();

            Paths = new PawsPaths();
            SettingsStore = new JsonSettingsStore(Paths);
            SecretStore = new DpapiSecretStore(Paths);

            // Login is browser-based (session forking) — no password handled in-app, supports
            // passkeys/2FA, and avoids Proton's anti-abuse blocks. There is no SRP/password path.
            WebAuthenticator = new WebProtonAuthenticator();

            // Builds a connected Proton Drive client for an account by resuming its stored session.
            DriveClientFactory = new ProtonDriveClientFactory(SecretStore);

            // Sync engine: plan + apply, persisting last-known state per pair.
            SyncStateStore = new JsonSyncStateStore(Paths);
            SyncEngine = new SyncEngine(DriveClientFactory, SyncStateStore);

            // Files-on-demand: registers sync roots + serves hydration for On-demand pairs.
            CloudSync = new CloudSyncService(new CloudFilterPlaceholderEngine(), DriveClientFactory);
        }

        /// <summary>Convenience accessor for the strongly-typed application instance.</summary>
        public static App Instance => (App)Current;

        public PawsPaths Paths { get; }

        public ISettingsStore SettingsStore { get; }

        public ISecretStore SecretStore { get; }

        public IWebProtonAuthenticator WebAuthenticator { get; }

        public IProtonDriveClientFactory DriveClientFactory { get; }

        public ISyncStateStore SyncStateStore { get; }

        public SyncEngine SyncEngine { get; }

        public CloudSyncService CloudSync { get; }

        public MainWindow? Window => _window;

        public SetupWorkflow CreateSetupWorkflow() => new(SettingsStore, SecretStore);

        /// <summary>True once at least one Proton account has been added.</summary>
        public bool IsConfigured => SettingsStore.Load().Accounts.Count > 0;

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();

            // Bring any On-demand folders online in the background (placeholders + hydration provider).
            _ = StartOnDemandPairsAsync();
        }

        /// <summary>
        /// Re-establishes the Cloud Filter providers for every enabled On-demand pair on launch, so their
        /// folders show on-demand placeholders and hydrate on open. Best-effort and per-pair isolated —
        /// a failure (e.g. an expired session) is left for the user to fix via "Set up" / "Sign in again".
        /// </summary>
        private async Task StartOnDemandPairsAsync()
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
