using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PAWS.Core.Configuration;
using PAWS.Core.Sync;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PAWS.Views
{
    /// <summary>
    /// Manages the configured Proton accounts and their folder mappings. Each folder is a card with a
    /// clear sync status (animated while syncing, ✓ up to date, ⚠ needs attention), a play/pause button
    /// for automatic sync, Open, and a "Manual" menu for on-demand sync actions. Account-level actions
    /// (add folder, sign in again, remove) live in an "Options" menu on the account header.
    /// </summary>
    public sealed partial class HomePage : Page
    {
        private const string PlayGlyph = "";
        private const string PauseGlyph = "";

        private enum PairState
        {
            Auto,      // automatic sync on, idle — watching for changes
            Paused,    // automatic sync off
            Syncing,   // a sync is running right now (animated)
            UpToDate,  // last sync finished clean
            Attention, // last sync had an error / held deletions
        }

        private sealed class PairStatusUi
        {
            public required ProgressRing Ring { get; init; }
            public required FontIcon Icon { get; init; }
            public required TextBlock Text { get; init; }
        }

        // Per-pair status UI, rebuilt on each Refresh and updated from the (background) sync events,
        // marshalled back to the UI thread.
        private readonly Dictionary<string, PairStatusUi> _pairStatus = new(StringComparer.Ordinal);

        public HomePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            App.Instance.CloudSync.AutoSyncStarted += OnAutoSyncStarted;
            App.Instance.CloudSync.AutoSyncCompleted += OnAutoSyncCompleted;
            App.Instance.CloudSync.AutoPullStarted += OnAutoPullStarted;
            App.Instance.CloudSync.AutoPullCompleted += OnAutoPullCompleted;
            App.Instance.FullSync.SyncStarted += OnFullSyncStarted;
            App.Instance.FullSync.SyncCompleted += OnFullSyncCompleted;
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            App.Instance.CloudSync.AutoSyncStarted -= OnAutoSyncStarted;
            App.Instance.CloudSync.AutoSyncCompleted -= OnAutoSyncCompleted;
            App.Instance.CloudSync.AutoPullStarted -= OnAutoPullStarted;
            App.Instance.CloudSync.AutoPullCompleted -= OnAutoPullCompleted;
            App.Instance.FullSync.SyncStarted -= OnFullSyncStarted;
            App.Instance.FullSync.SyncCompleted -= OnFullSyncCompleted;
        }

        private void Refresh()
        {
            AccountsPanel.Children.Clear();
            _pairStatus.Clear();
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
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center }); // person
            title.Children.Add(new TextBlock
            {
                Text = account.Label,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            });

            var addFolder = new MenuFlyoutItem { Text = "Add folder…", Icon = new FontIcon { Glyph = "" } };
            addFolder.Click += async (_, _) => await AddFolderAsync(account);

            var signIn = new MenuFlyoutItem { Text = "Sign in again", Icon = new FontIcon { Glyph = "" } };
            signIn.Click += async (_, _) => await ReSignInAsync(account);

            var removeAccount = new MenuFlyoutItem { Text = "Remove account…", Icon = new FontIcon { Glyph = "" } };
            removeAccount.Click += async (_, _) => await RemoveAccountAsync(account);

            var optionsFlyout = new MenuFlyout();
            optionsFlyout.Items.Add(addFolder);
            optionsFlyout.Items.Add(signIn);
            optionsFlyout.Items.Add(new MenuFlyoutSeparator());
            optionsFlyout.Items.Add(removeAccount);

            var options = new DropDownButton { Content = "Options", Flyout = optionsFlyout, VerticalAlignment = VerticalAlignment.Center };

            var header = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(title);
            Grid.SetColumn(options, 1);
            header.Children.Add(options);

            var content = new StackPanel { Spacing = 8 };
            if (account.SyncPairs.Count == 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "No folders yet — use Options ▸ Add folder to start syncing one.",
                    Opacity = 0.7,
                });
            }
            else
            {
                foreach (var pair in account.SyncPairs)
                {
                    content.Children.Add(BuildFolderCard(account, pair));
                }
            }

            return new Expander
            {
                Header = header,
                Content = content,
                IsExpanded = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
        }

        private FrameworkElement BuildFolderCard(ProtonAccount account, SyncPair pair)
        {
            // --- Left: name + mode, paths, status ---------------------------------------------------
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            nameRow.Children.Add(new FontIcon { Glyph = "", FontSize = 16, VerticalAlignment = VerticalAlignment.Center }); // folder
            nameRow.Children.Add(new TextBlock
            {
                Text = FolderDisplayName(pair.LocalPath),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            nameRow.Children.Add(BuildModePill(pair.Mode));

            var paths = new TextBlock
            {
                Text = $"{pair.LocalPath}  ⇄  {pair.RemotePath}",
                FontSize = 12,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            };
            ToolTipService.SetToolTip(paths, $"Local: {pair.LocalPath}\nProton Drive: {pair.RemotePath}");

            var ring = new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed };
            var icon = new FontIcon { FontSize = 12, Glyph = PauseGlyph, Opacity = 0.6 };
            var indicator = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            indicator.Children.Add(ring);
            indicator.Children.Add(icon);

            var statusText = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var statusRow = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 6, 0, 0) };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusRow.Children.Add(indicator);
            Grid.SetColumn(statusText, 1);
            statusRow.Children.Add(statusText);

            var info = new StackPanel();
            info.Children.Add(nameRow);
            info.Children.Add(paths);
            info.Children.Add(statusRow);

            _pairStatus[pair.Id] = new PairStatusUi { Ring = ring, Icon = icon, Text = statusText };

            // --- Right: play/pause · Open · Manual ▾ ------------------------------------------------
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
            };

            if (pair.Mode is SyncMode.OnDemand or SyncMode.FullSync)
            {
                actions.Children.Add(BuildPlayPauseButton(account, pair));
            }

            // Open is a split button: the click opens the local folder (the everyday case); the arrow
            // offers that plus viewing this folder on the Proton Drive website — a deep link into the
            // pair's remote folder, falling back to the Drive home page if the link can't be built.
            var openLocal = new MenuFlyoutItem { Text = "Open local folder", Icon = new FontIcon { Glyph = "" } }; // folder open
            openLocal.Click += (_, _) => OpenLocalFolder(pair);

            var openWeb = new MenuFlyoutItem { Text = "View on drive.proton.me", Icon = new FontIcon { Glyph = "" } }; // globe
            openWeb.Click += async (_, _) =>
            {
                var uri = new Uri("https://drive.proton.me/");
                try
                {
                    var url = await App.Instance.CloudSync.GetWebUrlAsync(account.Id, pair.RemotePath);
                    if (url is not null)
                    {
                        uri = new Uri(url);
                    }
                }
                catch
                {
                    // Best-effort deep link — fall back to the Drive home page.
                }

                await Windows.System.Launcher.LaunchUriAsync(uri);
            };

            var openFlyout = new MenuFlyout();
            openFlyout.Items.Add(openLocal);
            openFlyout.Items.Add(openWeb);

            var open = new SplitButton { Content = "Open", Flyout = openFlyout };
            ToolTipService.SetToolTip(open, "Open this folder in File Explorer — or use the arrow to view your files on the Proton Drive website.");
            open.Click += (_, _) => OpenLocalFolder(pair);
            actions.Children.Add(open);

            actions.Children.Add(BuildManualMenu(account, pair));

            // --- Card --------------------------------------------------------------------------------
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(info);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);

            var card = new Border
            {
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Child = grid,
            };

            SetIdleStatus(pair);
            return card;
        }

        private Button BuildPlayPauseButton(ProtonAccount account, SyncPair pair)
        {
            var playIcon = new FontIcon { FontSize = 12, Glyph = pair.AutoSync ? PauseGlyph : PlayGlyph };
            var playPause = new Button { Content = playIcon, Padding = new Thickness(9, 7, 9, 7) };
            ToolTipService.SetToolTip(playPause, pair.AutoSync ? "Pause automatic sync" : "Start automatic sync");

            playPause.Click += async (_, _) =>
            {
                playPause.IsEnabled = false;
                try
                {
                    if (pair.AutoSync)
                    {
                        if (pair.Mode == SyncMode.OnDemand)
                        {
                            DisableAutoSync(account, pair);
                        }
                        else
                        {
                            DisableFullAutoSync(account, pair);
                        }

                        playIcon.Glyph = PlayGlyph;
                        ToolTipService.SetToolTip(playPause, "Start automatic sync");
                    }
                    else
                    {
                        playIcon.Glyph = PauseGlyph;
                        ToolTipService.SetToolTip(playPause, "Pause automatic sync");

                        if (pair.Mode == SyncMode.OnDemand)
                        {
                            await EnableAutoSyncAsync(account, pair);
                        }
                        else
                        {
                            await EnableFullAutoSyncAsync(account, pair);
                        }
                    }
                }
                finally
                {
                    playPause.IsEnabled = true;
                }
            };

            return playPause;
        }

        private DropDownButton BuildManualMenu(ProtonAccount account, SyncPair pair)
        {
            var flyout = new MenuFlyout();

            if (pair.Mode == SyncMode.OnDemand)
            {
                var push = new MenuFlyoutItem { Text = "Push local changes to Drive", Icon = new FontIcon { Glyph = "" } };
                push.Click += async (_, _) => await SyncUpAsync(account, pair);
                flyout.Items.Add(push);

                var pull = new MenuFlyoutItem { Text = "Pull remote changes from Drive", Icon = new FontIcon { Glyph = "" } };
                pull.Click += async (_, _) => await SyncDownAsync(account, pair);
                flyout.Items.Add(pull);
            }
            else
            {
                var syncNow = new MenuFlyoutItem { Text = "Sync now (review changes first)", Icon = new FontIcon { Glyph = "" } };
                syncNow.Click += async (_, _) => await SyncNowAsync(account, pair);
                flyout.Items.Add(syncNow);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());

            var remove = new MenuFlyoutItem { Text = "Stop syncing this folder…", Icon = new FontIcon { Glyph = "" } };
            remove.Click += async (_, _) => await RemoveFolderAsync(account, pair);
            flyout.Items.Add(remove);

            var manual = new DropDownButton { Content = "Manual", Flyout = flyout };
            ToolTipService.SetToolTip(manual, "Run a sync yourself, or stop syncing this folder.");
            return manual;
        }

        private async Task RemoveFolderAsync(ProtonAccount account, SyncPair pair)
        {
            var dialog = new ContentDialog
            {
                Title = "Stop syncing this folder?",
                Content = $"PAWS will stop syncing \"{FolderDisplayName(pair.LocalPath)}\".\n\n"
                          + "Nothing is deleted — the files stay on your PC and on Proton Drive; they just won't sync anymore.",
                PrimaryButtonText = "Stop syncing",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            App.Instance.CloudSync.StopAutoSync(pair.Id); // stop the on-demand watcher/poll, if any
            App.Instance.FullSync.StopAutoSync(pair.Id); // stop the full-sync watcher/poll, if any
            App.Instance.CloudSync.Disable(pair.Id); // disconnect the on-demand provider, if any
            App.Instance.CreateSetupWorkflow().RemoveSyncPair(account.Id, pair.Id);
            Refresh();
        }

        private static void OpenLocalFolder(SyncPair pair)
        {
            if (Directory.Exists(pair.LocalPath))
            {
                Process.Start(new ProcessStartInfo { FileName = pair.LocalPath, UseShellExecute = true });
            }
        }

        private static string FolderDisplayName(string localPath)
        {
            var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? localPath : name;
        }

        private static Border BuildModePill(SyncMode mode)
        {
            var label = mode switch
            {
                SyncMode.OnDemand => "On-demand",
                SyncMode.FullSync => "Full sync",
                _ => "Cloud-only",
            };

            return new Border
            {
                Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = label, FontSize = 11, Opacity = 0.8 },
            };
        }

        private static Brush? ThemeBrush(string key)
        {
            var resources = Application.Current.Resources;
            return resources.ContainsKey(key) ? resources[key] as Brush : null;
        }

        // ---- Status handling ------------------------------------------------------------------------

        /// <summary>The resting status for a pair (no sync running): auto on, paused, or manual-only.</summary>
        private void SetIdleStatus(SyncPair pair)
        {
            if (pair.Mode is not (SyncMode.OnDemand or SyncMode.FullSync))
            {
                SetPairStatus(pair.Id, PairState.Paused, "Manual sync only — use Manual ▸ Sync now.");
            }
            else if (pair.AutoSync)
            {
                SetPairStatus(pair.Id, PairState.Auto, "Automatic sync on — watching for changes.");
            }
            else
            {
                SetPairStatus(pair.Id, PairState.Paused, "Automatic sync paused — press ▶ to resume.");
            }
        }

        private void SetPairStatus(string pairId, PairState state, string text)
        {
            if (!_pairStatus.TryGetValue(pairId, out var ui))
            {
                return;
            }

            ui.Text.Text = text;

            var syncing = state == PairState.Syncing;
            ui.Ring.IsActive = syncing;
            ui.Ring.Visibility = syncing ? Visibility.Visible : Visibility.Collapsed;
            ui.Icon.Visibility = syncing ? Visibility.Collapsed : Visibility.Visible;

            var (glyph, brush, opacity) = state switch
            {
                PairState.UpToDate => ("", ThemeBrush("SystemFillColorSuccessBrush") ?? new SolidColorBrush(Colors.SeaGreen), 1.0),
                PairState.Attention => ("", ThemeBrush("SystemFillColorCautionBrush") ?? new SolidColorBrush(Colors.Orange), 1.0),
                PairState.Paused => (PauseGlyph, null, 0.6),
                _ => ("", null, 0.6), // Auto (sync arrows)
            };

            ui.Icon.Glyph = glyph;
            ui.Icon.Opacity = opacity;
            if (brush is not null)
            {
                ui.Icon.Foreground = brush;
            }
            else
            {
                ui.Icon.ClearValue(IconElement.ForegroundProperty);
            }
        }

        // ---- Automatic sync (play/pause) --------------------------------------------------------------

        /// <summary>
        /// Turns on background auto-sync for an on-demand pair: persists the preference, makes sure the
        /// folder is set up (placeholders + provider), then starts the debounced watcher.
        /// </summary>
        private async Task EnableAutoSyncAsync(ProtonAccount account, SyncPair pair)
        {
            pair.AutoSync = true;
            App.Instance.CreateSetupWorkflow().SetPairAutoSync(account.Id, pair.Id, true);
            SetPairStatus(pair.Id, PairState.Syncing, "Setting up automatic sync…");

            try
            {
                if (!App.Instance.CloudSync.IsEnabled(pair.Id))
                {
                    await App.Instance.CloudSync.EnableAsync(account.Id, pair);
                }

                App.Instance.CloudSync.StartAutoSync(account.Id, pair);
                SetIdleStatus(pair);
            }
            catch (Exception ex)
            {
                SetPairStatus(pair.Id, PairState.Attention, $"Couldn't start automatic sync: {ex.Message}");
            }
        }

        /// <summary>Turns off background auto-sync for a pair (the on-demand provider stays connected).</summary>
        private void DisableAutoSync(ProtonAccount account, SyncPair pair)
        {
            pair.AutoSync = false;
            App.Instance.CreateSetupWorkflow().SetPairAutoSync(account.Id, pair.Id, false);
            App.Instance.CloudSync.StopAutoSync(pair.Id);
            SetIdleStatus(pair);
        }

        /// <summary>Turns on automatic two-way sync for a full-sync pair (local watcher + Drive poll).</summary>
        private Task EnableFullAutoSyncAsync(ProtonAccount account, SyncPair pair)
        {
            pair.AutoSync = true;
            App.Instance.CreateSetupWorkflow().SetPairAutoSync(account.Id, pair.Id, true);

            try
            {
                App.Instance.FullSync.StartAutoSync(account.Id, pair);
                SetIdleStatus(pair);
            }
            catch (Exception ex)
            {
                SetPairStatus(pair.Id, PairState.Attention, $"Couldn't start automatic sync: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>Turns off automatic two-way sync for a full-sync pair.</summary>
        private void DisableFullAutoSync(ProtonAccount account, SyncPair pair)
        {
            pair.AutoSync = false;
            App.Instance.CreateSetupWorkflow().SetPairAutoSync(account.Id, pair.Id, false);
            App.Instance.FullSync.StopAutoSync(pair.Id);
            SetIdleStatus(pair);
        }

        // ---- Background sync events → status line -----------------------------------------------------

        private void OnFullSyncStarted(string pairId)
            => DispatcherQueue.TryEnqueue(() => SetPairStatus(pairId, PairState.Syncing, "Syncing…"));

        private void OnFullSyncCompleted(FullSyncEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (e.NeedsReview)
                {
                    SetPairStatus(e.PairId, PairState.Attention, $"{e.PendingDeletes} deletions held for review — check Manual ▸ Sync now.");
                    return;
                }

                if (!e.Succeeded)
                {
                    SetPairStatus(e.PairId, PairState.Attention, $"Sync error: {e.Error!.Message}");
                    return;
                }

                var result = e.Result!;
                var when = DateTime.Now.ToString("t");
                if (result.Failures.Count > 0)
                {
                    SetPairStatus(e.PairId, PairState.Attention, $"{result.Completed} synced, {result.Failures.Count} failed — {result.Failures[0].Error} · {when}");
                    return;
                }

                SetPairStatus(e.PairId, PairState.UpToDate, result.Total == 0
                    ? $"Up to date · {when}"
                    : $"Synced {result.Completed} change(s){(result.Skipped > 0 ? $", {result.Skipped} conflict(s) skipped" : string.Empty)} · {when}");
            });
        }

        private void OnAutoSyncStarted(string pairId)
            => DispatcherQueue.TryEnqueue(() => SetPairStatus(pairId, PairState.Syncing, "Syncing local changes…"));

        private void OnAutoSyncCompleted(AutoSyncEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!e.Succeeded)
                {
                    SetPairStatus(e.PairId, PairState.Attention, $"Sync error: {e.Error!.Message}");
                    return;
                }

                var result = e.Result!;
                var when = DateTime.Now.ToString("t");
                if (result.Failures.Count > 0)
                {
                    // Show the actual reason (first failure); full detail + stack is in the log file.
                    SetPairStatus(e.PairId, PairState.Attention, $"{result.Completed} pushed, {result.Failures.Count} failed — {result.Failures[0].Error} · {when}");
                    return;
                }

                SetPairStatus(e.PairId, PairState.UpToDate, result.Total == 0
                    ? $"Up to date · {when}"
                    : $"Pushed {result.Completed} change(s) · {when}");
            });
        }

        private void OnAutoPullStarted(string pairId)
            => DispatcherQueue.TryEnqueue(() => SetPairStatus(pairId, PairState.Syncing, "Checking Proton Drive for changes…"));

        private void OnAutoPullCompleted(AutoPullEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!e.Succeeded)
                {
                    SetPairStatus(e.PairId, PairState.Attention, $"Sync error: {e.Error!.Message}");
                    return;
                }

                var result = e.Result!;
                var when = DateTime.Now.ToString("t");
                SetPairStatus(e.PairId, PairState.UpToDate, result.Total == 0
                    ? $"Up to date · {when}"
                    : $"Pulled {result.Created} new, {result.Updated} changed, {result.Deleted} removed · {when}");
            });
        }

        // ---- Manual sync actions ----------------------------------------------------------------------

        /// <summary>
        /// Pulls changes made on Proton Drive (new, changed, deleted files/folders) down into an on-demand
        /// folder as placeholders — no content is downloaded. Local-only changes and conflicts are left
        /// alone, so this never discards unpushed local work.
        /// </summary>
        private async Task SyncDownAsync(ProtonAccount account, SyncPair pair)
        {
            var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            var status = new TextBlock { Text = "Checking Proton Drive for changes…", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(ring);
            header.Children.Add(status);

            var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
            panel.Children.Add(header);

            var dialog = new ContentDialog
            {
                Title = $"Pull changes — {FolderDisplayName(pair.LocalPath)}",
                Content = new ScrollViewer { Content = panel, MaxHeight = 440 },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };

            dialog.Opened += async (_, _) =>
            {
                SetPairStatus(pair.Id, PairState.Syncing, "Checking Proton Drive for changes…");
                try
                {
                    var result = await App.Instance.CloudSync.PullChangesAsync(account.Id, pair);
                    ring.IsActive = false;

                    var when = DateTime.Now.ToString("t");
                    status.Text = result.Total == 0
                        ? "No remote changes — already up to date."
                        : $"Pulled remote changes: {result.Created} new, {result.Updated} changed, {result.Deleted} removed. "
                          + "New and changed files appear as on-demand placeholders (open them to download).";
                    SetPairStatus(pair.Id, PairState.UpToDate, result.Total == 0
                        ? $"Up to date · {when}"
                        : $"Pulled {result.Created} new, {result.Updated} changed, {result.Deleted} removed · {when}");
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Pull failed: {ex.Message}\n\nIf this mentions a session or token, use Options ▸ Sign in again on the account, then retry.";
                    SetPairStatus(pair.Id, PairState.Attention, $"Pull failed: {ex.Message}");
                }
            };

            await dialog.ShowAsync();
        }

        /// <summary>
        /// Brings a folder online as files-on-demand: registers the Cloud Filter sync root, creates
        /// placeholders for the remote tree, and connects the hydration provider. Non-destructive —
        /// placeholders occupy no disk space; files download when opened in Explorer.
        /// </summary>
        private async Task SetUpOnDemandAsync(ProtonAccount account, SyncPair pair)
        {
            var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            var status = new TextBlock { Text = "Setting up files-on-demand…", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(ring);
            header.Children.Add(status);

            var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
            panel.Children.Add(header);

            var dialog = new ContentDialog
            {
                Title = $"Files-on-demand — {FolderDisplayName(pair.LocalPath)}",
                Content = panel,
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };

            dialog.Opened += async (_, _) =>
            {
                try
                {
                    if (!App.Instance.CloudSync.IsSupported)
                    {
                        ring.IsActive = false;
                        status.Text = "Files-on-demand isn't available on this Windows version (needs Windows 10 1809+).";
                        return;
                    }

                    var count = await App.Instance.CloudSync.EnableAsync(account.Id, pair);
                    ring.IsActive = false;
                    status.Text = $"Ready — {count} remote item(s) available on-demand. Click \"Open\" to browse the folder in Explorer; files download automatically when you open them.";
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Setup failed: {ex.Message}\n\nIf this mentions a session or token, use Options ▸ Sign in again on the account, then retry.";
                }
            };

            await dialog.ShowAsync();
        }

        /// <summary>
        /// Pushes local changes in an on-demand folder up to Drive (new files, edits, deletes). Remote
        /// changes aren't pulled here — they appear as placeholders. Safe: hydration isn't seen as a change.
        /// </summary>
        private async Task SyncUpAsync(ProtonAccount account, SyncPair pair)
        {
            var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            var status = new TextBlock { Text = "Pushing local changes to Proton Drive…", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(ring);
            header.Children.Add(status);

            var results = new StackPanel { Spacing = 2 };

            var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
            panel.Children.Add(header);
            panel.Children.Add(results);

            var dialog = new ContentDialog
            {
                Title = $"Push changes — {FolderDisplayName(pair.LocalPath)}",
                Content = new ScrollViewer { Content = panel, MaxHeight = 440 },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };

            dialog.Opened += async (_, _) =>
            {
                SetPairStatus(pair.Id, PairState.Syncing, "Pushing local changes…");
                try
                {
                    // First use this session: register the sync root + placeholders + provider.
                    if (!App.Instance.CloudSync.IsEnabled(pair.Id))
                    {
                        status.Text = "Setting up files-on-demand…";
                        await App.Instance.CloudSync.EnableAsync(account.Id, pair);
                    }

                    status.Text = "Pushing local changes to Proton Drive…";
                    var result = await App.Instance.CloudSync.SyncChangesAsync(account.Id, pair);
                    ring.IsActive = false;

                    var when = DateTime.Now.ToString("t");
                    status.Text = result.Total == 0
                        ? "No local changes to push — already up to date."
                        : $"Pushed {result.Completed} change(s) up.{(result.Failures.Count > 0 ? $" {result.Failures.Count} failed." : string.Empty)}";

                    foreach (var failure in result.Failures)
                    {
                        results.Children.Add(new TextBlock { Text = $"⚠ {failure.Operation.RelativePath}: {failure.Error}", TextWrapping = TextWrapping.Wrap });
                    }

                    SetPairStatus(pair.Id,
                        result.Failures.Count > 0 ? PairState.Attention : PairState.UpToDate,
                        result.Failures.Count > 0
                            ? $"{result.Completed} pushed, {result.Failures.Count} failed — {result.Failures[0].Error} · {when}"
                            : result.Total == 0 ? $"Up to date · {when}" : $"Pushed {result.Completed} change(s) · {when}");
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Push failed: {ex.Message}\n\nIf this mentions a session or token, use Options ▸ Sign in again on the account, then retry.";
                    SetPairStatus(pair.Id, PairState.Attention, $"Push failed: {ex.Message}");
                }
            };

            await dialog.ShowAsync();
        }

        /// <summary>
        /// Full-sync: compute the sync plan (capture + reconcile against persisted state) and show it.
        /// Files move only if the user clicks the primary "Sync N items" button — Close cancels (so it
        /// doubles as a dry-run preview). After applying, the new last-known state is persisted.
        /// </summary>
        private async Task SyncNowAsync(ProtonAccount account, SyncPair pair)
        {
            var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            var status = new TextBlock { Text = "Comparing local and remote…", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(ring);
            header.Children.Add(status);

            var results = new StackPanel { Spacing = 2 };

            var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
            panel.Children.Add(header);
            panel.Children.Add(results);

            var dialog = new ContentDialog
            {
                Title = $"Sync — {FolderDisplayName(pair.LocalPath)}",
                Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };

            SyncPlan? plan = null;
            var applied = false;

            dialog.Opened += async (_, _) =>
            {
                SetPairStatus(pair.Id, PairState.Syncing, "Comparing local and remote…");
                try
                {
                    plan = await App.Instance.SyncEngine.PlanAsync(account.Id, pair);
                    ring.IsActive = false;

                    if (plan.Operations.Count == 0)
                    {
                        status.Text = "Already in sync — nothing to do.";
                        SetPairStatus(pair.Id, PairState.UpToDate, $"Up to date · {DateTime.Now:t}");
                        return;
                    }

                    status.Text = $"{plan.Operations.Count} planned operation(s). Review, then Sync — or Close to cancel.";
                    SetPairStatus(pair.Id, PairState.Auto, $"{plan.Operations.Count} change(s) waiting for confirmation…");
                    foreach (var op in plan.Operations)
                    {
                        results.Children.Add(new TextBlock
                        {
                            Text = $"{OperationGlyph(op.Kind)}  {op.RelativePath}{(op.IsFolder ? "/" : string.Empty)}   — {op.Reason}",
                            TextWrapping = TextWrapping.Wrap,
                        });
                    }

                    dialog.PrimaryButtonText = $"Sync {plan.Operations.Count} item(s)";
                    dialog.DefaultButton = ContentDialogButton.Primary;
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Could not plan: {ex.Message}\n\nIf this mentions a session or token, use Options ▸ Sign in again on the account, then retry.";
                    SetPairStatus(pair.Id, PairState.Attention, $"Sync failed: {ex.Message}");
                }
            };

            dialog.PrimaryButtonClick += async (_, e) =>
            {
                if (plan is null || plan.Operations.Count == 0)
                {
                    return;
                }

                // Keep the dialog open to show progress and the result.
                var deferral = e.GetDeferral();
                e.Cancel = true;
                dialog.IsPrimaryButtonEnabled = false;
                dialog.IsEnabled = true;
                results.Children.Clear();
                ring.IsActive = true;
                status.Text = "Applying…";
                SetPairStatus(pair.Id, PairState.Syncing, "Syncing…");

                try
                {
                    var progress = new Progress<SyncProgress>(p =>
                        status.Text = $"Applying… [{p.Completed + 1}/{p.Total}] {OperationGlyph(p.Current.Kind)} {p.Current.RelativePath}");

                    var result = await App.Instance.SyncEngine.ApplyAsync(account.Id, plan, progress);
                    applied = true;

                    ring.IsActive = false;
                    status.Text = $"Done — {result.Completed} applied, {result.Skipped} skipped (conflicts), {result.Failures.Count} failed.";

                    foreach (var failure in result.Failures)
                    {
                        results.Children.Add(new TextBlock { Text = $"⚠ {failure.Operation.RelativePath}: {failure.Error}", TextWrapping = TextWrapping.Wrap });
                    }

                    var when = DateTime.Now.ToString("t");
                    SetPairStatus(pair.Id,
                        result.Failures.Count > 0 ? PairState.Attention : PairState.UpToDate,
                        result.Failures.Count > 0
                            ? $"{result.Completed} synced, {result.Failures.Count} failed — {result.Failures[0].Error} · {when}"
                            : $"Synced {result.Completed} change(s) · {when}");

                    dialog.PrimaryButtonText = string.Empty; // sync finished — only Close remains
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Sync failed: {ex.Message}";
                    SetPairStatus(pair.Id, PairState.Attention, $"Sync failed: {ex.Message}");
                    dialog.IsPrimaryButtonEnabled = true;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();

            // Closed without applying — restore the resting status.
            if (!applied)
            {
                SetIdleStatus(pair);
            }
        }

        private static string OperationGlyph(SyncOperationKind kind) => kind switch
        {
            SyncOperationKind.DownloadFile => "⬇ download",
            SyncOperationKind.UploadFile => "⬆ upload",
            SyncOperationKind.CreateLocalFolder => "📁⬇ new local folder",
            SyncOperationKind.CreateRemoteFolder => "📁⬆ new remote folder",
            SyncOperationKind.DeleteLocal => "🗑 delete local",
            SyncOperationKind.DeleteRemote => "🗑 delete remote",
            SyncOperationKind.Conflict => "⚠ conflict",
            _ => kind.ToString(),
        };

        // ---- Account actions ----------------------------------------------------------------------------

        /// <summary>
        /// Re-runs the browser login for an existing account and refreshes its stored session (fresh
        /// tokens + key password). Recovers an expired/rotated session without touching its folders.
        /// </summary>
        private async Task ReSignInAsync(ProtonAccount account)
        {
            // The authenticator's challenge callback runs on a background thread, so all UI updates from
            // it (and from the continuation) must be marshalled back to the UI thread.
            var ui = DispatcherQueue;
            using var cts = new CancellationTokenSource();

            var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            var status = new TextBlock { Text = "Opening Proton sign-in…", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var busy = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            busy.Children.Add(ring);
            busy.Children.Add(status);

            var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
            panel.Children.Add(busy);

            var dialog = new ContentDialog
            {
                Title = $"Sign in again — {account.Label}",
                Content = panel,
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot,
            };

            // Closing the dialog (Cancel) aborts the sign-in poll.
            dialog.Closing += (_, _) => cts.Cancel();

            dialog.Opened += async (_, _) =>
            {
                try
                {
                    var result = await App.Instance.WebAuthenticator.SignInAsync(
                        challenge =>
                        {
                            ui.TryEnqueue(async () =>
                            {
                                try { await Windows.System.Launcher.LaunchUriAsync(new Uri(challenge.Url)); }
                                catch { /* user can complete the opened page manually */ }

                                status.Text = $"A Proton login page opened (code {challenge.UserCode}). Finish signing in there…";
                            });

                            return Task.CompletedTask;
                        },
                        cts.Token);

                    ui.TryEnqueue(() =>
                    {
                        ring.IsActive = false;

                        if (!result.IsSuccess)
                        {
                            status.Text = "Sign-in failed: " + (result.Message ?? "unknown error");
                            return;
                        }

                        App.Instance.CreateSetupWorkflow().RefreshAccountSession(account.Id, result.Session!);
                        status.Text = $"✓ Signed in as {result.Session!.Username}. Session refreshed.";
                        dialog.CloseButtonText = "Done";
                    });
                }
                catch (OperationCanceledException)
                {
                    // Dialog was cancelled.
                }
                catch (Exception ex)
                {
                    ui.TryEnqueue(() =>
                    {
                        ring.IsActive = false;
                        status.Text = "Error: " + ex.Message;
                    });
                }
            };

            await dialog.ShowAsync();
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
            modeBox.Items.Add("On-demand — files download when you open them");
            modeBox.Items.Add("Full sync — keep complete copies both ways");
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

            // For an On-demand folder, bring it online immediately (placeholders + hydration provider).
            if (pair.Mode == SyncMode.OnDemand)
            {
                await SetUpOnDemandAsync(account, pair);
            }
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
