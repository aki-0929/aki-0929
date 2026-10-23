using System.Buffers;
using System.IO.Pipelines;
using Kaling.IO;

namespace Kaling.StressTest;

internal sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
{
    public PipeReader Input { get; } = input;

    public PipeWriter Output { get; } = output;
}

internal sealed class StressCommand : CommandChannel.Command
{
    public byte[] Payload { get; init; } = [];

    public override void Serialize(IBufferWriter<byte> writer) => writer.Write(Payload);

    public override void Deserialize(in ReadOnlySequence<byte> payload)
    {
        // Stress path: payload not verified; avoid per-message string allocations.
    }
}
