using Microsoft.UI.Xaml;
using PAWS.Core.Abstractions;
using PAWS.Core.Drive;
using PAWS.Core.Proton;
using PAWS.Core.Setup;
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
        }

        /// <summary>Convenience accessor for the strongly-typed application instance.</summary>
        public static App Instance => (App)Current;

        public PawsPaths Paths { get; }

        public ISettingsStore SettingsStore { get; }

        public ISecretStore SecretStore { get; }

        public IWebProtonAuthenticator WebAuthenticator { get; }

        public IProtonDriveClientFactory DriveClientFactory { get; }

        public MainWindow? Window => _window;

        public SetupWorkflow CreateSetupWorkflow() => new(SettingsStore, SecretStore);

        /// <summary>True once at least one Proton account has been added.</summary>
        public bool IsConfigured => SettingsStore.Load().Accounts.Count > 0;

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
