using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            App.Instance.AccountSessionExpired += OnAccountSessionExpired;
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
            App.Instance.AccountSessionExpired -= OnAccountSessionExpired;
        }

        // A Proton session expired while this page is open — refresh immediately so the affected
        // account's card shows the "Sign-in required" banner without waiting for the user to navigate
        // away and back. Fires off the UI thread's dispatcher already (see App's ctor wiring).
        private void OnAccountSessionExpired(string accountId) => Refresh();

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

        private void OnSettingsClicked(object sender, RoutedEventArgs e)
            => App.Instance.Window?.NavigateToSettings();

        private Expander BuildAccountCard(ProtonAccount account)
        {
            // Reflects the account's persisted session directly (rather than a separate in-memory flag)
            // so it's correct on every Refresh regardless of WHY the session isn't resumable — a just-
            // detected refresh-token expiry (see App.AccountSessionExpired) clears the stored tokens the
            // same way, and a successful "Sign in again" clears this banner the moment the page refreshes.
            var needsReauth = App.Instance.SecretStore.LoadSecrets(account.Id)?.HasResumableSession != true;

            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center }); // person
            title.Children.Add(new TextBlock
            {
                Text = account.Label,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            });

            if (needsReauth)
            {
                var cautionBrush = ThemeBrush("SystemFillColorCautionBrush") ?? new SolidColorBrush(Colors.Orange);
                var warningIcon = new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = cautionBrush };
                var warningText = new TextBlock { Text = "Sign-in required", VerticalAlignment = VerticalAlignment.Center, Foreground = cautionBrush, FontSize = 12 };
                var warning = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                warning.Children.Add(warningIcon);
                warning.Children.Add(warningText);
                ToolTipService.SetToolTip(warning, "This Proton session has expired -- sign in again to keep syncing.");
                title.Children.Add(warning);
            }

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

            var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            if (needsReauth)
            {
                var signInNow = new Button { Content = "Sign in", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                signInNow.Click += async (_, _) => await ReSignInAsync(account);
                headerActions.Children.Add(signInNow);
            }

            headerActions.Children.Add(options);

            var header = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(title);
            Grid.SetColumn(headerActions, 1);
            header.Children.Add(headerActions);

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
                catch (Exception ex)
                {
                    // This handler is async void — an escaping exception would take the app down.
                    SetPairStatus(pair.Id, PairState.Attention, $"Pause/resume failed: {ex.Message}");
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

                var resolve = new MenuFlyoutItem { Text = "Resolve conflicts…", Icon = new FontIcon { Glyph = "" } }; // warning
                ToolTipService.SetToolTip(resolve, "Review files that changed both here and on Proton Drive, and choose which version to keep.");
                resolve.Click += async (_, _) => await ResolveConflictsDialogAsync(account, pair);
                flyout.Items.Add(resolve);

                var freeUp = new MenuFlyoutItem { Text = "Free up space now", Icon = new FontIcon { Glyph = "" } }; // cloud
                ToolTipService.SetToolTip(freeUp, "Make every synced file in this folder online-only again. Pinned files and unsynced changes are left alone.");
                freeUp.Click += async (_, _) => await FreeUpSpaceAsync(pair);
                flyout.Items.Add(freeUp);
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

        /// <summary>
        /// Dehydrates all synced files in an on-demand folder back to cloud-only placeholders. Pinned
        /// files, unpushed local edits, and already-online-only files are skipped — nothing is lost.
        /// </summary>
        private async Task FreeUpSpaceAsync(SyncPair pair)
        {
            var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            var status = new TextBlock { Text = "Freeing up space…", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(ring);
            header.Children.Add(status);

            var dialog = new ContentDialog
            {
                Title = $"Free up space — {FolderDisplayName(pair.LocalPath)}",
                Content = new StackPanel { Spacing = 10, MinWidth = 420, Children = { header } },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };

            dialog.Opened += async (_, _) =>
            {
                try
                {
                    var result = await Task.Run(() => App.Instance.CloudSync.FreeUpSpace(pair));
                    ring.IsActive = false;
                    status.Text = result.Dehydrated == 0
                        ? $"Nothing to free — {result.Skipped} file(s) are already online-only, pinned, recently changed, or not yet synced."
                        : $"Freed {result.Dehydrated} file(s) back to online-only. {result.Skipped} left as-is (pinned, unsynced, or already online-only).";
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Free up space failed: {ex.Message}";
                }
            };

            await dialog.ShowAsync();
        }

        // ---- Conflict resolution ----------------------------------------------------------------------

        /// <summary>
        /// One row of the conflict UI: the path, why it conflicts, and a ComboBox with the applicable
        /// choices ("Decide later" keeps it skipped). The chosen resolution is registered in
        /// <paramref name="selections"/>; a file-vs-folder mismatch gets a disabled box (no automatic
        /// resolution — the user must rename one side).
        /// </summary>
        private static FrameworkElement BuildConflictRow(SyncOperation conflict, Dictionary<string, ComboBox> selections)
        {
            var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };

            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            title.Children.Add(new FontIcon { Glyph = "", FontSize = 13, VerticalAlignment = VerticalAlignment.Center }); // warning
            title.Children.Add(new TextBlock
            {
                Text = conflict.RelativePath,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(title);

            if (conflict.Reason is { } reason)
            {
                panel.Children.Add(new TextBlock { Text = reason, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            }

            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0) };
            combo.Items.Add(new ComboBoxItem { Content = "Decide later", Tag = null });

            var mismatch = conflict is { Remote: not null, Local: not null }
                && conflict.Remote.IsFolder != conflict.Local.IsFolder;
            if (mismatch)
            {
                combo.IsEnabled = false;
                panel.Children.Add(new TextBlock
                {
                    Text = "A file on one side and a folder on the other can't be merged automatically — rename one of them, then sync again.",
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = conflict.Local is null ? "Keep it deleted (remove from Proton Drive too)" : "Keep this PC's version",
                    Tag = ConflictResolution.KeepLocal,
                });
                combo.Items.Add(new ComboBoxItem
                {
                    Content = conflict.Remote is null ? "Accept the deletion (remove from this PC)" : "Keep Proton Drive's version",
                    Tag = ConflictResolution.KeepRemote,
                });
                if (conflict is { Remote: not null, Local: not null })
                {
                    combo.Items.Add(new ComboBoxItem { Content = "Keep both (rename this PC's copy)", Tag = ConflictResolution.KeepBoth });
                }
            }

            combo.SelectedIndex = 0;
            selections[conflict.RelativePath] = combo;
            panel.Children.Add(combo);
            return panel;
        }

        /// <summary>The non-"decide later" choices the user made across the built conflict rows.</summary>
        private static Dictionary<string, ConflictResolution> CollectResolutions(Dictionary<string, ComboBox> selections)
        {
            var resolutions = new Dictionary<string, ConflictResolution>(StringComparer.Ordinal);
            foreach (var (path, combo) in selections)
            {
                if ((combo.SelectedItem as ComboBoxItem)?.Tag is ConflictResolution resolution)
                {
                    resolutions[path] = resolution;
                }
            }

            return resolutions;
        }

        /// <summary>
        /// Lists an on-demand folder's conflicts (files changed both here and on Drive, or a deletion
        /// racing an edit) and applies the user's per-file decisions. "Keep Drive's version" restores an
        /// on-demand placeholder (no download); "keep both" renames the local copy so nothing is lost.
        /// </summary>
        private async Task ResolveConflictsDialogAsync(ProtonAccount account, SyncPair pair)
        {
            var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            var status = new TextBlock { Text = "Checking for conflicts…", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(ring);
            header.Children.Add(status);

            var rows = new StackPanel { Spacing = 6 };
            var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
            panel.Children.Add(header);
            panel.Children.Add(rows);

            var dialog = new ContentDialog
            {
                Title = $"Resolve conflicts — {FolderDisplayName(pair.LocalPath)}",
                Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };

            var selections = new Dictionary<string, ComboBox>(StringComparer.Ordinal);

            dialog.Opened += async (_, _) =>
            {
                try
                {
                    // First use this session: the sync root + provider must be up before we can touch
                    // placeholders (and before resolution can recreate them).
                    if (!App.Instance.CloudSync.IsEnabled(pair.Id))
                    {
                        status.Text = "Setting up files-on-demand…";
                        await App.Instance.CloudSync.EnableAsync(account.Id, pair);
                        status.Text = "Checking for conflicts…";
                    }

                    var conflicts = await App.Instance.CloudSync.GetConflictsAsync(account.Id, pair);
                    ring.IsActive = false;

                    if (conflicts.Count == 0)
                    {
                        status.Text = "No conflicts — everything can sync cleanly.";
                        return;
                    }

                    status.Text = $"{conflicts.Count} file(s) changed both here and on Proton Drive. Choose what to keep for each, then Apply.";
                    foreach (var conflict in conflicts)
                    {
                        rows.Children.Add(BuildConflictRow(conflict, selections));
                    }

                    dialog.PrimaryButtonText = "Apply";
                    dialog.DefaultButton = ContentDialogButton.Primary;
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Could not check for conflicts: {ex.Message}\n\nIf this mentions a session or token, use Options ▸ Sign in again on the account, then retry.";
                }
            };

            dialog.PrimaryButtonClick += async (_, e) =>
            {
                var resolutions = CollectResolutions(selections);
                if (resolutions.Count == 0)
                {
                    return; // nothing chosen — just close
                }

                var deferral = e.GetDeferral();
                e.Cancel = true;
                dialog.IsPrimaryButtonEnabled = false;
                ring.IsActive = true;
                status.Text = $"Applying {resolutions.Count} decision(s)…";
                SetPairStatus(pair.Id, PairState.Syncing, "Resolving conflicts…");

                try
                {
                    var result = await App.Instance.CloudSync.ResolveConflictsAsync(account.Id, pair, resolutions);
                    ring.IsActive = false;
                    rows.Children.Clear();

                    status.Text = result.Errors.Count == 0
                        ? $"Resolved {result.Resolved} conflict(s)."
                        : $"Resolved {result.Resolved} conflict(s); {result.Errors.Count} could not be applied.";
                    foreach (var error in result.Errors)
                    {
                        rows.Children.Add(new TextBlock { Text = $"⚠ {error}", TextWrapping = TextWrapping.Wrap });
                    }

                    var when = DateTime.Now.ToString("t");
                    SetPairStatus(pair.Id,
                        result.Errors.Count > 0 ? PairState.Attention : PairState.UpToDate,
                        result.Errors.Count > 0
                            ? $"{result.Resolved} conflict(s) resolved, {result.Errors.Count} failed · {when}"
                            : $"{result.Resolved} conflict(s) resolved · {when}");

                    dialog.PrimaryButtonText = string.Empty; // done — only Close remains
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Applying failed: {ex.Message}";
                    SetPairStatus(pair.Id, PairState.Attention, $"Conflict resolution failed: {ex.Message}");
                    dialog.IsPrimaryButtonEnabled = true;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
        }

        private enum StopSyncChoice
        {
            Cancel,
            KeepFiles,
            RemoveFiles,
        }

        /// <summary>
        /// Asks what should happen to the local files when syncing stops. Either way the local folder is
        /// returned to an ordinary folder: online-only placeholders are removed (their content lives on
        /// Proton Drive), and nothing on Proton Drive is touched.
        /// </summary>
        private async Task<StopSyncChoice> AskKeepOrRemoveFilesAsync(string title, string intro)
        {
            var content = new StackPanel { Spacing = 10, MaxWidth = 460 };
            content.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock
            {
                Text = "What should happen to the files in the local folder? Everything stays on Proton Drive either way.",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = "• Keep files — files already on this PC stay as normal files. Online-only files "
                       + "(the ones that download when opened) are removed from the folder, since their "
                       + "content only lives on Proton Drive.\n"
                       + "• Remove files — everything inside the local folder is deleted from this PC.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });

            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = "Keep files",
                SecondaryButtonText = "Remove files",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            return await dialog.ShowAsync() switch
            {
                ContentDialogResult.Primary => StopSyncChoice.KeepFiles,
                ContentDialogResult.Secondary => StopSyncChoice.RemoveFiles,
                _ => StopSyncChoice.Cancel,
            };
        }

        /// <summary>
        /// Runs the actual teardown for one or more pairs with a progress dialog: stops their watchers,
        /// decommissions each local folder back to an ordinary folder (keeping or removing files as
        /// chosen), then applies <paramref name="onCleanupDone"/> (the settings change — remove the pair
        /// or the whole account) and reports what happened.
        /// </summary>
        private async Task RunStopSyncCleanupAsync(
            string title, IReadOnlyList<SyncPair> pairs, bool keepFiles, Action onCleanupDone)
        {
            var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            var status = new TextBlock { Text = "Cleaning up…", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(ring);
            header.Children.Add(status);

            var details = new StackPanel { Spacing = 2 };
            var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
            panel.Children.Add(header);
            panel.Children.Add(details);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = new ScrollViewer { Content = panel, MaxHeight = 440 },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };

            dialog.Opened += async (_, _) =>
            {
                var reverted = 0;
                var deleted = 0;
                var kept = 0;
                var errors = new List<string>();

                foreach (var pair in pairs)
                {
                    status.Text = $"Cleaning up \"{FolderDisplayName(pair.LocalPath)}\"…";
                    try
                    {
                        App.Instance.FullSync.StopAutoSync(pair.Id); // full-sync watcher/poll, if any
                        var result = await App.Instance.CloudSync.DecommissionAsync(pair, keepFiles);
                        reverted += result.Reverted;
                        deleted += result.Deleted;
                        kept += result.Kept;
                        errors.AddRange(result.Errors);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{FolderDisplayName(pair.LocalPath)}: {ex.Message}");
                    }
                }

                // The cleanup is done (best-effort) — apply the settings change regardless, so a stray
                // locked file can't leave the account/folder half-removed in the app.
                try
                {
                    onCleanupDone();
                }
                catch (Exception ex)
                {
                    errors.Add($"Updating settings failed: {ex.Message}");
                }

                ring.IsActive = false;
                status.Text = keepFiles
                    ? $"Done. {reverted + kept} file(s)/folder(s) kept on this PC as normal files; "
                      + $"{deleted} online-only item(s) removed locally (still on Proton Drive)."
                    : $"Done. {deleted} item(s) removed from this PC. Everything is still on Proton Drive.";

                foreach (var error in errors)
                {
                    details.Children.Add(new TextBlock { Text = $"⚠ {error}", TextWrapping = TextWrapping.Wrap });
                }
            };

            await dialog.ShowAsync();
        }

        private async Task RemoveFolderAsync(ProtonAccount account, SyncPair pair)
        {
            var choice = await AskKeepOrRemoveFilesAsync(
                "Stop syncing this folder?",
                $"PAWS will stop syncing \"{FolderDisplayName(pair.LocalPath)}\" and turn it back into a regular folder.");

            if (choice == StopSyncChoice.Cancel)
            {
                return;
            }

            await RunStopSyncCleanupAsync(
                $"Stop syncing — {FolderDisplayName(pair.LocalPath)}",
                [pair],
                keepFiles: choice == StopSyncChoice.KeepFiles,
                onCleanupDone: () => App.Instance.CreateSetupWorkflow().RemoveSyncPair(account.Id, pair.Id));

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
                // Cloud-only mode is commented out/TODO (see SyncMode.cs) — this only catches a
                // corrupted/future Mode value, not a real selectable option.
                _ => "Unknown mode",
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

                if (result.Skipped > 0)
                {
                    SetPairStatus(e.PairId, PairState.Attention,
                        $"{(result.Completed > 0 ? $"Synced {result.Completed} change(s); " : string.Empty)}{result.Skipped} conflict(s) need a decision — Manual ▸ Sync now · {when}");
                    return;
                }

                SetPairStatus(e.PairId, PairState.UpToDate, result.Total == 0
                    ? $"Up to date · {when}"
                    : $"Synced {result.Completed} change(s) · {when}");
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

                if (result.Skipped > 0)
                {
                    // Conflicts ride along in the push plan (never auto-resolved) — needs the user.
                    SetPairStatus(e.PairId, PairState.Attention,
                        $"{(result.Completed > 0 ? $"Pushed {result.Completed} change(s); " : string.Empty)}{result.Skipped} conflict(s) need a decision — Manual ▸ Resolve conflicts · {when}");
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
                        : $"Pushed {result.Completed} change(s) up.{(result.Failures.Count > 0 ? $" {result.Failures.Count} failed." : string.Empty)}"
                          + (result.Skipped > 0 ? $" {result.Skipped} conflict(s) skipped — use Manual ▸ Resolve conflicts to settle them." : string.Empty);

                    foreach (var failure in result.Failures)
                    {
                        results.Children.Add(new TextBlock { Text = $"⚠ {failure.Operation.RelativePath}: {failure.Error}", TextWrapping = TextWrapping.Wrap });
                    }

                    SetPairStatus(pair.Id,
                        result.Failures.Count > 0 || result.Skipped > 0 ? PairState.Attention : PairState.UpToDate,
                        result.Failures.Count > 0
                            ? $"{result.Completed} pushed, {result.Failures.Count} failed — {result.Failures[0].Error} · {when}"
                            : result.Skipped > 0
                                ? $"{result.Skipped} conflict(s) need a decision — Manual ▸ Resolve conflicts · {when}"
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
            var conflictSelections = new Dictionary<string, ComboBox>(StringComparer.Ordinal);

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

                    var conflictCount = plan.Operations.Count(o => o.Kind == SyncOperationKind.Conflict);
                    status.Text = $"{plan.Operations.Count} planned operation(s)."
                        + (conflictCount > 0 ? $" {conflictCount} conflict(s) — pick what to keep for each (\"decide later\" leaves both sides alone)." : string.Empty)
                        + " Review, then Sync — or Close to cancel.";
                    SetPairStatus(pair.Id, PairState.Auto, $"{plan.Operations.Count} change(s) waiting for confirmation…");
                    foreach (var op in plan.Operations)
                    {
                        if (op.Kind == SyncOperationKind.Conflict)
                        {
                            results.Children.Add(BuildConflictRow(op, conflictSelections));
                            continue;
                        }

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

                // Collect the conflict decisions before the rows (and their ComboBoxes) are cleared.
                var resolutions = CollectResolutions(conflictSelections);

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

                    var result = await App.Instance.SyncEngine.ApplyAsync(account.Id, plan, progress, resolutions);
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
                        App.Instance.ClearSessionExpired(account.Id); // re-arms notification for a LATER expiry
                        status.Text = $"✓ Signed in as {result.Session!.Username}. Session refreshed.";
                        dialog.CloseButtonText = "Done";
                        Refresh(); // clears the "Sign-in required" banner immediately if it was showing
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
            var choice = await AskKeepOrRemoveFilesAsync(
                "Remove account?",
                $"PAWS will remove {account.Label} and its stored credentials from this PC, and stop syncing "
                + $"its {account.SyncPairs.Count} folder(s) — each goes back to being a regular folder.");

            if (choice == StopSyncChoice.Cancel)
            {
                return;
            }

            await RunStopSyncCleanupAsync(
                $"Remove account — {account.Label}",
                [.. account.SyncPairs],
                keepFiles: choice == StopSyncChoice.KeepFiles,
                onCleanupDone: () => App.Instance.CreateSetupWorkflow().RemoveAccount(account.Id));

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
            // TODO: Cloud-only mode is commented out — not implemented (see SyncMode.cs).
            // modeBox.Items.Add("Cloud-only");

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
