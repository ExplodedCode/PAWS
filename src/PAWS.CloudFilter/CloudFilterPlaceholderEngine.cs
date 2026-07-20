using System.Runtime.InteropServices;
using System.Threading;
using PAWS.Core.Diagnostics;
using PAWS.Core.Drive;
using PAWS.Core.Sync;
using Vanara.Extensions;
using Vanara.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace PAWS.CloudFilter;

/// <summary>
/// <see cref="IPlaceholderEngine"/> over the Win32 Cloud Filter API (cldapi.dll, via Vanara). Registers
/// a local folder as a cloud sync root and serves it on demand: directory enumeration populates a
/// folder's placeholders lazily (FETCH_PLACEHOLDERS, answered from the remote snapshot), and opening a
/// file hydrates it (FETCH_DATA, answered by downloading from Proton Drive). Each placeholder stores a
/// file-identity blob (the remote node's revision/UID).
/// </summary>
public sealed class CloudFilterPlaceholderEngine : IPlaceholderEngine
{
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public void RegisterSyncRoot(SyncRootInfo info)
    {
        Directory.CreateDirectory(info.LocalPath);

        // Prefer SHELL registration (StorageProviderSyncRootManager): it performs the same Cloud Filter
        // registration AND integrates with Explorer — sync status icons plus the built-in "Always keep on
        // this device" / "Free up space" context menu (Explorer pins/dehydrates directly; hydration flows
        // through our connected provider). Works unpackaged (no MSIX identity required — cfapiSync ships
        // this exact pattern as a plain desktop app). A path previously registered by the old Win32-only
        // call makes Register throw, so unregister and retry once to migrate existing pairs; if the shell
        // path still fails, fall back to the Win32 registration — everything works except the Explorer
        // menu/icons.
        if (TryRegisterWithShell(info))
        {
            return;
        }

        CfUnregisterSyncRoot(info.LocalPath); // best-effort migration off a previous Win32-only registration
        if (TryRegisterWithShell(info))
        {
            return;
        }

        RegisterSyncRootWin32(info);
    }

    private static bool TryRegisterWithShell(SyncRootInfo info)
    {
        try
        {
            var folder = Windows.Storage.StorageFolder.GetFolderFromPathAsync(info.LocalPath).AsTask().GetAwaiter().GetResult();

            var registration = new Windows.Storage.Provider.StorageProviderSyncRootInfo
            {
                Id = ShellSyncRootId(info),
                Path = folder,
                DisplayNameResource = $"{info.ProviderName} - {FolderLeaf(info.LocalPath)}",
                IconResource = @"%SystemRoot%\system32\imageres.dll,-1043",
                Version = info.Version,
                ProviderId = Guid.Parse(info.ProviderId),
                HydrationPolicy = Windows.Storage.Provider.StorageProviderHydrationPolicy.Progressive,
                HydrationPolicyModifier = Windows.Storage.Provider.StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
                // "Full" is the shell's name for on-demand population (folders fill via FETCH_PLACEHOLDERS).
                PopulationPolicy = Windows.Storage.Provider.StorageProviderPopulationPolicy.Full,
                InSyncPolicy = Windows.Storage.Provider.StorageProviderInSyncPolicy.FileLastWriteTime,
                HardlinkPolicy = Windows.Storage.Provider.StorageProviderHardlinkPolicy.None,
                ShowSiblingsAsGroup = false,
                // THE switch for Explorer's "Always keep on this device" / "Free up space" context menu —
                // it defaults to false, and without it the shell offers no pin UX at all.
                AllowPinning = true,
                ProtectionMode = Windows.Storage.Provider.StorageProviderProtectionMode.Unknown,
            };
            registration.Context = Windows.Security.Cryptography.CryptographicBuffer.ConvertStringToBinary(
                registration.Id, Windows.Security.Cryptography.BinaryStringEncoding.Utf8);

            Windows.Storage.Provider.StorageProviderSyncRootManager.Register(registration);
            RemovePackagedAppAumid(registration.Id, info.LocalPath);

            // Register over an EXISTING id succeeds but keeps the old capabilities (observed live: a root
            // registered before AllowPinning was set stayed Flags-stale after re-register, so Explorer
            // never showed the pin menu). Verify what the shell actually recorded and, if the pin
            // capability is missing, unregister and register fresh.
            var recorded = GetSyncRootInfoWithRetry(folder, info.LocalPath);
            if (recorded is null)
            {
                // GetSyncRootInformationForFolder can throw ERROR_NOT_FOUND transiently for a folder that
                // WAS just successfully registered (same quirk documented on the unregister path below) —
                // confirmed live 2026-07-17. Register() itself didn't throw, so trust that the
                // registration succeeded rather than falling through to CfUnregisterSyncRoot + a full
                // re-attempt, which can cascade all the way to the Win32-only fallback (no Explorer
                // context menu at all) just because THIS lookup is flaky, not because registration
                // actually failed. If AllowPinning silently didn't stick, Settings ▸ "Repair Explorer
                // context menu" re-runs this whole path on demand.
                PawsLog.Write($"Shell sync-root registered for '{info.LocalPath}' but AllowPinning could not be verified (lookup kept failing) — assuming success rather than falling back to the Win32-only path.");
                return true;
            }

            if (!recorded.AllowPinning)
            {
                Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister(recorded.Id);
                Windows.Storage.Provider.StorageProviderSyncRootManager.Register(registration);
                RemovePackagedAppAumid(registration.Id, info.LocalPath);
                recorded = GetSyncRootInfoWithRetry(folder, info.LocalPath) ?? recorded;
            }

            if (!recorded.AllowPinning)
            {
                PawsLog.Write($"Shell sync-root for '{info.LocalPath}' is registered but AllowPinning is still false after a re-register attempt — Explorer's 'Always keep on this device'/'Free up space' items may be missing. Try Settings ▸ Repair Explorer context menu.");
            }

            return recorded.AllowPinning;
        }
        catch
        {
            return false;
        }
    }

