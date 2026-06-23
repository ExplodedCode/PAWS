using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAWS.Core.Configuration;
using PAWS.Core.Proton;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PAWS.Views
{
    /// <summary>First-run onboarding: capture Proton credentials + a folder mapping, then persist them.</summary>
    public sealed partial class SetupPage : Page
    {
        public SetupPage()
        {
            InitializeComponent();
        }

        private void OnTwoFactorToggled(object sender, RoutedEventArgs e)
            => TwoFactorBox.Visibility = TwoFactorToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

        private void OnMailboxToggled(object sender, RoutedEventArgs e)
            => MailboxPasswordInput.Visibility = MailboxToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

        private async void OnBrowseClicked(object sender, RoutedEventArgs e)
        {
            var window = App.Instance.Window;
            if (window is null)
            {
                return;
            }

            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*"); // required for FolderPicker to work in a desktop app
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                LocalFolderBox.Text = folder.Path;
            }
        }

        private async void OnConnectClicked(object sender, RoutedEventArgs e)
        {
            ErrorBar.IsOpen = false;

            var email = EmailBox.Text?.Trim() ?? string.Empty;
            var password = PasswordInput.Password ?? string.Empty;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            {
                ShowError("Enter your Proton email and password.");
                return;
            }

            if (string.IsNullOrWhiteSpace(LocalFolderBox.Text))
            {
                ShowError("Choose a local folder to sync.");
                return;
            }

            SetBusy(true);
            try
            {
                var login = new ProtonLoginRequest
                {
                    Username = email,
                    Password = password,
                    TwoFactorCode = TwoFactorToggle.IsOn ? TwoFactorBox.Text?.Trim() : null,
                    MailboxPassword = MailboxToggle.IsOn ? MailboxPasswordInput.Password : null,
                };

                var pair = new SyncPair
                {
                    LocalPath = LocalFolderBox.Text.Trim(),
                    RemotePath = string.IsNullOrWhiteSpace(RemotePathBox.Text) ? "/" : RemotePathBox.Text.Trim(),
                    Mode = (SyncMode)Math.Max(0, ModeBox.SelectedIndex),
                };

                var result = await App.Instance.CreateSetupWorkflow().AuthenticateAndPersistAsync(login, pair);
                if (!result.IsSuccess)
                {
                    ShowError($"{result.Status}: {result.Message}");
                    return;
                }

                App.Instance.Window?.NavigateToHome();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ShowError(string message)
        {
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
        }

        private void SetBusy(bool busy)
        {
            Busy.IsActive = busy;
            ConnectButton.IsEnabled = !busy;
        }
    }
}
