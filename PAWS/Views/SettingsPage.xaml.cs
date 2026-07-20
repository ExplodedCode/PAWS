using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAWS.Core.Configuration;
using PAWS.Infrastructure.Startup;

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
        private async void OnBehaviorToggled(object sender, RoutedEventArgs e)
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

            await StartupRegistration.ApplyAsync(settings.RunOnStartup);
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

        /// <summary>
        /// Re-registers every on-demand folder's sync root, which re-runs the shell's AllowPinning check
        /// that controls whether Explorer's "Always keep on this device"/"Free up space" items appear —
        /// the same self-heal that already runs on every launch, just triggerable on demand for when the
        /// items have gone missing between launches (e.g. a transient shell lookup failure at register
        /// time — see CloudFilterPlaceholderEngine's remarks). Doesn't touch connections, sync state, or
        /// files.
        /// </summary>
        private async void OnRepairContextMenuClicked(object sender, RoutedEventArgs e)
        {
            RepairContextMenuButton.IsEnabled = false;
            RepairContextMenuStatusText.Text = "Repairing…";

            // Task.Run: the shell registration underneath blocks its calling thread (synchronous WinRT
            // interop), so keep it off the UI thread even though the repair API itself is async.
            var repaired = await Task.Run(async () =>
            {
                var settings = App.Instance.SettingsStore.Load();
                var count = 0;
                foreach (var account in settings.Accounts)
                {
                    foreach (var pair in account.SyncPairs.Where(p => p.Mode == SyncMode.OnDemand && p.Enabled))
                    {
                        try
                        {
                            await App.Instance.CloudSync.RepairContextMenuAsync(account.Id, pair);
                            count++;
                        }
                        catch
                        {
                            // Best-effort per folder — one failure shouldn't stop the rest.
                        }
                    }
                }

                return count;
            });

            RepairContextMenuStatusText.Text = repaired == 0
                ? "No on-demand folders to repair."
                : $"Done — repaired {repaired} folder{(repaired == 1 ? "" : "s")}. If items are still missing, try reopening Explorer.";
            RepairContextMenuButton.IsEnabled = true;
        }

        private async void OnResetClicked(object sender, RoutedEventArgs e)
            => await ResetAppAsync();

        /// <summary>
        /// Signs out every account and removes all folder configurations: stops every watcher/poll,
        /// decommissions each synced folder back to an ordinary folder (files already on this PC are kept
        /// as normal files; online-only placeholders are removed locally — their content stays on Proton
        /// Drive), unregisters the sync roots, clears per-pair sync state, and deletes each account's
        /// stored credentials and settings. Nothing on Proton Drive is touched.
        /// </summary>
        private async Task ResetAppAsync()
        {
            var confirm = new ContentDialog
            {
                Title = "Reset PAWS?",
                Content = "This signs out every account and removes all folder sync configurations from this PC. "
                          + "Each synced folder goes back to being a regular folder.\n\n"
                          + "Files already on this PC are kept as normal files. Online-only files (the ones that "
                          + "download when opened) are removed from your folders — they stay on Proton Drive.",
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

            foreach (var account in settings.Accounts)
            {
                foreach (var pair in account.SyncPairs)
                {
                    // Best-effort teardown per pair — a failure on one must not block the reset.
                    // Decommission stops auto-sync, cleans the placeholder tree (keeping local files),
                    // unregisters the sync root, and clears the pair's persisted state.
                    try
                    {
                        App.Instance.FullSync.StopAutoSync(pair.Id);
                        await App.Instance.CloudSync.DecommissionAsync(pair, keepLocalFiles: true);
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
