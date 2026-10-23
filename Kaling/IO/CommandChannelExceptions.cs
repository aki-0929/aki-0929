namespace Kaling.IO;

/// <summary>The kind of protocol violation reported by <see cref="CommandChannelProtocolException"/>.</summary>
public enum CommandChannelErrorCode
{
    /// <summary>The peer did not start with the command channel preface; it is probably not a command channel endpoint.</summary>
    InvalidPreface,

    /// <summary>A fragment header has an invalid flag combination or control payload size.</summary>
    InvalidHeader,

    /// <summary>A command type id is not registered on this side.</summary>
    UnknownTypeId,

    /// <summary>The fragments of one command carry different type ids.</summary>
    TypeIdMismatch,

    /// <summary>A command started on a stream id that is still being received.</summary>
    StreamIdReused,

    /// <summary>A continuation fragment arrived for a command that never started.</summary>
    OrphanFragment,

    /// <summary>The transport ended in the middle of a fragment or command.</summary>
    Truncated,

    /// <summary><see cref="CommandChannel.Command.Deserialize"/> threw; the inner exception has the details.</summary>
    InvalidPayload
}

/// <summary>Which side started a graceful close.</summary>
public enum CommandChannelCloseInitiator
{
    /// <summary>This side called <see cref="CommandChannel.CloseAsync"/>.</summary>
    Local,

    /// <summary>The peer called <see cref="CommandChannel.CloseAsync"/>.</summary>
    Remote
}

/// <summary>The peer violated the protocol. Thrown only by the <c>ReadAsync</c> call that detected it.</summary>
public sealed class CommandChannelProtocolException : Exception
{
    internal CommandChannelProtocolException(CommandChannelErrorCode errorCode, string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>What the peer did wrong.</summary>
    public CommandChannelErrorCode ErrorCode { get; }
}

/// <summary>
/// The connection is closing or closed gracefully. Commands that fail with this exception were not
/// sent at all, so they can be safely resent on another connection.
/// </summary>
public sealed class CommandChannelClosedException : OperationCanceledException
{
    internal CommandChannelClosedException(CommandChannelCloseInitiator initiator, string? reason,
        CancellationToken closingToken)
        : base(
            reason is null
                ? $"The connection was closed ({initiator})."
                : $"The connection was closed ({initiator}): {reason}", closingToken)
    {
        CloseInitiator = initiator;
        Reason = reason;
    }

    /// <summary>Which side started the close.</summary>
    public CommandChannelCloseInitiator CloseInitiator { get; }

    /// <summary>The reason passed to <see cref="CommandChannel.CloseAsync"/> by the initiator, if any.</summary>
    public string? Reason { get; }
}

/// <summary>The connection ended abnormally. <see cref="Exception.InnerException"/> is the root cause.</summary>
public sealed class CommandChannelAbortedException : OperationCanceledException
{
    internal CommandChannelAbortedException(Exception? innerException)
        : base(
            innerException is null
                ? "The connection was aborted."
                : $"The connection was aborted: {innerException.Message}", innerException)
    {
    }
}