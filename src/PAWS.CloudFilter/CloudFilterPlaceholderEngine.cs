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
        => CfUnregisterSyncRoot(localPath).ThrowIfFailed();

    public PlaceholderResult CreatePlaceholders(string localRoot, RemoteSnapshot remoteSnapshot)
    {
        var errors = new List<string>();
        var created = 0;
        var skipped = 0;

        // Group entries by their parent folder's relative path (root = ""), so we can create each
        // folder's children in one CfCreatePlaceholders call. Parent-first (the snapshot is sorted).
        var byParent = remoteSnapshot.Entries
            .GroupBy(e => ParentPath(e.RelativePath))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var parentPath in byParent.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            var parentFullPath = Path.Combine(localRoot, parentPath.Replace('/', Path.DirectorySeparatorChar));
            var infos = new List<CF_PLACEHOLDER_CREATE_INFO>();

            foreach (var child in byParent[parentPath])
            {
                var childFullPath = Path.Combine(localRoot, child.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(childFullPath) || Directory.Exists(childFullPath))
                {
                    skipped++;
                    continue;
                }

                infos.Add(BuildPlaceholderInfo(child));
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
                FreeIdentities(infos);
            }
        }

        return new PlaceholderResult(created, skipped, errors);
    }

    public IDisposable Connect(string localRoot, RemoteSnapshot snapshot, FetchPlaceholderData fetchData)
        => new HydrationConnection(localRoot, snapshot, fetchData);

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

    private static void FreeIdentities(IEnumerable<CF_PLACEHOLDER_CREATE_INFO> infos)
    {
        foreach (var info in infos)
        {
            if (info.FileIdentity != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(info.FileIdentity);
            }
        }
    }

    private static string ParentPath(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    /// <summary>
    /// A live provider connection. Serves FETCH_PLACEHOLDERS (directory enumeration → the folder's
    /// children from the snapshot) and FETCH_DATA (file open → download via the caller's callback).
    /// The native callbacks run on Cloud Filter threads; the (network) download is offloaded to a worker.
    /// </summary>
    private sealed class HydrationConnection : IDisposable
    {
        private const int ChunkSize = 1 << 20; // 1 MiB transfer chunks

        private readonly CF_CALLBACK _fetchDataCallback;          // must outlive the connection (native ref)
        private readonly CF_CALLBACK _fetchPlaceholdersCallback;  // must outlive the connection
        private readonly CF_CALLBACK_REGISTRATION[] _callbacks;   // must outlive the connection
        private readonly FetchPlaceholderData _fetchData;
        private readonly Dictionary<string, List<RemoteEntry>> _childrenByParent;
        private readonly string _localRootNormalized;
        private readonly CF_CONNECTION_KEY _connectionKey;

        // Serializes hydration: the Proton SDK's native crypto is not safe under concurrent downloads,
        // and a single file open fires several FETCH_DATA (multiple ranges + thumbnail/preview). We
        // download each file once and cache it, serving every requested range from memory.
        private readonly SemaphoreSlim _downloadGate = new(1, 1);
        private string? _cachedIdentity;
        private byte[]? _cachedData;
        private bool _disposed;

        public HydrationConnection(string localRoot, RemoteSnapshot snapshot, FetchPlaceholderData fetchData)
        {
            _fetchData = fetchData;
            _childrenByParent = snapshot.Entries
                .GroupBy(e => ParentPath(e.RelativePath))
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

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

        // Directory enumeration: populate the folder's placeholders from the snapshot (in-memory, fast).
        private void OnFetchPlaceholders(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
        {
            var operation = CreateOperationInfo(callbackInfo, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS);

            var relativeFolder = ToRelativeFolder(callbackInfo.NormalizedPath);
            var children = _childrenByParent.TryGetValue(relativeFolder, out var list) ? list : [];
            var infos = children.Select(BuildPlaceholderInfo).ToArray();

            using var array = new SafeNativeArray<CF_PLACEHOLDER_CREATE_INFO>(infos);
            try
            {
                var transfer = new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
                {
                    PlaceholderArray = array,
                    PlaceholderCount = (uint)infos.Length,
                    PlaceholderTotalCount = (uint)infos.Length,
                    // Tell the platform this folder is fully provided — don't ask again.
                    Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION,
                    // CompletionStatus left default (STATUS_SUCCESS).
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
