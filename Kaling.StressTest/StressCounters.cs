namespace Kaling.StressTest;

/// <summary>Hot-path counters only; no I/O.</summary>
internal sealed class StressCounters
{
#pragma warning disable CS0649 // updated via Interlocked on the stress path
    public long Sent;
    public long Received;
    public long SendErrors;
    public long ReadErrors;
#pragma warning restore CS0649
}
