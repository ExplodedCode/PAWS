using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAWS.Infrastructure.Startup;
using PAWS.Infrastructure.Storage;

namespace PAWS.Views
{
    /// <summary>
    /// App-wide settings: sync speed limits (persisted now, enforced once the transfer throttle ships)
    /// and the reset action that signs out all accounts and removes every folder configuration.
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        // True while the controls are being populated from settings, so change handlers don't save.
        private bool _loading = true;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var settings = App.Instance.SettingsStore.Load();

            _loading = true;
            StartupToggle.IsOn = settings.RunOnStartup;
            BackgroundToggle.IsOn = settings.RunInBackground;
            AutoSyncStartToggle.IsOn = settings.AutoSyncOnLaunch;

            AutoDehydrateToggle.IsOn = settings.AutoDehydrateDays is not null;
            AutoDehydrateDaysBox.IsEnabled = AutoDehydrateToggle.IsOn;
            AutoDehydrateDaysBox.Value = settings.AutoDehydrateDays ?? 14;

            UploadLimitToggle.IsOn = settings.UploadLimitKBps is not null;
            UploadLimitBox.IsEnabled = UploadLimitToggle.IsOn;
            UploadLimitBox.Value = settings.UploadLimitKBps ?? 1024;

            DownloadLimitToggle.IsOn = settings.DownloadLimitKBps is not null;
            DownloadLimitBox.IsEnabled = DownloadLimitToggle.IsOn;
            DownloadLimitBox.Value = settings.DownloadLimitKBps ?? 1024;
            _loading = false;
        }

        private void OnBackClicked(object sender, RoutedEventArgs e)
            => App.Instance.Window?.NavigateToHome();

        // One handler for all three behavior switches — they persist together. Run-on-startup applies to
        // the Windows registry right away; the other two are consulted where they act (window close,
        // app launch), so persisting is all they need.
        private void OnBehaviorToggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            var settings = App.Instance.SettingsStore.Load();
            settings.RunOnStartup = StartupToggle.IsOn;
            settings.RunInBackground = BackgroundToggle.IsOn;
            settings.AutoSyncOnLaunch = AutoSyncStartToggle.IsOn;
            App.Instance.SettingsStore.Save(settings);

            StartupRegistration.Apply(settings.RunOnStartup);
        }

        private void OnAutoDehydrateToggled(object sender, RoutedEventArgs e)
        {
            AutoDehydrateDaysBox.IsEnabled = AutoDehydrateToggle.IsOn;
            SaveAutoDehydrate();
        }

        private void OnAutoDehydrateValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
            => SaveAutoDehydrate();

        private void SaveAutoDehydrate()
        {
            if (_loading)
            {
                return;
            }

            var settings = App.Instance.SettingsStore.Load();
            settings.AutoDehydrateDays = AutoDehydrateToggle.IsOn ? ToDays(AutoDehydrateDaysBox.Value) : null;
            App.Instance.SettingsStore.Save(settings);
        }

        // NumberBox reports NaN while empty; fall back to the default sweep age.
        private static int ToDays(double value)
            => double.IsNaN(value) || value < 1 ? 14 : (int)value;

        private void OnUploadLimitToggled(object sender, RoutedEventArgs e)
        {
            UploadLimitBox.IsEnabled = UploadLimitToggle.IsOn;
            SaveLimits();
        }

        private void OnDownloadLimitToggled(object sender, RoutedEventArgs e)
        {
            DownloadLimitBox.IsEnabled = DownloadLimitToggle.IsOn;
            SaveLimits();
        }

        private void OnLimitValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
            => SaveLimits();

        // Load-modify-save so the accounts list is never clobbered by a stale in-memory copy. The live
        // throttle is updated too, so the new limit applies immediately — including mid-transfer.
        private void SaveLimits()
        {
            if (_loading)
            {
                return;
            }

            var settings = App.Instance.SettingsStore.Load();
            settings.UploadLimitKBps = UploadLimitToggle.IsOn ? ToKBps(UploadLimitBox.Value) : null;
            settings.DownloadLimitKBps = DownloadLimitToggle.IsOn ? ToKBps(DownloadLimitBox.Value) : null;
            App.Instance.SettingsStore.Save(settings);

            App.Instance.Throttle.UploadLimitKBps = settings.UploadLimitKBps;
            App.Instance.Throttle.DownloadLimitKBps = settings.DownloadLimitKBps;
        }

        // NumberBox reports NaN while empty; fall back to a sensible default.
        private static int ToKBps(double value)
            => double.IsNaN(value) || value < 16 ? 1024 : (int)value;

        private async void OnResetClicked(object sender, RoutedEventArgs e)
            => await ResetAppAsync();

        /// <summary>
        /// Signs out every account and removes all folder configurations: stops every watcher/poll,
        /// disconnects the on-demand providers, clears per-pair sync state, and deletes each account's
        /// stored credentials and settings. Local files and Proton Drive content are left untouched.
        /// </summary>
        private async Task ResetAppAsync()
        {
            var confirm = new ContentDialog
            {
                Title = "Reset PAWS?",
                Content = "This signs out every account and removes all folder sync configurations from this PC.\n\n"
                          + "No files are deleted — everything on your PC and on Proton Drive stays where it is.",
                PrimaryButtonText = "Reset",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            ResetButton.IsEnabled = false;

            var settings = App.Instance.SettingsStore.Load();
            var workflow = App.Instance.CreateSetupWorkflow();
            var populatedStore = new JsonPopulatedFolderStore(App.Instance.Paths);

            foreach (var account in settings.Accounts)
            {
                foreach (var pair in account.SyncPairs)
                {
                    // Best-effort teardown per pair — a failure on one must not block the reset.
                    try
                    {
                        App.Instance.CloudSync.StopAutoSync(pair.Id);
                        App.Instance.FullSync.StopAutoSync(pair.Id);
                        App.Instance.CloudSync.Disable(pair.Id);
                        App.Instance.SyncStateStore.Clear(pair.Id);
                        populatedStore.Clear(pair.Id);
                    }
                    catch
                    {
                        // Keep going — remaining pairs and the account removal still apply.
                    }
                }

                workflow.RemoveAccount(account.Id);
            }

            App.Instance.Window?.NavigateToSetup();
        }
    }
}
