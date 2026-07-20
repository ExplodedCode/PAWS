using System.Diagnostics;

namespace PAWS.Tests;

/// <summary>
/// Live (spawns real child processes) verification of PAWS.SingleInstanceGuard — the actual mechanism
/// PAWS.exe uses to ensure only one process runs per session (a second launch must bounce instead of
/// spinning up duplicate sync engines/tray icon, which would race the same on-demand sync roots and
/// Drive client). Exercises the REAL class via a small companion executable
/// (PAWS.Tests.SingleInstanceHost, which links the same source file PAWS.exe ships) rather than a
/// reimplementation. Ported from PAWS.AuthTest's --singleinstancetest.
/// </summary>
[Collection("CloudFilter")] // not cfapi-related, but shares the "don't run alongside other process-spawning tests" caution
public class SingleInstanceGuardTests
{
    private static string HostPath => Path.Combine(AppContext.BaseDirectory, "PAWS.Tests.SingleInstanceHost.exe");

    private static Process StartChild(string arg) => Process.Start(new ProcessStartInfo(HostPath, arg)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
    })!;

    [SkippableFact]
    public async Task SecondLaunch_BouncesToPrimary_AndPrimaryReactivatesAfterExit()
    {
        Skip.IfNot(File.Exists(HostPath), $"Companion test host not found at {HostPath} — build PAWS.Tests.SingleInstanceHost first.");

        // Step 1: start the "primary" child and let it actually acquire the mutex + register its
        // activation listener before racing a second process against it.
        var primary = StartChild("primary");
        var primaryFirstLine = await primary.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("PRIMARY", primaryFirstLine);
        await Task.Delay(500); // give ListenForActivation's background thread time to actually start waiting

        // Step 2: a second process launched while the primary is still running must detect it and bail —
        // fast (it must NOT sit around running a whole second copy of the app).
        var sw = Stopwatch.StartNew();
        var secondary = StartChild("secondary");
        var secondaryLine = await secondary.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        secondary.WaitForExit(5000);
        sw.Stop();
        Assert.Equal("SECONDARY", secondaryLine);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"second launch should exit quickly, took {sw.ElapsedMilliseconds}ms");

        // Step 3: the primary must have been signalled by the secondary's launch and reacted — proves the
        // cross-process EventWaitHandle activation actually fires, not just that the Mutex check works.
        var primarySecondLine = await primary.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        primary.WaitForExit(5000);
        Assert.Equal("ACTIVATED", primarySecondLine);

        // Step 4: once the primary has exited (its Mutex released along with the process), a brand-new
        // process must be able to become primary again — no orphaned lock left behind.
        var successor = StartChild("secondary");
        var successorLine = await successor.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        successor.WaitForExit(5000);
        Assert.Equal("PRIMARY", successorLine);
    }
}
