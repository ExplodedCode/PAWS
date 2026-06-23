using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PAWS.Views
{
    /// <summary>Status/configuration screen shown once the machine is linked to a Proton account.</summary>
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            InitializeComponent();
            Loaded += (_, _) => Refresh();
        }

        private void Refresh()
        {
            var settings = App.Instance.SettingsStore.Load();
            AccountText.Text = $"Signed in as {settings.AccountEmail}";
            PairsList.ItemsSource = settings.SyncPairs
                .Select(p => $"{p.LocalPath}    ⇄    {p.RemotePath}     [{p.Mode}]")
                .ToList();
        }

        private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
        {
            var path = App.Instance.SettingsStore.Load().SyncPairs.FirstOrDefault()?.LocalPath;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
        }

        private void OnReconfigureClicked(object sender, RoutedEventArgs e)
            => App.Instance.Window?.NavigateToSetup();

        private void OnSignOutClicked(object sender, RoutedEventArgs e)
        {
            App.Instance.SecretStore.ClearProtonSecrets();

            var settings = App.Instance.SettingsStore.Load();
            settings.SetupCompleted = false;
            App.Instance.SettingsStore.Save(settings);

            App.Instance.Window?.NavigateToSetup();
        }
    }
}
