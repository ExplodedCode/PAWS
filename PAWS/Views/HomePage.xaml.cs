using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAWS.Core.Configuration;
using PAWS.Core.Sync;
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

            var reSignIn = new Button { Content = "Sign in again" };
            reSignIn.Click += async (_, _) => await ReSignInAsync(account);

            var removeAccount = new Button { Content = "Remove account", Margin = new Thickness(8, 0, 0, 0) };
            removeAccount.Click += async (_, _) => await RemoveAccountAsync(account);

            var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headerButtons.Children.Add(reSignIn);
            headerButtons.Children.Add(removeAccount);

            var header = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(title);
            Grid.SetColumn(headerButtons, 1);
            header.Children.Add(headerButtons);

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

            row.Children.Add(new TextBlock
            {
                Text = $"{pair.LocalPath}    ⇄    {pair.RemotePath}     [{pair.Mode}]",
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });

            var browse = new Button { Content = "Browse" };
            browse.Click += async (_, _) => await BrowseRemoteAsync(account, pair);

            var snapshot = new Button { Content = "Snapshot", Margin = new Thickness(8, 0, 0, 0) };
            snapshot.Click += async (_, _) => await ShowSnapshotAsync(account, pair);

            var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
            var action = new Button { Margin = new Thickness(8, 0, 0, 0), Style = accent };
            if (pair.Mode == SyncMode.OnDemand)
            {
                // Always available — the handler sets up on-demand on first use, then pushes local changes.
                action.Content = "Sync up";
                action.Click += async (_, _) => await SyncUpAsync(account, pair);
            }
            else
            {
                action.Content = "Sync now";
                action.Click += async (_, _) => await SyncNowAsync(account, pair);
            }

            var open = new Button { Content = "Open", Margin = new Thickness(8, 0, 0, 0) };
            open.Click += (_, _) =>
            {
                if (Directory.Exists(pair.LocalPath))
                {
                    Process.Start(new ProcessStartInfo { FileName = pair.LocalPath, UseShellExecute = true });
                }
            };

            var remove = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += (_, _) =>
            {
                App.Instance.CloudSync.Disable(pair.Id); // disconnect the on-demand provider, if any
                App.Instance.CreateSetupWorkflow().RemoveSyncPair(account.Id, pair.Id);
                Refresh();
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(browse);
            buttons.Children.Add(snapshot);
            buttons.Children.Add(action);
            buttons.Children.Add(open);
            buttons.Children.Add(remove);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);

            return row;
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
                Title = $"Files-on-demand: {pair.LocalPath}  ⇄  {pair.RemotePath}",
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
                    status.Text = $"Setup failed: {ex.Message}\n\nIf this mentions a session or token, use \"Sign in again\" on the account, then retry.";
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
                Title = $"Sync up: {pair.LocalPath}",
                Content = new ScrollViewer { Content = panel, MaxHeight = 440 },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };

            dialog.Opened += async (_, _) =>
            {
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

                    status.Text = result.Total == 0
                        ? "No local changes to push — already up to date."
                        : $"Pushed {result.Completed} change(s) up.{(result.Failures.Count > 0 ? $" {result.Failures.Count} failed." : string.Empty)}";

                    foreach (var failure in result.Failures)
                    {
                        results.Children.Add(new TextBlock { Text = $"⚠ {failure.Operation.RelativePath}: {failure.Error}", TextWrapping = TextWrapping.Wrap });
                    }
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Sync up failed: {ex.Message}\n\nIf this mentions a session or token, use \"Sign in again\" on the account, then retry.";
                }
            };

            await dialog.ShowAsync();
        }

        /// <summary>
        /// Phase 4b in the UI: compute the sync plan (capture + reconcile against persisted state) and
        /// show it. Files move only if the user clicks the primary "Sync N items" button — Close cancels
        /// (so it doubles as a dry-run preview). After applying, the new last-known state is persisted.
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
                Title = $"Sync: {pair.LocalPath}  ⇄  {pair.RemotePath}",
                Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };

            SyncPlan? plan = null;

            dialog.Opened += async (_, _) =>
            {
                try
                {
                    plan = await App.Instance.SyncEngine.PlanAsync(account.Id, pair);
                    ring.IsActive = false;

                    if (plan.Operations.Count == 0)
                    {
                        status.Text = "Already in sync — nothing to do.";
                        return;
                    }

                    status.Text = $"{plan.Operations.Count} planned operation(s). Review, then Sync — or Close to cancel.";
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
                    status.Text = $"Could not plan: {ex.Message}\n\nIf this mentions a session or token, use \"Sign in again\" on the account, then retry.";
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

                try
                {
                    var progress = new Progress<SyncProgress>(p =>
                        status.Text = $"Applying… [{p.Completed + 1}/{p.Total}] {OperationGlyph(p.Current.Kind)} {p.Current.RelativePath}");

                    var result = await App.Instance.SyncEngine.ApplyAsync(account.Id, plan, progress);

                    ring.IsActive = false;
                    status.Text = $"Done — {result.Completed} applied, {result.Skipped} skipped (conflicts), {result.Failures.Count} failed.";

                    foreach (var failure in result.Failures)
                    {
                        results.Children.Add(new TextBlock { Text = $"⚠ {failure.Operation.RelativePath}: {failure.Error}", TextWrapping = TextWrapping.Wrap });
                    }

                    dialog.PrimaryButtonText = string.Empty; // sync finished — only Close remains
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Sync failed: {ex.Message}";
                    dialog.IsPrimaryButtonEnabled = true;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
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

        /// <summary>
        /// Phase 2 in the UI: capture the full recursive remote snapshot for a folder and show it as an
        /// indented tree with counts/sizes. Exercises <see cref="RemoteSnapshotBuilder"/> and the
        /// active-only listing filter via the same connected client as Browse.
        /// </summary>
        private async Task ShowSnapshotAsync(ProtonAccount account, SyncPair pair)
        {
            var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            var status = new TextBlock { Text = "Capturing remote snapshot…", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(ring);
            header.Children.Add(status);

            var results = new StackPanel { Spacing = 2 };

            var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
            panel.Children.Add(header);
            panel.Children.Add(results);

            var dialog = new ContentDialog
            {
                Title = $"Snapshot: {pair.RemotePath}",
                Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };

            dialog.Opened += async (_, _) =>
            {
                try
                {
                    await using var client = await App.Instance.DriveClientFactory.CreateAsync(account.Id);

                    var snapshot = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath);
                    if (snapshot is null)
                    {
                        ring.IsActive = false;
                        status.Text = $"\"{pair.RemotePath}\" is not a folder on Drive.";
                        return;
                    }

                    foreach (var entry in snapshot.Entries)
                    {
                        var depth = entry.RelativePath.AsSpan().Count('/');
                        var indent = new string(' ', depth * 4);
                        var size = entry.IsFile && entry.Size is { } s ? $"   ({FormatSize(s)})" : string.Empty;
                        results.Children.Add(new TextBlock { Text = $"{indent}{(entry.IsFolder ? "📁" : "📄")} {entry.Name}{size}" });
                    }

                    ring.IsActive = false;
                    status.Text = snapshot.Entries.Count == 0
                        ? "This folder tree is empty."
                        : $"{snapshot.FolderCount} folder(s), {snapshot.FileCount} file(s), {FormatSize(snapshot.TotalFileBytes)} total.";
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Snapshot failed: {ex.Message}\n\nIf this mentions a session or token, use \"Sign in again\" on the account, then retry.";
                }
            };

            await dialog.ShowAsync();
        }

        /// <summary>
        /// Live proof of the app↔Drive wiring: resume the account's stored session, resolve the pair's
        /// remote path, and list its contents in a dialog. Runs entirely off the persisted browser login.
        /// </summary>
        private async Task BrowseRemoteAsync(ProtonAccount account, SyncPair pair)
        {
            var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
            var status = new TextBlock { Text = "Connecting to Proton Drive…", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(ring);
            header.Children.Add(status);

            var results = new StackPanel { Spacing = 2 };

            var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
            panel.Children.Add(header);
            panel.Children.Add(results);

            var dialog = new ContentDialog
            {
                Title = $"Remote folder: {pair.RemotePath}",
                Content = new ScrollViewer { Content = panel, MaxHeight = 440 },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };

            dialog.Opened += async (_, _) =>
            {
                try
                {
                    await using var client = await App.Instance.DriveClientFactory.CreateAsync(account.Id);

                    var folder = await client.ResolvePathAsync(pair.RemotePath);
                    if (folder is null)
                    {
                        ring.IsActive = false;
                        status.Text = $"Folder not found on Drive: {pair.RemotePath}";
                        return;
                    }

                    var count = 0;
                    await foreach (var child in client.ListChildrenAsync(folder))
                    {
                        var size = child.Size is { } s ? $"   ({FormatSize(s)})" : string.Empty;
                        results.Children.Add(new TextBlock { Text = $"{(child.IsFolder ? "📁" : "📄")}  {child.Name}{size}" });
                        count++;
                    }

                    ring.IsActive = false;
                    status.Text = count == 0 ? "This folder is empty." : $"{count} item(s).";
                }
                catch (Exception ex)
                {
                    ring.IsActive = false;
                    status.Text = $"Could not list folder: {ex.Message}\n\nIf this mentions a session or token, use \"Sign in again\" on the account, then retry.";
                }
            };

            await dialog.ShowAsync();
        }

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
                        status.Text = $"✓ Signed in as {result.Session!.Username}. Session refreshed — you can Browse remote again.";
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

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
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
