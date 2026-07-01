namespace PAWS.Core.Diagnostics;

/// <summary>
/// Minimal diagnostic log hook. Sync/Core code calls <see cref="Write"/>; the host app points
/// <see cref="Writer"/> at a file sink (see the WinUI app). It's a no-op until wired, so the console
/// harness and tests don't need to set anything up. Never throws into callers.
/// </summary>
public static class PawsLog
{
    /// <summary>Where log lines go. Set once at app startup; left null elsewhere (no-op).</summary>
    public static Action<string>? Writer { get; set; }

    public static void Write(string message)
    {
        try
        {
            Writer?.Invoke(message);
        }
        catch
        {
            // Diagnostics must never break the operation being logged.
        }
    }
}
