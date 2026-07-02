using System.Runtime.InteropServices;
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

            // Register over an EXISTING id succeeds but keeps the old capabilities (observed live: a root
            // registered before AllowPinning was set stayed Flags-stale after re-register, so Explorer
            // never showed the pin menu). Verify what the shell actually recorded and, if the pin
            // capability is missing, unregister and register fresh.
            var recorded = Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder);
            if (!recorded.AllowPinning)
            {
                Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister(recorded.Id);
                Windows.Storage.Provider.StorageProviderSyncRootManager.Register(registration);
                recorded = Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder);
            }

            return recorded.AllowPinning;
        }
        catch
        {
            return false;
        }
    }

    // The shell requires the id format "{providerId}!{userSid}!{accountSegment}"; the last segment is a
    // stable per-folder tag so each pair registers as its own root.
    private static string ShellSyncRootId(SyncRootInfo info)
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "S-1-0-0";
        var pathHash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.Unicode.GetBytes(info.LocalPath.TrimEnd('\\', '/').ToUpperInvariant()));
        return $"{info.ProviderId}!{sid}!{Convert.ToHexString(pathHash.AsSpan(0, 8))}";
    }

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
        catch
        {
            return false;
        }
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
            files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
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
        // download each file once and cache it, serving every requested range from memory.
        private readonly SemaphoreSlim _downloadGate = new(1, 1);
        private string? _cachedIdentity;
        private byte[]? _cachedData;
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

        private async Task PopulateAsync(CF_OPERATION_INFO operation, string relativeFolder)
        {
            if (_disposed)
            {
                return;
            }

            // The network listing runs outside the gate so it doesn't block hydration; a listing failure
            // reports the enumeration as failed (and leaves on-demand population ON) so a re-browse retries
            // rather than caching an empty folder.
            IReadOnlyList<RemoteEntry> children;
            var populated = true;
            try
            {
                children = await _fetchChildren(relativeFolder, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
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

        private async Task TransferAsync(CF_OPERATION_INFO operation, string identity, long offset, long length)
        {
            // Bail before touching the connection if it's being torn down (reconnect / disable): running
            // CfExecute against a disconnected connection key is an access violation.
            if (_disposed)
            {
                return;
            }

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

                    if (string.Equals(_cachedIdentity, identity, StringComparison.Ordinal) && _cachedData is { } cached)
                    {
                        // Already downloaded (e.g. a later range of the same open) — serve from memory.
                        var total = cached.Length;
                        if (offset > total)
                        {
                            offset = total;
                        }

                        if (offset + length > total)
                        {
                            length = total - offset;
                        }

                        TransferRange(operation, cached, offset, length);
                        return;
                    }

                    // First access: STREAM the download straight to the platform as bytes arrive, so a slow
                    // (e.g. throttled) transfer keeps reporting progress via CfExecute instead of buffering
                    // the whole file and only then sending it — which would blow Cloud Filter's hydration
                    // timeout on a large/slow download. The sink forwards the bytes falling in the requested
                    // range as they download, and keeps the full content so any other range of the same open
                    // is served from memory without re-downloading.
                    using var sink = new CfHydrationSink(operation, offset, offset + length);
                    await _fetchData(identity, sink, CancellationToken.None).ConfigureAwait(false);
                    _cachedData = sink.ToArray();
                    _cachedIdentity = identity;
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

        private static unsafe void TransferRange(CF_OPERATION_INFO operation, byte[] data, long offset, long length)
        {
            if (length <= 0)
            {
                return;
            }

            long sent = 0;
            while (sent < length)
            {
                var chunk = (int)Math.Min(ChunkSize, length - sent);
                fixed (byte* pointer = &data[(int)(offset + sent)])
                {
                    var transfer = new CF_OPERATION_PARAMETERS.TRANSFERDATA
                    {
                        Length = chunk,
                        Offset = offset + sent,
                        Buffer = (IntPtr)pointer,
                        Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                    };
                    var parameters = CF_OPERATION_PARAMETERS.Create(transfer);
                    CfExecute(operation, ref parameters);
                }

                sent += chunk;
            }
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
        /// byte is also accumulated so the caller can cache the full content for other ranges of the open.
        /// </summary>
        private sealed class CfHydrationSink(CF_OPERATION_INFO operation, long sendStart, long sendEnd) : Stream
        {
            private readonly MemoryStream _accumulated = new();

            public override bool CanWrite => true;

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override long Length => _accumulated.Length;

            public override long Position
            {
                get => _accumulated.Length;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

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

            public byte[] ToArray() => _accumulated.ToArray();

            private void Emit(ReadOnlySpan<byte> data)
            {
                var fileStart = _accumulated.Length; // file offset of this write's first byte
                _accumulated.Write(data);            // keep everything for the caller's cache
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
                    _accumulated.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private static CF_OPERATION_INFO CreateOperationInfo(in CF_CALLBACK_INFO callbackInfo, CF_OPERATION_TYPE type)
        {
            var operation = new CF_OPERATION_INFO
            {
                Type = type,
                ConnectionKey = callbackInfo.ConnectionKey,
                TransferKey = callbackInfo.TransferKey,
                CorrelationVector = callbackInfo.CorrelationVector,
                RequestKey = callbackInfo.RequestKey,
            };
            operation.StructSize = (uint)Marshal.SizeOf(operation);
            return operation;
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
            _cachedData = null;
            GC.KeepAlive(_fetchDataCallback);
            GC.KeepAlive(_fetchPlaceholdersCallback);
            GC.KeepAlive(_callbacks);
        }
    }
}
