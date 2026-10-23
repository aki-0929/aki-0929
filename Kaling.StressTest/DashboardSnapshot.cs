namespace Kaling.StressTest;

internal sealed class DashboardSnapshot
{
    public bool Running { get; set; }
    public TimeSpan Elapsed { get; set; }
    public long Sent { get; set; }
    public long Received { get; set; }
    public long InFlight { get; set; }
    public double AvgSendPerSec { get; set; }
    public double AvgRecvPerSec { get; set; }
    public double InstSendPerSec { get; set; }
    public double InstRecvPerSec { get; set; }
    public double ReceiveMibPerSec { get; set; }
    public long SendErrors { get; set; }
    public long ReadErrors { get; set; }
    public int PayloadBytes { get; set; }
    public int ParallelSenders { get; set; }
    public int MaxFragmentSize { get; set; }
    public int MaxSlots { get; set; }
}