    // When a PACKAGED app calls Register, the shell records the caller's AUMID on the sync-root key and
    // then SUPPRESSES its own built-in "Always keep on this device"/"Free up space" context-menu verbs
    // for that root, deferring to cloud-files handlers the package is presumed to declare (PAWS declares
    // none). Root-caused empirically 2026-07-19: an UNPACKAGED registration with byte-for-byte identical
    // policies got the verbs; deleting the AUMID value from the packaged registration made them appear
    // instantly — with AllowPinning (registry Flags bit 0x20) correctly set the whole time, so this, not
    // the pin capability, was why the menu never showed. The SyncRootManager tree is user-writable (the
    // unelevated Register call itself writes it), so stripping the value needs no elevation. Best-effort:
    // without it the root still syncs/hydrates fine — only the built-in menu items stay hidden.
    private static void RemovePackagedAppAumid(string syncRootId, string localPath)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\{syncRootId}", writable: true);
            key?.DeleteValue("AUMID", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            PawsLog.Write($"Could not remove the packaged-app AUMID from sync root '{syncRootId}' ({localPath}): {ex.GetType().Name}: {ex.Message} — Explorer's 'Always keep on this device'/'Free up space' items may stay hidden. Try Settings ▸ Repair Explorer context menu.");
        }
    }

    // GetSyncRootInformationForFolder can throw ERROR_NOT_FOUND (0x80070490) for a folder that IS
    // genuinely shell-registered — a transient lookup quirk, not evidence the registration itself failed
    // (see the unregister-path remarks for the first place this was found and confirmed live). A few
    // short retries ride out the race; null means it never resolved.
    private static Windows.Storage.Provider.StorageProviderSyncRootInfo? GetSyncRootInfoWithRetry(
        Windows.Storage.StorageFolder folder, string localPath, int attempts = 3)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder);
            }
            catch (Exception ex) when (attempt < attempts)
            {
                PawsLog.Write($"GetSyncRootInformationForFolder attempt {attempt}/{attempts} failed for '{localPath}': {ex.GetType().Name}: {ex.Message} — retrying.");
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                PawsLog.Write($"GetSyncRootInformationForFolder failed for '{localPath}' after {attempts} attempts: {ex.GetType().Name}: {ex.Message}.");
                return null;
            }
        }

        return null;
    }

    // The shell requires the id format "{providerId}!{userSid}!{accountSegment}"; the last segment is a
    // stable per-folder tag so each pair registers as its own root.
    private static string ShellSyncRootId(SyncRootInfo info) => $"{info.ProviderId}!{ComputeSidAndPathHashSuffix(info.LocalPath)}";

    private static string FolderLeaf(string localPath)
    {
        var name = Path.GetFileName(localPath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? localPath : name;
    }

    // The original Win32-only registration (no Explorer shell integration) — kept as the fallback.
    private static void RegisterSyncRootWin32(SyncRootInfo info)
    {
        var registration = new CF_SYNC_REGISTRATION
        {
            ProviderId = Guid.Parse(info.ProviderId),
            ProviderName = info.ProviderName,
            ProviderVersion = info.Version,
        };
        registration.StructSize = (uint)Marshal.SizeOf(registration);

        var policies = new CF_SYNC_POLICIES
        {
            Hydration = new CF_HYDRATION_POLICY
            {
                Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_PROGRESSIVE,
                Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED,
            },
            Population = new CF_POPULATION_POLICY
            {
                // Lazy: folders are populated on first enumeration via FETCH_PLACEHOLDERS.
                Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_PARTIAL,
                Modifier = CF_POPULATION_POLICY_MODIFIER.CF_POPULATION_POLICY_MODIFIER_NONE,
            },
            InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_ALL,
            HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
        };
        policies.StructSize = (uint)Marshal.SizeOf(policies);

        CfRegisterSyncRoot(info.LocalPath, registration, policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE).ThrowIfFailed();
    }

    public void UnregisterSyncRoot(string localPath)
    {
        // Shell-registered roots must be removed through the shell (clears the Explorer integration);
        // Win32-registered roots need CfUnregisterSyncRoot. Try shell first, then the Win32 call — if the
        // shell already unregistered it, the Win32 failure is expected and ignored.
        var shellUnregistered = TryUnregisterShell(localPath);
        var hr = CfUnregisterSyncRoot(localPath);
        if (!shellUnregistered)
        {
            hr.ThrowIfFailed();
        }
    }

    private static bool TryUnregisterShell(string localPath)
    {
        try
        {
            var folder = Windows.Storage.StorageFolder.GetFolderFromPathAsync(localPath).AsTask().GetAwaiter().GetResult();
            var current = Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder);
            Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister(current.Id);
            return true;
        }
        catch (Exception ex)
        {
            // GetSyncRootInformationForFolder can throw ERROR_NOT_FOUND (0x80070490) for a folder that
            // IS genuinely shell-registered — observed reliably from an unpackaged process shortly after
            // its own Register call (a caller/package-identity quirk of the per-folder lookup, not a
            // real "nothing registered" state; the registry entry is present and DisplayNameResource-
            // findable the whole time). Falling back to a direct registry-id lookup avoids leaving a
            // permanently orphaned Explorer sidebar entry every time this happens (this used to require
            // a separate manual cleanup pass over dozens of stray "PAWS - {name}" entries).
            PawsLog.Write($"TryUnregisterShell fast path failed for '{localPath}' ({ex.GetType().Name}: {ex.Message}); trying registry-id fallback.");

            var id = FindShellRegisteredId(localPath);
            if (id is null)
            {
                return false;
            }

            try
            {
                Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister(id);
                return true;
            }
            catch (Exception fallbackEx)
            {
                PawsLog.Write($"TryUnregisterShell registry-id fallback also failed for '{localPath}': {fallbackEx.GetType().Name}: {fallbackEx.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Finds a shell sync-root registration's full id for <paramref name="localPath"/> without going
    /// through the per-folder WinRT lookup (see the ERROR_NOT_FOUND caveat above). <see
    /// cref="ShellSyncRootId"/>'s id format is <c>{providerId}!{sid}!{pathHash}</c>, where the path-hash
    /// segment depends only on the current user's SID and the (case/trailing-slash-normalized) path —
    /// never on the provider id or name — so a registration for this exact path (by any provider,
    /// though in practice only ours) can be found by suffix-matching that segment directly against the
    /// registry, which the shell's own registration always keeps in sync.
    /// </summary>
    private static string? FindShellRegisteredId(string localPath)
    {
        var suffix = "!" + ComputeSidAndPathHashSuffix(localPath);

        const string RootKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";
        using var rootKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RootKeyPath);
        if (rootKey is null)
        {
            return null;
        }

        foreach (var name in rootKey.GetSubKeyNames())
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    private static string ComputeSidAndPathHashSuffix(string localPath)
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "S-1-0-0";
        var pathHash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.Unicode.GetBytes(localPath.TrimEnd('\\', '/').ToUpperInvariant()));
        return $"{sid}!{Convert.ToHexString(pathHash.AsSpan(0, 8))}";
    }

    public PlaceholderResult CreatePlaceholders(string localRoot, RemoteSnapshot remoteSnapshot)
    {
        var errors = new List<string>();
        var created = 0;
        var skipped = 0;

        // Group entries by their parent folder's relative path (root = "") so parents are created before
        // their children (the snapshot is sorted; we also process parents in ordinal order).
        var byParent = remoteSnapshot.Entries
            .GroupBy(e => ParentPath(e.RelativePath))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var parentPath in byParent.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            var parentFullPath = Path.Combine(localRoot, parentPath.Replace('/', Path.DirectorySeparatorChar));

            foreach (var child in byParent[parentPath])
            {
                var childFullPath = Path.Combine(localRoot, child.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(childFullPath) || Directory.Exists(childFullPath))
                {
                    skipped++;
                    continue;
                }

                // Create ONE placeholder per CfCreatePlaceholders call. Passing a multi-element array
                // corrupts adjacent entries' on-disk cloud metadata whenever an element carries a long
                // FileIdentity (our identities are ~45–68-char composite uids): the batched entry's identity
                // bleeds into the next entry, so browsing it fails with STATUS_CLOUD_FILE_METADATA_CORRUPT
                // ("The cloud file metadata is corrupt and unreadable"). Reproduced down to a 66-char
                // identity flipping the following folder to corrupt; single-element calls are unaffected and
                // match the reference providers (CloudMirror, Proton's windows-drive both pass count=1).
                var info = BuildPlaceholderInfo(child);
                try
                {
                    var single = new[] { info };
                    var hr = CfCreatePlaceholders(parentFullPath, single, 1, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out var processed);
                    if (hr.Succeeded)
                    {
                        created += (int)processed;
                    }
                    else
                    {
                        errors.Add($"{child.RelativePath}: 0x{hr.Code:X8}");
                    }
                }
                finally
                {
                    FreeIdentity(info);
                }
            }
        }

        return new PlaceholderResult(created, skipped, errors);
    }

    public IDisposable Connect(string localRoot, FetchFolderChildren fetchChildren, FetchPlaceholderData fetchData)
        => new HydrationConnection(localRoot, fetchChildren, fetchData);

    private const int PinnedAttribute = 0x80000;           // FILE_ATTRIBUTE_PINNED — "Always keep on this device"
    private const int RecallOnDataAccessAttribute = 0x400000; // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS — already cloud-only

    public DehydrateResult DehydrateTree(string path, TimeSpan? notUsedFor = null)
    {
        var dehydrated = 0;
        var skipped = 0;
        var errors = new List<string>();
        var cutoffUtc = notUsedFor is { } age ? DateTime.UtcNow - age : (DateTime?)null;

        IEnumerable<string> files;
        if (File.Exists(path))
        {
            files = [path];
        }
        else if (Directory.Exists(path))
        {
            // NOT Directory.EnumerateFiles(AllDirectories): that recursion descends into UNPOPULATED
            // directory placeholders too, which (a) fails outright when their population is pending
            // ("An invalid name request was made" — observed aborting the whole launch sweep on a
            // freshly-set-up pair, 2026-07-18) and (b) would otherwise force-populate every lazily
            // materialized folder just to look inside — during a sweep whose entire purpose is freeing
            // space. An unpopulated directory contains nothing hydrated, so it is skipped wholesale.
            files = EnumerateDehydrationCandidates(path, errors);
        }
        else
        {
            return new DehydrateResult(0, 0, [$"{path}: not found"]);
        }

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                var attributes = (int)info.Attributes;

                // Leave alone: pinned files, files already cloud-only, and (when an age is given) files
                // used recently — "used" meaning either written or read.
                if ((attributes & PinnedAttribute) != 0
                    || (attributes & RecallOnDataAccessAttribute) != 0
                    || (cutoffUtc is { } cutoff && (info.LastWriteTimeUtc > cutoff || info.LastAccessTimeUtc > cutoff)))
                {
                    skipped++;
                    continue;
                }

                if (TryDehydrate(file))
                {
                    dehydrated++;
                }
                else
                {
                    // In use, not a placeholder (a local file that hasn't synced), or not in sync
                    // (unpushed edits — the platform refuses, which is exactly the safety we want).
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{file}: {ex.Message}");
            }
        }

        return new DehydrateResult(dehydrated, skipped, errors);
    }

    // Walks the tree for DehydrateTree, descending only into directories whose content is actually
    // materialized locally — an unpopulated directory placeholder (RECALL_ON_DATA_ACCESS on a dir) is
    // skipped wholesale (nothing hydrated inside; enumerating it would trigger population, or fail while
    // population is pending). Per-directory failures are recorded and the walk continues, so one bad
    // directory can't abort the whole sweep.
    private static IEnumerable<string> EnumerateDehydrationCandidates(string root, List<string> errors)
    {
        var pending = new Queue<string>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory).ToList();

                foreach (var subDirectory in Directory.EnumerateDirectories(directory))
                {
                    if (((int)File.GetAttributes(subDirectory) & RecallOnDataAccessAttribute) == 0)
                    {
                        pending.Enqueue(subDirectory);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{directory}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static bool TryDehydrate(string file)
    {
        if (CfOpenFileWithOplock(file, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE, out var handle).Failed)
        {
            return false;
        }

        using (handle)
        {
            return CfDehydratePlaceholder(handle, 0, -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE).Succeeded;
        }
    }

    public bool HydrateFile(string path)
    {
        // Shared open (not the EXCLUSIVE oplock dehydration takes): hydration can run for as long as the
        // download does, and other readers opening the file mid-hydration is normal — the platform
        // coordinates the ranges. CfHydratePlaceholder blocks while cldflt raises FETCH_DATA against the
        // folder's connected provider (HydrationConnection.TransferAsync serves it, same as a user
        // opening the file in Explorer would).
        if (CfOpenFileWithOplock(path, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_NONE, out var handle).Failed)
        {
            return false;
        }

        using (handle)
        {
            return CfHydratePlaceholder(handle, 0, -1, CF_HYDRATE_FLAGS.CF_HYDRATE_FLAG_NONE).Succeeded;
        }
    }

    public DecommissionResult DecommissionTree(string localRoot, bool keepLocalFiles)
    {
        var reverted = 0;
        var deleted = 0;
        var kept = 0;
        var errors = new List<string>();

        if (!Directory.Exists(localRoot))
        {
            return new DecommissionResult(0, 0, 0, []);
        }

        if (keepLocalFiles)
        {
            CleanDirectoryKeepingLocal(localRoot, ref reverted, ref deleted, ref kept, errors);
        }
        else
        {
            // Delete everything under the root (the root itself is the user's chosen folder — keep it).
            // Placeholders are ordinary filesystem objects to delete; no revert needed first.
            foreach (var entry in Directory.EnumerateFileSystemEntries(localRoot))
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }

                    deleted++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{entry}: {ex.Message}");
                }
            }
        }

        return new DecommissionResult(reverted, deleted, kept, errors);
    }

    // Depth-first pass over one directory for the keep-local-files decommission. Files: hydrated
    // placeholders are reverted to plain files, cloud-only placeholders (content not on disk) are
    // deleted, plain files are kept. Sub-folders are processed first, then each placeholder folder is
    // deleted if nothing local remained inside it, or reverted to a plain folder otherwise.
    //
    // NOTE placeholder detection: under SHELL sync-root registration the Cloud Filter driver MASKS the
    // reparse-point attribute (a fully-hydrated placeholder is indistinguishable from a plain file by
    // attributes, and even CfGetPlaceholderStateFromFindData reports NO_STATES) — so never test
    // FileAttributes.ReparsePoint here. RECALL_ON_DATA_ACCESS is still surfaced and marks content that
    // is not fully local; for the rest, CfRevertPlaceholder itself is the authority — it succeeds on a
    // placeholder and returns ERROR_NOT_A_CLOUD_FILE on a plain file.
    private static void CleanDirectoryKeepingLocal(
        string directory, ref int reverted, ref int deleted, ref int kept, List<string> errors)
    {
        foreach (var subDirectory in Directory.EnumerateDirectories(directory))
        {
            try
            {
                // A symlink/junction points elsewhere — never treat its target as ours to clean.
                if (new DirectoryInfo(subDirectory).LinkTarget is not null)
                {
                    kept++;
                    continue;
                }

                CleanDirectoryKeepingLocal(subDirectory, ref reverted, ref deleted, ref kept, errors);

                var isEmpty = !Directory.EnumerateFileSystemEntries(subDirectory).Any();

                // RECALL_ON_DATA_ACCESS on a directory = a placeholder whose children were never fully
                // populated — cloud-backed by definition (a plain local folder never carries it), and
                // CfRevertPlaceholder refuses it as "partial". Empty after the pass above means nothing
                // local lives in it: remove it (its contents still live on the remote).
                if ((File.GetAttributes(subDirectory) & (FileAttributes)RecallOnDataAccessAttribute) != 0 && isEmpty)
                {
                    Directory.Delete(subDirectory);
                    deleted++;
                    continue;
                }

                switch (TryRevert(subDirectory, isDirectory: true))
                {
                    case RevertOutcome.NotPlaceholder:
                        kept++;
                        break;
                    case RevertOutcome.Reverted when isEmpty:
                        // The folder existed only to mirror the remote and nothing local remained in
                        // it — remove it (its contents still live on the remote).
                        Directory.Delete(subDirectory);
                        deleted++;
                        break;
                    case RevertOutcome.Reverted:
                        reverted++;
                        break;
                    default:
                        errors.Add($"{subDirectory}: could not revert the folder placeholder");
                        break;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{subDirectory}: {ex.Message}");
            }
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if ((File.GetAttributes(file) & (FileAttributes)RecallOnDataAccessAttribute) != 0)
                {
                    // Cloud-only (or partially hydrated): the content is not on disk, so "keep files"
                    // has nothing to keep — remove the local stub; the file stays on the remote.
                    File.Delete(file);
                    deleted++;
                    continue;
                }

                switch (TryRevert(file, isDirectory: false))
                {
                    case RevertOutcome.Reverted:
                        reverted++;
                        break;
                    case RevertOutcome.NotPlaceholder:
                        kept++; // plain local file — not ours to touch
                        break;
                    default:
                        // In use or refused — the data IS on disk, so leaving it is safe (a fully-
                        // hydrated placeholder keeps opening even after the sync root goes away).
                        errors.Add($"{file}: could not revert the placeholder (file in use?)");
                        break;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{file}: {ex.Message}");
            }
        }
    }

    private enum RevertOutcome
    {
        Reverted,
        NotPlaceholder,
        Failed,
    }

    // ERROR_NOT_A_CLOUD_FILE as an HRESULT — CfRevertPlaceholder/CfUpdatePlaceholder return it when the
    // target is a plain file, which is our only reliable placeholder test (see CleanDirectoryKeepingLocal).
    private static readonly HRESULT NotACloudFile = ((Win32Error)Win32Error.ERROR_NOT_A_CLOUD_FILE).ToHRESULT();

    // Strips a placeholder back to an ordinary file/folder (removes the cloud reparse point + metadata).
    // Directories can't take an exclusive oplock, so they open with plain write access instead
    // (CfRevertPlaceholder needs a writable handle).
    private static RevertOutcome TryRevert(string path, bool isDirectory)
    {
        var openFlags = isDirectory ? CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS : CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE;
        if (CfOpenFileWithOplock(path, openFlags, out var handle).Failed)
        {
            return RevertOutcome.Failed;
        }

        using (handle)
        {
            var hr = CfRevertPlaceholder(handle, CF_REVERT_FLAGS.CF_REVERT_FLAG_NONE);

            // A never-populated directory placeholder is "partial" and refuses to revert; marking it
            // populated (nothing more will be enumerated into it — we're decommissioning) unblocks it.
            if (hr.Failed && hr != NotACloudFile && isDirectory)
            {
                long usn = 0;
                if (CfUpdatePlaceholder(
                    handle, default, IntPtr.Zero, 0, null, 0,
                    CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DISABLE_ON_DEMAND_POPULATION | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC,
                    ref usn).Succeeded)
                {
                    hr = CfRevertPlaceholder(handle, CF_REVERT_FLAGS.CF_REVERT_FLAG_NONE);
                }
            }

            return hr.Succeeded ? RevertOutcome.Reverted
                : hr == NotACloudFile ? RevertOutcome.NotPlaceholder
                : RevertOutcome.Failed;
        }
    }

    public void FinalizeUploadedFile(string fullPath, string fileIdentity)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                return;
            }

            if (CfOpenFileWithOplock(fullPath, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE, out var handle).Failed)
            {
                return;
            }

            using (handle)
            {
                var identity = Marshal.StringToCoTaskMemUni(fileIdentity);
                try
                {
                    var identityLength = (uint)((fileIdentity.Length + 1) * sizeof(char));

                    // Whether the pushed file is a placeholder (edited, needs its identity refreshed to
                    // the NEW revision — else a later dehydrate + open would download the old content) or
                    // a plain local file (brand new, needs converting) can NOT be told from attributes:
                    // shell-registered roots mask the reparse-point attribute. So try the update path and
                    // let the API say "not a cloud file", then convert. Zeroed CF_FS_METADATA fields mean
                    // "no metadata change"; both paths mark the file in-sync so it can dehydrate.
                    long usn = 0;
                    var hr = CfUpdatePlaceholder(
                        handle, default, identity, identityLength, null, 0,
                        CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC, ref usn);
                    if (hr == NotACloudFile)
                    {
                        CfConvertToPlaceholder(
                            handle, identity, identityLength, CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC, out _);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(identity);
                }
            }
        }
        catch
        {
            // Best-effort — the file just stays non-dehydratable until the next enable rebuilds it.
        }
    }

    // Builds a placeholder for one remote entry. Caller must free the returned info's FileIdentity.
    private static CF_PLACEHOLDER_CREATE_INFO BuildPlaceholderInfo(RemoteEntry child)
    {
        var identity = child.IsFolder ? child.Uid : child.RevisionUid ?? child.Uid;
        var modified = (child.ModifiedUtc ?? DateTimeOffset.UtcNow).UtcDateTime.ToFileTimeStruct();

        return new CF_PLACEHOLDER_CREATE_INFO
        {
            RelativeFileName = child.Name,
            FileIdentity = Marshal.StringToCoTaskMemUni(identity),
            FileIdentityLength = (uint)((identity.Length + 1) * sizeof(char)),
            FsMetadata = new CF_FS_METADATA
            {
                FileSize = child.IsFolder ? 0 : child.Size ?? 0,
                BasicInfo = new Kernel32.FILE_BASIC_INFO
                {
                    FileAttributes = (FileFlagsAndAttributes)(child.IsFolder ? FileAttributes.Directory : FileAttributes.Normal),
                    CreationTime = modified,
                    LastWriteTime = modified,
                    ChangeTime = modified,
                    LastAccessTime = modified,
                },
            },
            Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
        };
    }

    private static void FreeIdentity(CF_PLACEHOLDER_CREATE_INFO info)
    {
        if (info.FileIdentity != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(info.FileIdentity);
        }
    }

    private static void FreeIdentities(IEnumerable<CF_PLACEHOLDER_CREATE_INFO> infos)
    {
        foreach (var info in infos)
        {
            FreeIdentity(info);
        }
    }

    private static string ParentPath(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    /// <summary>
    /// A live provider connection. Serves FETCH_PLACEHOLDERS (directory enumeration → that folder's
    /// children, listed lazily on first browse via the caller's callback) and FETCH_DATA (file open →
    /// download via the caller's callback). The native callbacks run on Cloud Filter threads; the (network)
    /// listing and download are offloaded to workers.
    /// </summary>
    private sealed class HydrationConnection : IDisposable
    {
        private const int ChunkSize = 1 << 20; // 1 MiB transfer chunks

        private readonly CF_CALLBACK _fetchDataCallback;          // must outlive the connection (native ref)
        private readonly CF_CALLBACK _fetchPlaceholdersCallback;  // must outlive the connection
        private readonly CF_CALLBACK_REGISTRATION[] _callbacks;   // must outlive the connection
        private readonly FetchPlaceholderData _fetchData;
        private readonly FetchFolderChildren _fetchChildren;
        private readonly string _localRootNormalized;
        private readonly CF_CONNECTION_KEY _connectionKey;

        // Serializes hydration: the Proton SDK's native crypto is not safe under concurrent downloads,
        // and a single file open fires several FETCH_DATA (multiple ranges + thumbnail/preview). We
        // download each file once and cache it, serving every requested range from a TEMP FILE rather
        // than an in-memory buffer — the SDK's only public download path is whole-file/sequential (no
        // true byte-range fetch — see the class remarks on TransferAsync), so a large file still gets
        // downloaded in full on first access, but keeping that content on disk instead of in a `byte[]`
        // means opening a multi-GB file doesn't balloon the app's memory.
        private readonly SemaphoreSlim _downloadGate = new(1, 1);
        private string? _cachedIdentity;
        private string? _cachedFilePath;
        private bool _disposed;

        public HydrationConnection(string localRoot, FetchFolderChildren fetchChildren, FetchPlaceholderData fetchData)
        {
            _fetchData = fetchData;
            _fetchChildren = fetchChildren;

            // Cloud Filter callback paths are volume-relative (no drive letter), e.g. "\Folder\Sub".
            _localRootNormalized = localRoot.Length >= 2 ? localRoot[2..] : localRoot;

            _fetchDataCallback = OnFetchData;
            _fetchPlaceholdersCallback = OnFetchPlaceholders;
            _callbacks =
            [
                new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = _fetchPlaceholdersCallback },
                new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = _fetchDataCallback },
                CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END,
            ];

            CfConnectSyncRoot(
                localRoot,
                _callbacks,
                IntPtr.Zero,
                CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO,
                out _connectionKey).ThrowIfFailed();
        }

        // Directory enumeration (first browse of a folder): list that folder's children LIVE and populate
        // its placeholders. Offloaded to a worker because the listing is a network call — this is what
        // makes population lazy/scalable (only browsed folders are materialized).
        private void OnFetchPlaceholders(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
        {
            if (_disposed)
            {
                return;
            }

            var operation = CreateOperationInfo(callbackInfo, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS);
            var relativeFolder = ToRelativeFolder(callbackInfo.NormalizedPath);
            _ = Task.Run(() => PopulateAsync(operation, relativeFolder));
        }

        // Nothing on this path times out on its own (ProviderHeartbeat only pings cfapi so IT doesn't give
        // up; it never gives up itself), so a stuck/degraded Drive session previously hung this forever —
        // wedging Explorer's entire browse of the folder (and, since a shell-registered root's provider
        // integration runs in Explorer's process, potentially the whole window) until PAWS was closed,
        // which force-tears-down the connection and is the only thing that ever unblocked it. Confirmed
        // live 2026-07-17: an independent, unrelated harness call (no cfapi, no Explorer, a fresh process)
        // hung identically on a plain Drive listing — the hang is in the Drive session/transport, not this
        // code — but PAWS still owes callers a bounded wait instead of propagating that hang forever.
        // 180s, not the original 60s: a large folder's listing genuinely CAN run minutes (per-child
        // metadata + name decryption on a cold session — 2026-07-18), and the heartbeat keeps Explorer
        // content to wait; this bound exists to end a degraded session's wait, not to police folder size.
        private const int ListingTimeoutSeconds = 180;

        private async Task PopulateAsync(CF_OPERATION_INFO operation, string relativeFolder)
        {
            if (_disposed)
            {
                return;
            }

            // Keeps this request alive across whatever we're about to wait on (see ProviderHeartbeat) —
            // listing a folder means calling into the shared Drive client, which can be busy for a long
            // time behind an unrelated push.
            using var heartbeat = new ProviderHeartbeat(operation);

            // The network listing runs outside the gate so it doesn't block hydration; a listing failure
            // (including a timeout) reports the enumeration as failed (and leaves on-demand population ON)
            // so a re-browse retries rather than caching an empty folder.
            IReadOnlyList<RemoteEntry> children;
            var populated = true;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ListingTimeoutSeconds));
                children = await _fetchChildren(relativeFolder, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Previously silent — a stuck listing left no trace at all. Logged so a recurrence is
                // visible in the log instead of only inferable from "Explorer hung again".
                PawsLog.Write($"On-demand folder listing timed out or failed for '{relativeFolder}': {ex.GetType().Name}: {ex.Message}");
                children = [];
                populated = false;
            }

            if (_disposed)
            {
                return;
            }

            // Serialize the CfExecute on the same gate Dispose drains, so we never transfer against a
            // disconnected connection key (see Dispose + TransferAsync).
            await _downloadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                SendPlaceholders(operation, children, populated);
            }
            finally
            {
                _downloadGate.Release();
            }
        }

        private void SendPlaceholders(CF_OPERATION_INFO operation, IReadOnlyList<RemoteEntry> children, bool populated)
        {
            var infos = children.Select(BuildPlaceholderInfo).ToArray();

            using var array = new SafeNativeArray<CF_PLACEHOLDER_CREATE_INFO>(infos);
            try
            {
                var transfer = new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
                {
                    PlaceholderArray = array,
                    PlaceholderCount = (uint)infos.Length,
                    PlaceholderTotalCount = (uint)infos.Length,
                    // On success, tell the platform this folder is fully provided — don't ask again. On a
                    // listing failure, leave population enabled so a re-browse retries, and fail the op.
                    Flags = populated
                        ? CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION
                        : CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE,
                    CompletionStatus = populated ? new NTStatus(0) : new NTStatus(0xC0000001u), // STATUS_SUCCESS / STATUS_UNSUCCESSFUL
                };
                var parameters = CF_OPERATION_PARAMETERS.Create(transfer);
                CfExecute(operation, ref parameters);
            }
            finally
            {
                FreeIdentities(infos);
            }
        }

        // File open: download the content and feed back the requested byte range.
        private void OnFetchData(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
        {
            // The callback structs (and the FileIdentity pointer) are only valid for this call, so copy
            // out everything the worker needs before returning.
            var operation = CreateOperationInfo(callbackInfo, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA);
            var identity = Marshal.PtrToStringUni(callbackInfo.FileIdentity) ?? string.Empty;
            var offset = callbackParameters.FetchData.RequiredFileOffset;
            var length = callbackParameters.FetchData.RequiredLength;

            _ = Task.Run(() => TransferAsync(operation, identity, offset, length));
        }

        // <summary>
        // NOTE on byte-range downloads: Cloud Filter hands us the exact [offset, offset+length) the
        // platform actually wants for this open — but Proton's SDK exposes only a whole-file, sequential
        // download (`FileDownloader.DownloadToStream`); the block-level machinery that could fetch just
        // the blocks overlapping a range (`BlockDownloader`, the block manifest, etc.) is all `internal`
        // to the SDK, not something this app can safely reach without reflecting into its
        // decryption/integrity-verification internals — a security-sensitive path we deliberately do not
        // reimplement (see paws-architecture memory, 2026-07-02 decision). So every first access still
        // downloads the WHOLE file regardless of the requested range; what changed is WHERE that content
        // is cached — a temp file on disk, not a `byte[]`, so a huge file no longer bloats memory (a
        // multi-GB open used to hold the entire decrypted file in RAM via `_cachedData`/`ToArray()`).
        // </summary>
        private async Task TransferAsync(CF_OPERATION_INFO operation, string identity, long offset, long length)
        {
            // Bail before touching the connection if it's being torn down (reconnect / disable): running
            // CfExecute against a disconnected connection key is an access violation.
            if (_disposed)
            {
                return;
            }

            // Keeps this request alive across whatever we're about to wait on (see ProviderHeartbeat) —
            // downloading a file means calling into the shared Drive client, which can be busy for a
            // long time behind an unrelated push, before a single byte of this fetch has moved.
            using var heartbeat = new ProviderHeartbeat(operation);

            try
            {
                // Serialize the whole hydration (download + transfer) — see _downloadGate.
                await _downloadGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Dispose() may have run while we were queued on the gate; the key is now dead.
                    if (_disposed)
                    {
                        return;
                    }

                    if (string.Equals(_cachedIdentity, identity, StringComparison.Ordinal) && _cachedFilePath is { } cachedPath)
                    {
                        // Already downloaded (e.g. a later range of the same open) — serve from the spill file.
                        var total = new FileInfo(cachedPath).Length;
                        if (offset > total)
                        {
                            offset = total;
                        }

                        if (offset + length > total)
                        {
                            length = total - offset;
                        }

                        TransferRangeFromFile(operation, cachedPath, offset, length);
                        return;
                    }

                    // A different file than whatever we cached before — that spill file is now stale.
                    DeleteCachedFile();

                    // First access: STREAM the download straight to the platform as bytes arrive, so a slow
                    // (e.g. throttled) transfer keeps reporting progress via CfExecute instead of buffering
                    // the whole file and only then sending it — which would blow Cloud Filter's hydration
                    // timeout on a large/slow download. The sink forwards the bytes falling in the requested
                    // range as they download, and ALSO spills every byte to a temp file so any other range
                    // of the same open is served from disk without re-downloading or holding it in memory.
                    var spillPath = NewTempFilePath();
                    var cached = false;
                    try
                    {
                        using (var sink = new CfHydrationSink(operation, offset, offset + length, spillPath))
                        {
                            await _fetchData(identity, sink, CancellationToken.None).ConfigureAwait(false);
                        }

                        _cachedFilePath = spillPath;
                        _cachedIdentity = identity;
                        cached = true;
                    }
                    finally
                    {
                        // A failed/cancelled download never gets cached — clean up its partial spill file
                        // rather than leaving it for the OS to reclaim later.
                        if (!cached)
                        {
                            try
                            {
                                File.Delete(spillPath);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                finally
                {
                    _downloadGate.Release();
                }
            }
            catch (Exception) when (!_disposed)
            {
                // A real failure (e.g. the download errored) while the connection is still live — tell the
                // platform the fetch failed so the open returns an error instead of hanging. If we're
                // disposing, skip this: CfExecute on the dead key would itself fault.
                TransferError(operation, offset, length);
            }
            catch (Exception)
            {
                // Torn down mid-transfer — nothing safe to report.
            }
        }

        // A fresh, collision-free path for a hydration's spill file, under a dedicated temp subfolder.
        private static string NewTempFilePath()
        {
            var directory = Path.Combine(Path.GetTempPath(), "PAWS-hydrate");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        }

        // Drops the current spill file (a different file is about to be cached, or the connection is
        // tearing down). Best-effort: a failed delete just leaves an orphaned temp file for the OS to
        // reclaim later, not a functional problem.
        private void DeleteCachedFile()
        {
            if (_cachedFilePath is { } path)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }

                _cachedFilePath = null;
                _cachedIdentity = null;
            }
        }

        private static unsafe void TransferRangeFromFile(CF_OPERATION_INFO operation, string filePath, long offset, long length)
        {
            if (length <= 0)
            {
                return;
            }

            using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            file.Seek(offset, SeekOrigin.Begin);

            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSize);
            try
            {
                long sent = 0;
                while (sent < length)
                {
                    var want = (int)Math.Min(ChunkSize, length - sent);
                    var read = ReadFully(file, buffer, want);
                    if (read <= 0)
                    {
                        break; // spill file ended early — nothing more to send for this range
                    }

                    fixed (byte* pointer = buffer)
                    {
                        var transfer = new CF_OPERATION_PARAMETERS.TRANSFERDATA
                        {
                            Length = read,
                            Offset = offset + sent,
                            Buffer = (IntPtr)pointer,
                            Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                        };
                        var parameters = CF_OPERATION_PARAMETERS.Create(transfer);
                        CfExecute(operation, ref parameters);
                    }

                    sent += read;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Stream.Read may return fewer bytes than requested even mid-file; loop until `count` bytes are
        // in hand or the stream is actually exhausted.
        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        private static void TransferError(CF_OPERATION_INFO operation, long offset, long length)
        {
            var transfer = new CF_OPERATION_PARAMETERS.TRANSFERDATA
            {
                Length = Math.Max(1, length),
                Offset = offset,
                Buffer = IntPtr.Zero,
                Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                CompletionStatus = new NTStatus(0xC0000001u), // STATUS_UNSUCCESSFUL
            };
            var parameters = CF_OPERATION_PARAMETERS.Create(transfer);
            CfExecute(operation, ref parameters);
        }

        /// <summary>
        /// A write-only <see cref="Stream"/> the downloader writes into: as bytes arrive it forwards the
        /// portion overlapping the requested range [<c>sendStart</c>, <c>sendEnd</c>) to the platform via
        /// <c>CfExecute(TRANSFER_DATA)</c> at the correct file offset — so hydration reports progress
        /// continuously during a slow download instead of stalling until the whole file is buffered. Every
        /// byte is also spilled to <paramref name="spillFilePath"/> (rather than kept in memory) so the
        /// caller can cache the full content on disk for other ranges of the same open, without a huge
        /// file ballooning the app's memory — see the class remarks on <c>TransferAsync</c>.
        /// </summary>
        private sealed class CfHydrationSink(CF_OPERATION_INFO operation, long sendStart, long sendEnd, string spillFilePath) : Stream
        {
            private readonly FileStream _spill = new(spillFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);

            public override bool CanWrite => true;

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override long Length => _spill.Length;

            public override long Position
            {
                get => _spill.Position;
                set => throw new NotSupportedException();
            }

            public override void Flush() => _spill.Flush();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => Emit(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer) => Emit(buffer);

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Emit(buffer.AsSpan(offset, count));
                return Task.CompletedTask;
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Emit(buffer.Span);
                return ValueTask.CompletedTask;
            }

            private void Emit(ReadOnlySpan<byte> data)
            {
                var fileStart = _spill.Position; // file offset of this write's first byte
                _spill.Write(data);               // spill everything to disk for the caller's cache
                var fileEnd = fileStart + data.Length;

                // The slice of this write that the platform is actually waiting on.
                var from = Math.Max(fileStart, sendStart);
                var to = Math.Min(fileEnd, sendEnd);
                if (to <= from)
                {
                    return;
                }

                SendChunks(operation, data.Slice((int)(from - fileStart), (int)(to - from)), from);
            }

            private static unsafe void SendChunks(CF_OPERATION_INFO op, ReadOnlySpan<byte> data, long fileOffset)
            {
                var sent = 0;
                while (sent < data.Length)
                {
                    var chunk = Math.Min(ChunkSize, data.Length - sent);
                    fixed (byte* pointer = &data[sent])
                    {
                        var transfer = new CF_OPERATION_PARAMETERS.TRANSFERDATA
                        {
                            Length = chunk,
                            Offset = fileOffset + sent,
                            Buffer = (IntPtr)pointer,
                            Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                        };
                        var parameters = CF_OPERATION_PARAMETERS.Create(transfer);
                        CfExecute(op, ref parameters);
                    }

                    sent += chunk;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    // Closes the write handle (flushing to disk) but does NOT delete the file — the
                    // connection keeps it around as the cache for other ranges of the same open, and
                    // cleans it up itself (a new identity, or connection teardown).
                    _spill.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private static CF_OPERATION_INFO CreateOperationInfo(in CF_CALLBACK_INFO callbackInfo, CF_OPERATION_TYPE type)
        {
            // ConnectionKey/TransferKey/RequestKey are VALUES and safe to carry to a worker thread.
            // CorrelationVector is a POINTER into platform memory that is only valid DURING the callback
            // (same lifetime rule as FileIdentity) — our CfExecute runs later, on a worker, after a
            // network call, so passing it along dereferences freed memory inside CfExecute = a native
            // access violation (seen in the wild: startup crash in SendPlaceholders after a force-close,
            // where the slow session resume widened the callback→CfExecute gap). It's optional telemetry;
            // leave it null.
            var operation = new CF_OPERATION_INFO
            {
                Type = type,
                ConnectionKey = callbackInfo.ConnectionKey,
                TransferKey = callbackInfo.TransferKey,
                RequestKey = callbackInfo.RequestKey,
            };
            operation.StructSize = (uint)Marshal.SizeOf(operation);
            return operation;
        }

        /// <summary>
        /// Keeps a pending Cloud Filter callback alive across a long wait. The platform gives every
        /// FETCH_DATA/FETCH_PLACEHOLDERS request a FIXED 60-SECOND timeout, and a valid operation on any
        /// pending request resets the timer for ALL of them — but that only happens automatically once
        /// something is actively moving bytes through <c>CfExecute</c>. Our two callbacks both start by
        /// calling into the shared Drive client (<see cref="_fetchData"/> / <see cref="_fetchChildren"/>),
        /// which serializes against the app's crypto gate — so a busy push can leave a brand-new request
        /// waiting, with ZERO progress reported, for as long as that push takes. Past 60s with nothing
        /// running, Explorer shows "Location is not available ... the cloud operation was not completed
        /// before the time-out period expired" and the whole window locks up (reported after a large
        /// local push: Explorer froze, then that exact error appeared for bigger files).
        /// <para>This pings <c>CfReportProviderProgress</c> on the SAME connection/transfer key every 20s
        /// (well under the 60s limit) starting the instant the callback fires — before we've even tried
        /// to acquire any lock — so the platform sees the operation as alive no matter how long the wait
        /// turns out to be. The reported percentage creeps upward but never reaches 100% until the real
        /// operation's own <c>CfExecute</c> call finishes it, so Explorer shows a visible (if
        /// approximate) "still working" indicator instead of a dead freeze.</para>
        /// </summary>
        private sealed class ProviderHeartbeat : IDisposable
        {
            private const int HeartbeatSeconds = 20;

            private readonly CF_CONNECTION_KEY _connectionKey;
            private readonly CF_TRANSFER_KEY _transferKey;
            private readonly Timer _timer;
            private long _ticks;
            private volatile bool _stopped;

            public ProviderHeartbeat(CF_OPERATION_INFO operation)
            {
                _connectionKey = operation.ConnectionKey;
                _transferKey = operation.TransferKey;
                _timer = new Timer(_ => Beat(), null, TimeSpan.FromSeconds(HeartbeatSeconds), TimeSpan.FromSeconds(HeartbeatSeconds));
            }

            private void Beat()
            {
                if (_stopped)
                {
                    return;
                }

                var completed = Math.Min(Interlocked.Increment(ref _ticks) * 10, 90);
                try
                {
                    CfReportProviderProgress(_connectionKey, _transferKey, 100, completed);
                }
                catch
                {
                    // Best-effort — a failed ping just means this tick didn't refresh the platform's timer;
                    // the next one (or the real operation's own CfExecute) still can.
                }
            }

            public void Dispose()
            {
                _stopped = true;
                _timer.Dispose();
            }
        }

        private string ToRelativeFolder(string normalizedPath)
        {
            if (normalizedPath.StartsWith(_localRootNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPath[_localRootNormalized.Length..].TrimStart('\\').Replace('\\', '/');
            }

            return normalizedPath.Replace('\\', '/');
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // Mark disposed FIRST so any transfer still queued on the gate bails instead of running
            // CfExecute against the connection we're about to disconnect.
            _disposed = true;

            // Wait for an in-flight transfer to finish (it holds the gate) so its CfExecute completes on the
            // still-connected key before we disconnect. Bounded so a stuck download can't hang teardown.
            var drained = _downloadGate.Wait(TimeSpan.FromSeconds(60));
            try
            {
                CfDisconnectSyncRoot(_connectionKey);
            }
            finally
            {
                if (drained)
                {
                    _downloadGate.Release();
                }
            }

            // Intentionally not disposing _downloadGate: a late transfer could still touch it, and a
            // disposed SemaphoreSlim throws. Let the GC reclaim it.
            DeleteCachedFile();
            GC.KeepAlive(_fetchDataCallback);
            GC.KeepAlive(_fetchPlaceholdersCallback);
            GC.KeepAlive(_callbacks);
        }
    }
}
