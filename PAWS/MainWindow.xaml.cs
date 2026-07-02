using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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

        private void OnTrayOpen(object sender, RoutedEventArgs e) => ShowFromTray();

        private void OnTrayQuit(object sender, RoutedEventArgs e)
        {
            _allowClose = true;
            TrayIcon.Dispose();
            Close();
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
