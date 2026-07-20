using Microsoft.Win32;
using PAWS.Core.Diagnostics;
using Windows.ApplicationModel;

namespace PAWS.Infrastructure.Startup;

/// <summary>
/// Keeps Windows autostart in line with the "run on startup" setting. Prefers the proper packaged
/// mechanism — a <c>windows.startupTask</c> manifest extension (see Package.appxmanifest), managed via
/// <see cref="Windows.ApplicationModel.StartupTask"/> — which shows up in Settings ▸ Apps ▸ Startup and
/// (unlike a Run-key write we fully own) can be independently disabled by the user or an org policy,
/// which must be respected rather than silently overridden. <see cref="StartupTask.GetAsync"/> requires
/// package identity, so an unpackaged process falls back to the classic HKCU\...\Run key.
/// </summary>
public static class StartupRegistration
{
    // Must exactly match Package.appxmanifest's <uap5:StartupTask TaskId="...">.
    private const string StartupTaskId = "PAWSStartup";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PAWS";

    /// <summary>Registers or removes the autostart entry. Returns false if it could not be applied.</summary>
    public static async Task<bool> ApplyAsync(bool runOnStartup)
    {
        if (await TryApplyViaStartupTaskAsync(runOnStartup).ConfigureAwait(false))
        {
            // The packaged mechanism is now the source of truth — clear any stale Run-key entry a prior
            // version of PAWS (before this shipped) might have left behind, so there's never a duplicate
            // autostart entry racing the one Windows now tracks properly.
            RemoveRegistryEntry();
            return true;
        }

        return ApplyViaRegistry(runOnStartup);
    }

    // Packaged path. RequestEnableAsync shows a one-time system consent dialog only the first time the
    // task's state is undecided (Disabled); a user who has explicitly turned it off from Windows Settings
    // (DisabledByUser) stays off — this deliberately does not re-prompt or fight that choice on every
    // toggle. Throws for an unpackaged process (no package identity) — the sole condition ApplyAsync
    // treats as "try the registry fallback instead".
    // DIAGNOSTIC (2026-07-17): the whole call hung with ZERO consent UI, zero exception, zero log line —
    // confirmed live (relaunched while watching, nothing appeared). That's stronger than "waiting on a
    // dialog"; it means one specific await never returns at all. Logging + a bounded timeout around EACH
    // step (rather than the whole method) pins down exactly which call is stuck instead of guessing again.
    private const int StepTimeoutSeconds = 15;

    private static async Task<bool> TryApplyViaStartupTaskAsync(bool runOnStartup)
    {
        try
        {
            PawsLog.Write($"StartupTask '{StartupTaskId}': calling GetAsync...");
            var task = await WithTimeout(StartupTask.GetAsync(StartupTaskId).AsTask(), "GetAsync").ConfigureAwait(false);
            var stateBefore = task.State;
            PawsLog.Write($"StartupTask '{StartupTaskId}': GetAsync returned, state={stateBefore}.");

            if (runOnStartup)
            {
                if (task.State is StartupTaskState.Disabled)
                {
                    PawsLog.Write($"StartupTask '{StartupTaskId}': calling RequestEnableAsync...");
                    await WithTimeout(task.RequestEnableAsync().AsTask(), "RequestEnableAsync").ConfigureAwait(false);
                }
            }
            else if (task.State is StartupTaskState.Enabled)
            {
                task.Disable();
            }

            PawsLog.Write($"StartupTask '{StartupTaskId}': wanted RunOnStartup={runOnStartup}, state {stateBefore} -> {task.State}.");
            return true;
        }
        catch (Exception ex)
        {
            // No package identity (unpackaged process) is the expected/common case this falls back to the
            // registry for; logged anyway (with the real exception) so an unexpected failure on a packaged
            // run — where the fallback isn't really appropriate — doesn't stay silent.
            PawsLog.Write($"StartupTask '{StartupTaskId}' unavailable, falling back to registry: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Bounds one step so a genuinely stuck WinRT call surfaces as a logged, named timeout instead of a
    // silent forever-pending Task — this fire-and-forgets from OnLaunched now (see the caller), so a hang
    // here can no longer block app startup, but it would otherwise linger unexplained forever.
    private static async Task<T> WithTimeout<T>(Task<T> task, string stepName)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(StepTimeoutSeconds))).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException($"{stepName} did not complete within {StepTimeoutSeconds}s.");
        }

        return await task.ConfigureAwait(false);
    }

    private static bool ApplyViaRegistry(bool runOnStartup)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (runOnStartup)
            {
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            // Registry access denied or similar — the preference stays saved; retried next launch/toggle.
            return false;
        }
    }

    private static void RemoveRegistryEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
        }
    }
}
