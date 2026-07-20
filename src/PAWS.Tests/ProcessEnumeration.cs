using System.Diagnostics;

namespace PAWS.Tests;

/// <summary>
/// Enumerates a folder from a SEPARATE process (a fresh cmd.exe) — a real browse that triggers Cloud
/// Filter's FETCH_PLACEHOLDERS, unlike an in-process enumeration by the provider itself (which never
/// fires it — see CloudFilterPlaceholderTests for the discovery).
/// </summary>
internal static class ProcessEnumeration
{
    public static (bool Ok, List<string> Names) EnumerateInSeparateProcess(string folder)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c dir /b \"{folder}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            return (false, [$"dir failed: {stderr.Trim()}"]);
        }

        var names = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return (true, names);
    }
}
