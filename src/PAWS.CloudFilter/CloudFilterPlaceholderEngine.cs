using System.Runtime.InteropServices;
using PAWS.Core.Sync;
using Vanara.Extensions;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace PAWS.CloudFilter;

/// <summary>
/// <see cref="IPlaceholderEngine"/> over the Win32 Cloud Filter API (cldapi.dll, via Vanara). Registers
/// a local folder as a cloud sync root and mirrors a remote tree into it as on-demand placeholders.
/// Each placeholder stores a file-identity blob (the remote node's revision/UID) so the connected
/// provider can hydrate it on access (handled separately).
/// </summary>
public sealed class CloudFilterPlaceholderEngine : IPlaceholderEngine
{
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public void RegisterSyncRoot(SyncRootInfo info)
    {
        Directory.CreateDirectory(info.LocalPath);

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
                Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_FULL,
                Modifier = CF_POPULATION_POLICY_MODIFIER.CF_POPULATION_POLICY_MODIFIER_NONE,
            },
            InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_ALL,
            HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
        };
        policies.StructSize = (uint)Marshal.SizeOf(policies);

        CfRegisterSyncRoot(info.LocalPath, registration, policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE).ThrowIfFailed();
    }

    public void UnregisterSyncRoot(string localPath)
        => CfUnregisterSyncRoot(localPath).ThrowIfFailed();

    public PlaceholderResult CreatePlaceholders(string localRoot, RemoteSnapshot remoteSnapshot)
    {
        var errors = new List<string>();
        var created = 0;
        var skipped = 0;

        // Group entries by their parent folder's relative path (root = ""), so we can create each
        // folder's children in one CfCreatePlaceholders call. Folders are processed parent-first
        // (the snapshot is sorted by path) so a subfolder placeholder exists before we populate it.
        var byParent = remoteSnapshot.Entries
            .GroupBy(e => ParentPath(e.RelativePath))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var parentPath in byParent.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            var parentFullPath = Path.Combine(localRoot, parentPath.Replace('/', Path.DirectorySeparatorChar));
            var children = byParent[parentPath];

            var infos = new List<CF_PLACEHOLDER_CREATE_INFO>(children.Count);
            var allocated = new List<IntPtr>(children.Count);

            foreach (var child in children)
            {
                // Skip entries that already exist on disk (e.g. partially synced).
                var childFullPath = Path.Combine(localRoot, child.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(childFullPath) || Directory.Exists(childFullPath))
                {
                    skipped++;
                    continue;
                }

                var identity = child.IsFolder ? child.Uid : child.RevisionUid ?? child.Uid;
                var identityPtr = Marshal.StringToCoTaskMemUni(identity);
                allocated.Add(identityPtr);

                var attributes = child.IsFolder ? FileAttributes.Directory : FileAttributes.Normal;
                var modified = (child.ModifiedUtc ?? DateTimeOffset.UtcNow).UtcDateTime.ToFileTimeStruct();

                infos.Add(new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = child.Name,
                    FileIdentity = identityPtr,
                    FileIdentityLength = (uint)((identity.Length + 1) * sizeof(char)),
                    FsMetadata = new CF_FS_METADATA
                    {
                        FileSize = child.IsFolder ? 0 : child.Size ?? 0,
                        BasicInfo = new Kernel32.FILE_BASIC_INFO
                        {
                            FileAttributes = (FileFlagsAndAttributes)attributes,
                            CreationTime = modified,
                            LastWriteTime = modified,
                            ChangeTime = modified,
                            LastAccessTime = modified,
                        },
                    },
                    Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                });
            }

            try
            {
                if (infos.Count > 0)
                {
                    var array = infos.ToArray();
                    var hr = CfCreatePlaceholders(parentFullPath, array, (uint)array.Length, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out var processed);
                    if (hr.Succeeded)
                    {
                        created += (int)processed;
                    }
                    else
                    {
                        errors.Add($"{parentPath}: 0x{hr.Code:X8}");
                    }
                }
            }
            finally
            {
                foreach (var ptr in allocated)
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
        }

        return new PlaceholderResult(created, skipped, errors);
    }

    private static string ParentPath(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }
}
