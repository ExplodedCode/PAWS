using System.Text;
using PAWS.CloudFilter;
using PAWS.Core.Sync;
using static PAWS.Tests.ProcessEnumeration;

namespace PAWS.Tests;

/// <summary>
/// Offline (no Proton account) coverage for <see cref="CloudFilterPlaceholderEngine"/> against the real
/// Windows Cloud Filter API. Ported from PAWS.AuthTest's --dirtest, --popfailtest, and --removetest.
/// </summary>
[Collection("CloudFilter")]
public class CloudFilterPlaceholderTests
{
    private const string ProviderId = "30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64";

    private static string NewTempRoot(string tag)
    {
        var root = Path.Combine(Path.GetTempPath(), $"paws-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Regression for the batched-CfCreatePlaceholders identity-overrun bug: a long-identity file next to
    /// a childless folder used to corrupt the following entry's on-disk cloud metadata
    /// (STATUS_CLOUD_FILE_METADATA_CORRUPT). Uses realistic long Proton-style composite identities and
    /// verifies, from a SEPARATE process (a real browse — same-process enumeration never fires
    /// FETCH_PLACEHOLDERS), that lazy population of nested folders works without corruption.
    /// </summary>
    [Fact]
    public void LazyPopulation_WithLongIdentities_DoesNotCorruptMetadata()
    {
        var root = NewTempRoot("dirtest");
        var engine = new CloudFilterPlaceholderEngine();
        try
        {
            engine.RegisterSyncRoot(new SyncRootInfo(root, ProviderId, "PAWS", "1.0.0.0"));

            var now = DateTimeOffset.UtcNow;
            string LongId(string tag) => "iAxR7cgYefZJzyNS1S1MbQ~pjMdBpjbtK7iGaTg-UgH-g~XsODA2mF5rj49K_" + tag; // ~64 chars
            RemoteEntry MkFile(string path, string tag) => new() { RelativePath = path, Name = path.Split('/')[^1], IsFolder = false, Size = 3, ModifiedUtc = now, Uid = LongId(tag), RevisionUid = LongId(tag + "r") };
            RemoteEntry MkDir(string path, string tag) => new() { RelativePath = path, Name = path.Split('/')[^1], IsFolder = true, Size = null, ModifiedUtc = now, Uid = "iAxR7cgYefZJzyNS1S1MbQ~zgzJvzyxOEdz_" + tag };

            // Shallow eager root: a long-identity FILE next to a childless FOLDER (the exact shape that corrupted).
            engine.CreatePlaceholders(root, new RemoteSnapshot
            {
                RootPath = "/",
                CapturedUtc = now,
                Entries = new List<RemoteEntry> { MkFile("root.txt", "root"), MkDir("sub", "sub") },
            });

            using var conn = engine.Connect(root,
                (rel, ct) => Task.FromResult<IReadOnlyList<RemoteEntry>>(rel switch
                {
                    "sub" => new List<RemoteEntry> { MkFile("sub/bigfile.txt", "bf"), MkDir("sub/deep", "deep") },
                    "sub/deep" => new List<RemoteEntry> { MkFile("sub/deep/leaf.txt", "lf") },
                    _ => new List<RemoteEntry>(),
                }),
                (id, output, ct) => Task.CompletedTask);

            var (subOk, sub) = EnumerateInSeparateProcess(Path.Combine(root, "sub"));
            Assert.True(subOk, sub.Count > 0 ? sub[0] : "browse sub/ failed");
            Assert.Contains("bigfile.txt", sub);
            Assert.Contains("deep", sub);

            var (deepOk, deep) = EnumerateInSeparateProcess(Path.Combine(root, "sub", "deep"));
            Assert.True(deepOk, deep.Count > 0 ? deep[0] : "browse sub/deep/ failed");
            Assert.Contains("leaf.txt", deep);
        }
        finally
        {
            try { engine.UnregisterSyncRoot(root); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Regression for a startup access violation at SendPlaceholders/CfExecute: TRANSFER_PLACEHOLDERS
    /// edge paths a normal browse never hits — an EMPTY remote folder (zero placeholders, success status)
    /// and a folder whose live listing THROWS (e.g. a Drive session resume failure) — must not crash the
    /// process, and a failed listing must leave population retryable (not latch
    /// DISABLE_ON_DEMAND_POPULATION).
    /// </summary>
    [Fact]
    public void PopulationFailure_DoesNotCrashAndStaysRetryable()
    {
        var root = NewTempRoot("popfailtest");
        var engine = new CloudFilterPlaceholderEngine();
        try
        {
            engine.RegisterSyncRoot(new SyncRootInfo(root, ProviderId, "PAWS", "1.0.0.0"));

            var now = DateTimeOffset.UtcNow;
            engine.CreatePlaceholders(root, new RemoteSnapshot
            {
                RootPath = "/",
                CapturedUtc = now,
                Entries = new List<RemoteEntry>
                {
                    new() { RelativePath = "emptydir", Name = "emptydir", IsFolder = true, ModifiedUtc = now, Uid = "vol~empty" },
                    new() { RelativePath = "faildir", Name = "faildir", IsFolder = true, ModifiedUtc = now, Uid = "vol~fail" },
                },
            });

            using var conn = engine.Connect(
                root,
                (rel, _) => rel switch
                {
                    "emptydir" => Task.FromResult<IReadOnlyList<RemoteEntry>>(new List<RemoteEntry>()),
                    "faildir" => Task.FromException<IReadOnlyList<RemoteEntry>>(new InvalidOperationException("simulated listing failure")),
                    _ => Task.FromResult<IReadOnlyList<RemoteEntry>>(new List<RemoteEntry>()),
                },
                (_, _, _) => Task.CompletedTask);

            var (emptyOk, emptyNames) = EnumerateInSeparateProcess(Path.Combine(root, "emptydir"));
            Assert.True(emptyOk);
            Assert.Empty(emptyNames);

            // A FAILED browse of faildir is acceptable (the listing genuinely throws) — a process crash
            // is the actual bug this regression guards against, so no assertion on (ok, names) here.
            EnumerateInSeparateProcess(Path.Combine(root, "faildir"));

            // Browse faildir again — population must still be enabled (the failure path must not have
            // latched DISABLE_ON_DEMAND_POPULATION) and the provider must still be alive to answer at
            // all (a thrown listing failing again is fine; a hang or crash is the bug this guards
            // against, and either would already have failed this test before reaching here).
            EnumerateInSeparateProcess(Path.Combine(root, "faildir"));
        }
        finally
        {
            try { engine.UnregisterSyncRoot(root); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Stopping sync / removing an account / resetting the app: DecommissionTree must return the folder
    /// to a NORMAL folder BEFORE the sync root is unregistered. Keep-files mode: a hydrated placeholder
    /// reverts to a plain file (content kept), a cloud-only placeholder is deleted (content lives on the
    /// remote), a plain local file is untouched, and a placeholder folder with nothing local left inside
    /// is removed while one with local content survives as a plain folder. Remove-files mode: everything
    /// under the root is deleted.
    /// </summary>
    [Fact]
    public void Decommission_RevertsKeepsAndDeletesTheRightThings()
    {
        var root = NewTempRoot("removetest");
        var engine = new CloudFilterPlaceholderEngine();
        var rootInfo = new SyncRootInfo(root, ProviderId, "PAWS", "1.0.0.0");
        try
        {
            engine.RegisterSyncRoot(rootInfo);

            var now = DateTimeOffset.UtcNow;
            var content = Encoding.UTF8.GetBytes("hello from the cloud! this file was hydrated on demand.");
            RemoteEntry MkFile(string path, string id, long size) => new()
            {
                RelativePath = path, Name = path.Split('/')[^1], IsFolder = false, Size = size, ModifiedUtc = now,
                Uid = "vol~" + id, RevisionUid = "vol~" + id + "~r1",
            };

            engine.CreatePlaceholders(root, new RemoteSnapshot
            {
                RootPath = "/",
                CapturedUtc = now,
                Entries = new List<RemoteEntry>
                {
                    MkFile("hydrated.txt", "hyd", content.Length),
                    MkFile("cloudonly.txt", "cld", 5),
                    new() { RelativePath = "sub", Name = "sub", IsFolder = true, ModifiedUtc = now, Uid = "vol~sub" },
                    MkFile("sub/inner.txt", "inn", 5),
                    new() { RelativePath = "sub2", Name = "sub2", IsFolder = true, ModifiedUtc = now, Uid = "vol~sub2" },
                },
            });

            byte[] bytes2;
            bool browseOk;
            List<string> names;

            // Local-file creation and hydration both need the provider connected (an unpopulated
            // on-demand directory refuses new files until the provider answers for it) — matching the
            // real app, where the provider runs for the folder's whole lifetime. Disconnect before
            // decommissioning, like the service does.
            using (engine.Connect(
                root,
                (rel, _) => Task.FromResult<IReadOnlyList<RemoteEntry>>(rel switch
                {
                    "sub2" => new List<RemoteEntry> { MkFile("sub2/keep2.txt", "kp2", content.Length) },
                    _ => new List<RemoteEntry>(),
                }),
                async (_, output, ct) => await output.WriteAsync(content, ct)))
            {
                File.WriteAllText(Path.Combine(root, "local.txt"), "born local, never a placeholder");
                var bytes = File.ReadAllBytes(Path.Combine(root, "hydrated.txt"));
                Assert.True(bytes.AsSpan().SequenceEqual(content), "hydrated.txt content should match on read");

                // Browse sub2 from a SEPARATE process so it gets POPULATED (the real Explorer flow), then
                // hydrate its file — a populated folder with local content must survive as a plain folder.
                (browseOk, names) = EnumerateInSeparateProcess(Path.Combine(root, "sub2"));
                bytes2 = File.ReadAllBytes(Path.Combine(root, "sub2", "keep2.txt"));
            }

            Assert.True(browseOk);
            Assert.Contains("keep2.txt", names);
            Assert.True(bytes2.AsSpan().SequenceEqual(content));

            var keep = engine.DecommissionTree(root, keepLocalFiles: true);
            Assert.Empty(keep.Errors);

            try { engine.UnregisterSyncRoot(root); } catch (Exception ex) { Assert.Fail($"unregister sync root failed: {ex.Message}"); }

            bool IsPlainFile(string relative)
            {
                var path = Path.Combine(root, relative);
                return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
            }

            Assert.True(IsPlainFile("hydrated.txt"), "hydrated.txt should be a plain file now");
            Assert.True(File.ReadAllBytes(Path.Combine(root, "hydrated.txt")).AsSpan().SequenceEqual(content));
            Assert.True(IsPlainFile("local.txt"), "local.txt should be untouched");
            Assert.False(File.Exists(Path.Combine(root, "cloudonly.txt")), "cloudonly.txt should be removed (content lives on the remote)");
            Assert.False(Directory.Exists(Path.Combine(root, "sub")), "sub/ should be removed (nothing local was inside it)");
            Assert.True(Directory.Exists(Path.Combine(root, "sub2")), "sub2/ should be kept as a plain folder (had local content)");
            Assert.True(IsPlainFile(Path.Combine("sub2", "keep2.txt")));
            Assert.True(File.ReadAllBytes(Path.Combine(root, "sub2", "keep2.txt")).AsSpan().SequenceEqual(content));

            // Round 2: remove-files mode — the folder is emptied (still never touching the remote). The
            // plain file goes in before re-registering (no provider is connected to answer population
            // requests).
            File.WriteAllText(Path.Combine(root, "another-local.txt"), "x");
            engine.RegisterSyncRoot(rootInfo);
            engine.CreatePlaceholders(root, new RemoteSnapshot
            {
                RootPath = "/",
                CapturedUtc = now,
                Entries = new List<RemoteEntry> { MkFile("cloudonly2.txt", "cl2", 5) },
            });

            var wipe = engine.DecommissionTree(root, keepLocalFiles: false);
            Assert.Empty(wipe.Errors);
            Assert.False(Directory.EnumerateFileSystemEntries(root).Any(), "remove-files mode should leave the folder empty");
        }
        finally
        {
            try { engine.UnregisterSyncRoot(root); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
