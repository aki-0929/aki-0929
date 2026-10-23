namespace Kaling.StressTest;

internal readonly struct LiveDashboardReading
{
    public StressPhase Phase { get; init; }
    public string? StartError { get; init; }
    public bool Running { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long Sent { get; init; }
    public long Received { get; init; }
    public long InFlight { get; init; }
    public double AvgSendPerSec { get; init; }
    public double AvgRecvPerSec { get; init; }
    public double InstSendPerSec { get; init; }
    public double InstRecvPerSec { get; init; }
    public double ReceiveMibPerSec { get; init; }
    public long SendErrors { get; init; }
    public long ReadErrors { get; init; }
    public int PayloadBytes { get; init; }
    public int ParallelSenders { get; init; }
    public int MaxFragmentSize { get; init; }
    public int MaxSlots { get; init; }
}
