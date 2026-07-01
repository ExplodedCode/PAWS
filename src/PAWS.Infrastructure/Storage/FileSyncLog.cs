namespace PAWS.Infrastructure.Storage;

/// <summary>
/// Appends diagnostic lines to a dated file under <c>%LOCALAPPDATA%\PAWS\logs</c>. Thread-safe and
/// best-effort — logging never throws into callers. Wire <see cref="Append"/> to
/// <c>PAWS.Core.Diagnostics.PawsLog.Writer</c> at startup.
/// </summary>
public sealed class FileSyncLog
{
    private readonly PawsPaths _paths;
    private readonly object _gate = new();

    public FileSyncLog(PawsPaths paths) => _paths = paths;

    /// <summary>Full path of today's log file (for surfacing to the user).</summary>
    public string CurrentFile => Path.Combine(_paths.LogsDirectory, $"paws-{DateTime.Now:yyyyMMdd}.log");

    public void Append(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_paths.LogsDirectory);
                File.AppendAllText(CurrentFile, line);
            }
            catch
            {
                // Best-effort diagnostics.
            }
        }
    }
}
