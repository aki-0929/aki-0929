namespace Kaling.StressTest;

internal sealed class StressOptions
{
    public int PayloadBytes { get; init; } = 64;
    public int ParallelSenders { get; init; } = 4;
    public int MaxFragmentSize { get; init; } = 16 * 1024;
    public int MaxConcurrentFragmentedCommands { get; init; } = 100;

    public static StressOptions Parse(string[] args)
    {
        return new StressOptions
        {
            PayloadBytes = ParseInt(args, "--payload", 64),
            ParallelSenders = Math.Clamp(ParseInt(args, "--parallel", 4), 1, 64),
            MaxFragmentSize = ParseInt(args, "--fragment", 16 * 1024),
            MaxConcurrentFragmentedCommands = ParseInt(args, "--slots", 100)
        };
    }

    private static int ParseInt(string[] args, string name, int defaultValue)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v))
                return v;

        return defaultValue;
    }
}
