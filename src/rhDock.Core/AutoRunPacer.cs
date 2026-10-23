namespace rhDock.Core;

/// <summary>
/// Decides when Auto runs after an input change, from how long recent Runs blocked the host. A fast Script runs at once
/// and then at most once per Run time (never faster than a display frame), so the host stays responsive for at least
/// half of an edit. A Script slower than an interactive pause waits until edits stop. Times are milliseconds on one
/// monotonic clock supplied by the caller.
/// </summary>
public sealed class AutoRunPacer
{
    /// <summary>One display frame at 60 Hz; Auto never runs more often while edits continue.</summary>
    public const long FrameMs = 16;

    /// <summary>Runs longer than this freeze the host noticeably, so they wait for edits to stop for this long.</summary>
    public const long InteractiveMs = 100;

    private double? average;
    private long? lastEnd;

    /// <summary>The smoothed Run duration, or null before the first Run of this Script.</summary>
    public double? AverageMs => average;

    /// <summary>Records a finished Run that blocked the host for <paramref name="elapsedMs"/> and ended at <paramref name="endMs"/>.</summary>
    public void Record(double elapsedMs, long endMs)
    {
        elapsedMs = Math.Max(0, elapsedMs);
        average = average is { } previous ? (previous + elapsedMs) / 2 : elapsedMs;
        lastEnd = endMs;
    }

    /// <summary>Forgets measured durations, for a different Script or reloaded source.</summary>
    public void Reset()
    {
        average = null;
        lastEnd = null;
    }

    /// <summary>When Auto should run for an input change made at <paramref name="nowMs"/>. A later change replaces the result.</summary>
    public long Due(long nowMs)
    {
        if (average is { } slow && slow > InteractiveMs) return nowMs + InteractiveMs;
        if (lastEnd is not { } end) return nowMs;
        return Math.Max(nowMs, end + Math.Max(FrameMs, (long)Math.Ceiling(average ?? 0)));
    }
}
