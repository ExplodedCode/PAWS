using Microsoft.Win32;

namespace PAWS.Infrastructure.Startup;

/// <summary>
/// Keeps the per-user Windows autostart registration (HKCU\...\Run) in line with the "run on startup"
/// setting, pointing at the current executable so it follows the install location. Best-effort: a
/// packaged (MSIX) install should eventually switch to a StartupTask manifest extension; the Run key
/// covers the current unpackaged use.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PAWS";

    /// <summary>Registers or removes the autostart entry. Returns false if it could not be applied.</summary>
    public static bool Apply(bool runOnStartup)
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
}
