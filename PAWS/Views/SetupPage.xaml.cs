using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAWS.Core.Configuration;
using PAWS.Core.Proton;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PAWS.Views
{
    /// <summary>
    /// First-run / add-account onboarding. Primary login is browser-based (session forking): the user
    /// signs in on Proton's website, the app receives a forked session, and then picks a folder to sync.
    /// </summary>
    public sealed partial class SetupPage : Page
    {
        private ProtonSession? _session;
        private CancellationTokenSource? _signInCts;

        public SetupPage()
        {
            InitializeComponent();

            // Only offer "back" when there is an existing account to return to (i.e. adding another).
            BackButton.Visibility = App.Instance.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnBackClicked(object sender, RoutedEventArgs e)
        {
            _signInCts?.Cancel();
            App.Instance.Window?.NavigateToHome();
        }

        private async void OnSignInClicked(object sender, RoutedEventArgs e)
        {
            // The authenticator runs on background threads (it uses ConfigureAwait(false)), so every
            // UI touch from its callback or continuation must be marshalled back to the UI thread.
            var ui = DispatcherQueue;

            ErrorBar.IsOpen = false;
            _signInCts?.Cancel();
            _signInCts = new CancellationTokenSource();
            var token = _signInCts.Token;

            SetSignInBusy(true);
            SignInStatus.Text = "Opening Proton sign-in…";

            try
            {
                var result = await App.Instance.WebAuthenticator.SignInAsync(
                    challenge =>
                    {
                        // Invoked on a background thread — hop to the UI thread to open the browser
                        // and update status.
                        ui.TryEnqueue(async () =>
                        {
                            try
                            {
                                await Windows.System.Launcher.LaunchUriAsync(new Uri(challenge.Url));
                            }
                            catch
                            {
                                // If the browser can't be launched, the user can still complete it manually.
                            }

                            SignInInfo.Title = "Finish signing in in your browser";
                            SignInInfo.Message = $"A Proton login page opened. Verification code: {challenge.UserCode}. Waiting for you to finish…";
                            SignInInfo.IsOpen = true;
                        });

                        return Task.CompletedTask;
                    },
                    token);

                ui.TryEnqueue(() =>
                {
                    SetSignInBusy(false);
                    SignInInfo.IsOpen = false;

                    if (!result.IsSuccess)
                    {
                        SignInStatus.Text = string.Empty;
                        ShowError(result.Message ?? "Sign-in failed.");
                        return;
                    }

                    _session = result.Session!;
                    SignInStatus.Text = $"✓ Signed in as {_session.Username}";
                    SignInButton.Content = "Sign in again";
                    EnableFolderSection(true);
                });
            }
            catch (OperationCanceledException)
            {
                ui.TryEnqueue(() =>
                {
                    SetSignInBusy(false);
                    SignInInfo.IsOpen = false;
                    SignInStatus.Text = string.Empty;
                });
            }
            catch (Exception ex)
            {
                ui.TryEnqueue(() =>
                {
                    SetSignInBusy(false);
                    SignInInfo.IsOpen = false;
                    ShowError(ex.Message);
                });
            }
        }

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

        private void OnFinishClicked(object sender, RoutedEventArgs e)
        {
            if (_session is null)
            {
                ShowError("Please sign in first.");
                return;
            }

            if (string.IsNullOrWhiteSpace(LocalFolderBox.Text))
            {
                ShowError("Choose a local folder to sync.");
                return;
            }

            var pair = new SyncPair
            {
                LocalPath = LocalFolderBox.Text.Trim(),
                RemotePath = string.IsNullOrWhiteSpace(RemotePathBox.Text) ? "/" : RemotePathBox.Text.Trim(),
                Mode = (SyncMode)Math.Max(0, ModeBox.SelectedIndex),
            };

            App.Instance.CreateSetupWorkflow().AddAccount(_session, pair, LabelBox.Text?.Trim());
            App.Instance.Window?.NavigateToHome();
        }

        private void EnableFolderSection(bool enabled)
        {
            FolderSection.IsEnabled = enabled;
            FolderSection.Opacity = enabled ? 1.0 : 0.5;
        }

        private void SetSignInBusy(bool busy)
        {
            SignInBusy.IsActive = busy;
            SignInButton.IsEnabled = !busy;
        }

        private void ShowError(string message)
        {
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
        }
    }
}
