using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAWS.Views;

namespace PAWS
{
    /// <summary>
    /// Host window. Shows the setup flow on first run and the home screen afterwards, and implements
    /// "close keeps running in the background" via a tray icon (the OneDrive/Proton model).
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private bool _allowClose;

        public MainWindow()
        {
            InitializeComponent();

            // Blend the caption area into the Mica backdrop with a custom title bar.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // Cap the window width: the content column maxes out at ~920 DIPs, so anything wider is just
            // empty space on both sides. Presenter sizes are physical pixels — scale by the monitor DPI.
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
                presenter.PreferredMaximumWidth = (int)(1000 * scale);
                presenter.PreferredMinimumWidth = (int)(640 * scale);
                presenter.PreferredMinimumHeight = (int)(420 * scale);
            }

            AppWindow.Closing += OnClosing;
            TrayIcon.LeftClickCommand = new RelayCommand(ShowFromTray);

            // Build the tray context menu in code with Command bindings (NOT XAML Click= handlers): the
            // H.NotifyIcon flyout renders the items but does not reliably route their Click events to the
            // code-behind — confirmed live 2026-07-17 (the menu showed "Open PAWS"/"Quit" but clicking
            // either did nothing, and a diagnostic log line at the very top of each Click handler never
            // wrote, proving the handlers were never entered). Commands work — same mechanism as
            // LeftClickCommand above — because they don't depend on routed-event delivery through the
            // flyout's popup. RelayCommand is defined at the bottom of this file.
            var trayMenu = new MenuFlyout();
            var openItem = new MenuFlyoutItem { Text = "Open PAWS", Command = new RelayCommand(OnTrayOpen) };
            var quitItem = new MenuFlyoutItem { Text = "Quit", Command = new RelayCommand(OnTrayQuit) };
            trayMenu.Items.Add(openItem);
            trayMenu.Items.Add(new MenuFlyoutSeparator());
            trayMenu.Items.Add(quitItem);
            TrayIcon.ContextFlyout = trayMenu;

            // Proactively tell the user when an account's Proton session has expired, rather than
            // leaving them to notice a cryptic sync failure and connect the dots themselves — this is
            // the one case that needs a notification even while the window is hidden in the tray.
            App.Instance.AccountSessionExpired += OnAccountSessionExpired;
            App.Instance.DriveTimedOut += OnDriveTimedOut;

            // Set the tray image from a real .ico via the Icon property — H.NotifyIcon's XAML IconSource
            // (ms-appx URI) conversion path throws COMException 0x800C000E on every launch. Best-effort:
            // without an image the tray item still works (tooltip + menu).
            try
            {
                TrayIcon.Icon = new System.Drawing.Icon(
                    System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "PAWS.ico"));
            }
            catch
            {
            }

            if (App.Instance.IsConfigured)
            {
                RootFrame.Navigate(typeof(HomePage));
            }
            else
            {
                RootFrame.Navigate(typeof(SetupPage));
            }
        }

        public void NavigateToHome() => RootFrame.Navigate(typeof(HomePage));

        public void NavigateToSetup() => RootFrame.Navigate(typeof(SetupPage));

        public void NavigateToSettings() => RootFrame.Navigate(typeof(SettingsPage));

        public void ShowFromTray()
        {
            AppWindow.Show();
            Activate();
        }

        // A Proton session's refresh token has expired — no automatic recovery is possible past this
        // point, so tell the user right away (even if the window is hidden in the tray) rather than
        // leaving them to notice a sync failure and connect the dots. Best-effort: a failed notification
        // still leaves the account visibly flagged next time the window is opened (HomePage checks the
        // account's stored session directly), so there's no silent dead end either way.
        private void OnAccountSessionExpired(string accountId)
        {
            try
            {
                var account = App.Instance.SettingsStore.Load().Accounts.FirstOrDefault(a => a.Id == accountId);
                var label = account?.Label ?? "Your Proton account";
                TrayIcon.ShowNotification(
                    "PAWS — sign-in required",
                    $"{label}'s Proton session has expired. Open PAWS and use Options ▸ Sign in again to keep syncing.",
                    H.NotifyIcon.Core.NotificationIcon.Warning);
            }
            catch
            {
            }
        }

        // A Drive call timed out (see CloudSyncService.DriveTimeout's remarks) — this is a nudge, not a
        // confirmed-dead session (that's OnAccountSessionExpired's job), so the wording deliberately
        // suggests re-signing-in as something worth TRYING rather than something required. Throttled per
        // pair at the App layer, so this fires at most once per pair per cooldown window, not on every
        // retry of a persistent hang.
        private void OnDriveTimedOut(string accountId, string pairId)
        {
            try
            {
                var pair = App.Instance.SettingsStore.Load().Accounts
                    .FirstOrDefault(a => a.Id == accountId)?.SyncPairs
                    .FirstOrDefault(p => p.Id == pairId);
                var label = pair is null ? "A synced folder" : System.IO.Path.GetFileName(pair.LocalPath.TrimEnd('\\', '/'));
                TrayIcon.ShowNotification(
                    "PAWS — connection trouble",
                    $"Couldn't reach Proton Drive for '{label}'. If this keeps happening, try Options ▸ Sign in again.",
                    H.NotifyIcon.Core.NotificationIcon.Warning);
            }
            catch
            {
            }
        }

        // Invoked via the tray menu item's Command (see ctor) — parameterless, not a RoutedEventHandler.
        private void OnTrayOpen()
        {
            PAWS.Core.Diagnostics.PawsLog.Write("Tray menu: Open invoked.");
            ShowFromTray();
        }

        private void OnTrayQuit()
        {
            PAWS.Core.Diagnostics.PawsLog.Write("Tray menu: Quit invoked.");
            _allowClose = true;
            TrayIcon.Dispose();
            Close();
            Application.Current.Exit();

            // Application.Exit() alone is NOT sufficient here — confirmed live: with only the two calls
            // above, the process stayed alive and responding (Get-Process showed it running long after
            // "Quit" was clicked, and a second attempt still needed Task Manager to actually end it).
            // Something outside the normal WinUI lifecycle — most likely H.NotifyIcon's own native
            // message-pump window/hook, which TrayIcon.Dispose() may not fully release synchronously —
            // keeps the process alive past Application.Exit()'s graceful shutdown path. Environment.Exit
            // is the only thing that reliably ends it. Safe here: the app is already built to tolerate
            // abrupt termination as a normal occurrence, not an edge case (see the "STARTUP CRASH after
            // force-close mid-upload" fix and the offline-changes catch-up-push logic elsewhere — both
            // exist specifically because abrupt process death was already expected, not new risk this adds).
            Environment.Exit(0);
        }

        private void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
        {
            if (_allowClose)
            {
                return;
            }

            // "Keep running in the background" off → closing the window really exits the app.
            if (!App.Instance.SettingsStore.Load().RunInBackground)
            {
                _allowClose = true;
                TrayIcon.Dispose();
                return;
            }

            // Don't exit — just hide. Background sync keeps running; the tray icon reopens the window.
            e.Cancel = true;
            sender.Hide();
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
    }

    /// <summary>Minimal ICommand used for the tray icon's left-click action.</summary>
    internal sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;

        public RelayCommand(Action execute) => _execute = execute;

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();
    }
}
