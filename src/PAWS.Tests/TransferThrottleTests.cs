using PAWS.Core.Sync;

namespace PAWS.Tests;

/// <summary>
/// Timing-based coverage for <see cref="TransferThrottle"/>'s token-bucket rate limiting, including the
/// per-pair override convention (null = inherit app-wide, 0 = explicit unlimited, positive = custom).
/// Ported from PAWS.AuthTest's --throttletest. Uses generous +/-1s windows around the expected ~3s/~0s
/// budgets to avoid CI timing flakiness while still catching a broken throttle in either direction.
/// </summary>
public class TransferThrottleTests
{
    private const int LimitKBps = 256;
    private static byte[] Payload => new byte[768 * 1024]; // 3 seconds of budget at 256 KB/s

    [Fact]
    public async Task Download_IsThrottledToRoughlyExpectedDuration()
    {
        var throttle = new TransferThrottle { DownloadLimitKBps = LimitKBps };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using (var dest = throttle.WrapDownloadDestination(new MemoryStream()))
        {
            await dest.WriteAsync(Payload);
        }
        sw.Stop();

        Assert.InRange(sw.Elapsed.TotalSeconds, 2.0, 5.0);
    }

    [Fact]
    public async Task Upload_IsThrottledToRoughlyExpectedDuration()
    {
        var throttle = new TransferThrottle { UploadLimitKBps = LimitKBps };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using (var source = throttle.WrapUploadSource(new MemoryStream(Payload)))
        {
            var buffer = new byte[81920];
            while (await source.ReadAsync(buffer) > 0)
            {
            }
        }
        sw.Stop();

        Assert.InRange(sw.Elapsed.TotalSeconds, 2.0, 5.0);
    }

    [Fact]
    public async Task NoLimitSet_IsEffectivelyInstant()
    {
        var throttle = new TransferThrottle();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using (var dest = throttle.WrapDownloadDestination(new MemoryStream()))
        {
            await dest.WriteAsync(Payload);
        }
        sw.Stop();

        Assert.True(sw.Elapsed.TotalSeconds < 1.0, $"expected ~0s, got {sw.Elapsed.TotalSeconds:0.00}s");
    }

    [Fact]
    public async Task PerPairOverride_SlowerThanAppWide_StillThrottles()
    {
        // App-wide 2048 KB/s would finish in ~0.4s unthrottled by the pair; the pair override (256 KB/s)
        // must still win and slow it down to ~3s.
        var throttle = new TransferThrottle { DownloadLimitKBps = 2048 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using (var dest = throttle.WrapDownloadDestination(new MemoryStream(), pairOverrideKBps: LimitKBps))
        {
            await dest.WriteAsync(Payload);
        }
        sw.Stop();

        Assert.InRange(sw.Elapsed.TotalSeconds, 2.0, 5.0);
    }

    [Fact]
    public async Task PerPairOverride_ExplicitUnlimited_BeatsSlowerAppWideLimit()
    {
        var throttle = new TransferThrottle { DownloadLimitKBps = LimitKBps };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using (var dest = throttle.WrapDownloadDestination(new MemoryStream(), pairOverrideKBps: 0))
        {
            await dest.WriteAsync(Payload);
        }
        sw.Stop();

        Assert.True(sw.Elapsed.TotalSeconds < 1.0, $"expected ~0s, got {sw.Elapsed.TotalSeconds:0.00}s");
    }
}
