namespace Kaling.IO;

/// <summary>Local limits only. Nothing here is negotiated with or enforced on the peer.</summary>
public sealed class CommandChannelOptions
{
    /// <summary>Payload size of the fragments this side sends. Like <c>Http2Limits.MaxFrameSize</c>.</summary>
    public int MaxFragmentSize
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1024);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, ushort.MaxValue);
            field = value;
        }
    } = 16 * 1024;

    /// <summary>
    /// How many multi-fragment commands this side sends at once; further senders wait.
    /// Like <c>Http2Limits.MaxStreamsPerConnection</c>.
    /// </summary>
    public int MaxConcurrentFragmentedCommands
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 100;
}