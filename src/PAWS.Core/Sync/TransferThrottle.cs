using System.Diagnostics;

namespace PAWS.Core.Sync;

/// <summary>
/// App-wide transfer speed limits (KB/s; null = unlimited), shared by every transfer path — sync
/// uploads/downloads and on-demand hydration. Mutable at runtime: the Settings page updates the
/// properties and in-flight transfers pick the change up on their next chunk. Wrap the streams a
/// transfer reads from / writes to via <see cref="WrapUploadSource"/> / <see cref="WrapDownloadDestination"/>.
/// </summary>
public sealed class TransferThrottle
{
    private int _uploadKBps;   // 0 = unlimited
    private int _downloadKBps;

    /// <summary>Upload cap in KB/s; null = unlimited.</summary>
    public int? UploadLimitKBps
    {
        get { var v = Volatile.Read(ref _uploadKBps); return v > 0 ? v : null; }
        set => Volatile.Write(ref _uploadKBps, value.GetValueOrDefault());
    }

    /// <summary>Download cap in KB/s; null = unlimited.</summary>
    public int? DownloadLimitKBps
    {
        get { var v = Volatile.Read(ref _downloadKBps); return v > 0 ? v : null; }
        set => Volatile.Write(ref _downloadKBps, value.GetValueOrDefault());
    }

    /// <summary>Rate-limits reads from an upload's source stream at the current upload cap.</summary>
    public Stream WrapUploadSource(Stream source, bool leaveOpen = false)
        => new ThrottledStream(source, () => UploadLimitKBps, leaveOpen);

    /// <summary>Rate-limits writes to a download's destination stream at the current download cap.</summary>
    public Stream WrapDownloadDestination(Stream destination, bool leaveOpen = false)
        => new ThrottledStream(destination, () => DownloadLimitKBps, leaveOpen);
}

/// <summary>
/// A pass-through <see cref="Stream"/> that paces bytes through a token bucket: each chunk of I/O adds
/// "debt" against the current rate, and the next chunk waits it out — so the sustained rate converges on
/// the limit while short bursts (up to one second's worth of banked credit) stay smooth. The limit is
/// re-read from <c>limitKBps</c> on every chunk, so changing the setting mid-transfer takes effect
/// immediately; null/0 means no pacing at all. Reads are capped to 64 KiB per call (callers loop by the
/// Stream contract) to keep the pacing granular. Seek/Length/Position proxy to the inner stream, so a
/// wrapped upload source stays seekable (the SDK requires it).
/// </summary>
public sealed class ThrottledStream : Stream
{
    private const int MaxChunkBytes = 64 * 1024;

    private readonly Stream _inner;
    private readonly Func<int?> _limitKBps;
    private readonly bool _leaveOpen;
    private readonly object _gate = new();
    private double _allowanceBytes;
    private long _lastRefillTimestamp = Stopwatch.GetTimestamp();

    public ThrottledStream(Stream inner, Func<int?> limitKBps, bool leaveOpen = false)
    {
        _inner = inner;
        _limitKBps = limitKBps;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanWrite => _inner.CanWrite;

    public override bool CanSeek => _inner.CanSeek;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, Cap(count));
        Pace(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer[..Cap(buffer.Length)]);
        Pace(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer[..Cap(buffer.Length)], cancellationToken).ConfigureAwait(false);
        await PaceAsync(read, cancellationToken).ConfigureAwait(false);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var written = 0;
        while (written < buffer.Length)
        {
            var chunk = Math.Min(MaxChunkBytes, buffer.Length - written);
            _inner.Write(buffer.Slice(written, chunk));
            Pace(chunk);
            written += chunk;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var written = 0;
        while (written < buffer.Length)
        {
            var chunk = Math.Min(MaxChunkBytes, buffer.Length - written);
            await _inner.WriteAsync(buffer.Slice(written, chunk), cancellationToken).ConfigureAwait(false);
            await PaceAsync(chunk, cancellationToken).ConfigureAwait(false);
            written += chunk;
        }
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private static int Cap(int count) => Math.Min(count, MaxChunkBytes);

    private void Pace(int bytes)
    {
        if (bytes <= 0 || _limitKBps() is not (> 0 and var limit))
        {
            return;
        }

        var delay = Reserve(bytes, limit * 1024);
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }

    private async ValueTask PaceAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0 || _limitKBps() is not (> 0 and var limit))
        {
            return;
        }

        var delay = Reserve(bytes, limit * 1024);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    // Charges the bucket for the bytes just moved; a negative balance is time debt the caller sleeps off.
    // Idle time banks credit, capped at one second's worth so a long pause can't fund an unbounded burst.
    private TimeSpan Reserve(int bytes, int bytesPerSecond)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastRefillTimestamp, now);
            _lastRefillTimestamp = now;

            _allowanceBytes = Math.Min(bytesPerSecond, _allowanceBytes + elapsed.TotalSeconds * bytesPerSecond);
            _allowanceBytes -= bytes;

            return _allowanceBytes >= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(-_allowanceBytes / bytesPerSecond);
        }
    }
}
