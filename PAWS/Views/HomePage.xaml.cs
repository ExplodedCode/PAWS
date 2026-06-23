using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAWS.Core.Configuration;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PAWS.Views
{
    /// <summary>
    /// Manages the configured Proton accounts and their folder mappings. Supports multiple accounts
    /// (including the same email more than once) and multiple folders per account.
    /// </summary>
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            InitializeComponent();
            Loaded += (_, _) => Refresh();
        }

        private void Refresh()
        {
            AccountsPanel.Children.Clear();
            var settings = App.Instance.SettingsStore.Load();

            if (settings.Accounts.Count == 0)
            {
                AccountsPanel.Children.Add(new TextBlock { Text = "No accounts yet. Click \"Add account\" to begin.", Opacity = 0.7 });
                return;
            }

            foreach (var account in settings.Accounts)
            {
                AccountsPanel.Children.Add(BuildAccountCard(account));
            }
        }

        private void OnAddAccountClicked(object sender, RoutedEventArgs e)
            => App.Instance.Window?.NavigateToSetup();

        private Expander BuildAccountCard(ProtonAccount account)
        {
            var title = new TextBlock
            {
                Text = account.Label,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            };

            var removeAccount = new Button { Content = "Remove account" };
            removeAccount.Click += async (_, _) => await RemoveAccountAsync(account);

            var header = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(title);
            Grid.SetColumn(removeAccount, 1);
            header.Children.Add(removeAccount);

            var content = new StackPanel { Spacing = 6 };
            if (account.SyncPairs.Count == 0)
            {
                content.Children.Add(new TextBlock { Text = "No folders yet.", Opacity = 0.7 });
            }
            else
            {
                foreach (var pair in account.SyncPairs)
                {
                    content.Children.Add(BuildPairRow(account, pair));
                }
            }

            var addFolder = new Button { Content = "Add folder", Margin = new Thickness(0, 8, 0, 0) };
            addFolder.Click += async (_, _) => await AddFolderAsync(account);
            content.Children.Add(addFolder);

            return new Expander
            {
                Header = header,
                Content = content,
                IsExpanded = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
        }

        private Grid BuildPairRow(ProtonAccount account, SyncPair pair)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = $"{pair.LocalPath}    ⇄    {pair.RemotePath}     [{pair.Mode}]",
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });

            var open = new Button { Content = "Open", Margin = new Thickness(8, 0, 0, 0) };
            open.Click += (_, _) =>
            {
                if (Directory.Exists(pair.LocalPath))
                {
                    Process.Start(new ProcessStartInfo { FileName = pair.LocalPath, UseShellExecute = true });
                }
            };
            Grid.SetColumn(open, 1);
            row.Children.Add(open);

            var remove = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += (_, _) =>
            {
                App.Instance.CreateSetupWorkflow().RemoveSyncPair(account.Id, pair.Id);
                Refresh();
            };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);

            return row;
        }

        private async Task RemoveAccountAsync(ProtonAccount account)
        {
            var dialog = new ContentDialog
            {
                Title = "Remove account",
                Content = $"Remove {account.Label} and its stored credentials from this PC?",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            App.Instance.CreateSetupWorkflow().RemoveAccount(account.Id);

            if (!App.Instance.IsConfigured)
            {
                App.Instance.Window?.NavigateToSetup();
                return;
            }

            Refresh();
        }

        private async Task AddFolderAsync(ProtonAccount account)
        {
            var localBox = new TextBox { Header = "Local folder", IsReadOnly = true, PlaceholderText = "Choose a folder…" };
            var browse = new Button { Content = "Browse…", Margin = new Thickness(0, 8, 0, 0) };
            browse.Click += async (_, _) =>
            {
                var picked = await PickFolderAsync();
                if (picked is not null)
                {
                    localBox.Text = picked;
                }
            };

            var remoteBox = new TextBox { Header = "Proton Drive folder", Text = "/", PlaceholderText = "/Backup/Laptop" };
            var modeBox = new ComboBox { Header = "Sync mode", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            modeBox.Items.Add("On-demand");
            modeBox.Items.Add("Full sync");
            modeBox.Items.Add("Cloud-only");

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(localBox);
            panel.Children.Add(browse);
            panel.Children.Add(remoteBox);
            panel.Children.Add(modeBox);

            var dialog = new ContentDialog
            {
                Title = $"Add folder to {account.Email}",
                Content = panel,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(localBox.Text))
            {
                return;
            }

            var pair = new SyncPair
            {
                LocalPath = localBox.Text.Trim(),
                RemotePath = string.IsNullOrWhiteSpace(remoteBox.Text) ? "/" : remoteBox.Text.Trim(),
                Mode = (SyncMode)Math.Max(0, modeBox.SelectedIndex),
            };

            App.Instance.CreateSetupWorkflow().AddSyncPair(account.Id, pair);
            Refresh();
        }

        private static async Task<string?> PickFolderAsync()
        {
            var window = App.Instance.Window;
            if (window is null)
            {
                return null;
            }

            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
    }
}
