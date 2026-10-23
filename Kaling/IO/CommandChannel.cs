using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using Kaling.Threading;

namespace Kaling.IO;

/// <summary>
/// Kestrel-style command channel over an <see cref="IDuplexPipe"/>. Commands are split into
/// fragments that interleave on the wire, so a large command never blocks small ones.
/// </summary>
/// <remarks>
/// Only one <see cref="ReadAsync"/> may run at a time, and the caller must keep reading: pings,
/// pong replies and the close handshake are processed inside it. Every other member is thread safe.
/// There are no background loops or timers. Cancellation comes from the caller's token.
/// </remarks>
public sealed class CommandChannel
{
    private const int HeaderSize = 16;
    private const int PingPayloadSize = 8;
    private static ReadOnlySpan<byte> Preface => "KL_HLX\0"u8;
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly CommandRegistry _registry;
    private readonly int _maxFragmentSize;
    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _abortCts = new();
    private readonly CancellationTokenSource _closingCts = new();
    private readonly TaskCompletionSource _sendsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile ConnectionState _state;
    private volatile CommandChannelClosedException? _closedException;
    private volatile CommandChannelAbortedException? _abortedException;
    private volatile bool _goAwayReceived;
    private volatile bool _goAwayFlushed;
    private volatile bool _completionRequested;
    private volatile Exception? _completionException;

    // Write side. Everything below is touched only while holding _writeLock unless noted.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _fragmentSlots;
    private readonly ConcurrentQueue<PendingControl> _pendingControl = new();
    private readonly ConcurrentDictionary<ulong, PendingPing> _pendingPings = new();
    private bool _prefaceSent;
    private volatile bool _goAwaySent;
    private long _lastStreamId;
    private int _activeSends;

    // Read side. Touched only by the single ReadAsync caller, or after the reader completed.
    private readonly Dictionary<ulong, Reassembly> _reassembly = [];
    private CancellationTokenSource _readCts = new();
    private ReaderState _readerState;
    private volatile Exception? _readTerminal;
    private ReadPhase _readPhase;
    private FragmentHeader _header;
    private int _remaining;
    private PayloadKind _payloadKind;
    private Reassembly? _target;
    private byte[]? _controlPayload;
    private int _controlFilled;
    private string? _receivedReason;

    /// <summary>
    /// Creates a connection on <paramref name="transport"/>. No handshake is needed; reading and
    /// sending can start immediately. The connection never disposes the transport.
    /// </summary>
    /// <param name="transport">The transport. The connection completes its pipes when it ends.</param>
    /// <param name="registry">Command mapping; must match the peer's.</param>
    /// <param name="options">Local limits; defaults are used when <see langword="null"/>.</param>
    public CommandChannel(IDuplexPipe transport, CommandRegistry registry, CommandChannelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(registry);
        options ??= new CommandChannelOptions();

        _input = transport.Input;
        _output = transport.Output;
        _registry = registry;
        _maxFragmentSize = options.MaxFragmentSize;
        _fragmentSlots = new SemaphoreSlim(options.MaxConcurrentFragmentedCommands,
            options.MaxConcurrentFragmentedCommands);
    }

    private enum ConnectionState
    {
        Open,
        Closing,
        Closed,
        Aborted
    }

    private enum ReaderState
    {
        Idle,
        Busy,
        Completed
    }

    private enum ReadPhase
    {
        Preface,
        Header,
        Payload
    }

    [Flags]
    private enum FragmentFlags : byte
    {
        None = 0,
        Start = 0x01,
        End = 0x02,
        Reset = 0x04,
        Control = 0x08,
        Known = Start | End | Reset | Control
    }

    private enum ControlType
    {
        Ping = 1,
        PingAck = 2,
        GoAway = 3
    }

    private enum PendingControlKind
    {
        PingAck,
        Reset
    }

    private enum ParseOutcome
    {
        NeedMoreData,
        Command,
        GoAway
    }

    private enum PayloadKind
    {
        Data,
        Control,
        Skip
    }

    /// <summary>
    /// Returns the next command. Throws <see cref="CommandChannelClosedException"/> after a graceful
    /// close once every command sent before it has been returned; never returns <see langword="null"/>.
    /// </summary>
    public async ValueTask<Command> ReadAsync(CancellationToken cancellationToken = default)
    {
        switch (Interlocked.CompareExchange(ref _readerState, ReaderState.Busy, ReaderState.Idle))
        {
            case ReaderState.Busy:
                throw new InvalidOperationException("Another ReadAsync call is already in progress.");
            case ReaderState.Completed:
                throw _readTerminal ?? GetAbortedException();
        }

        try
        {
            while (true)
            {
                if (_readTerminal is { } terminal) throw terminal;

                ThrowIfAborted();
                cancellationToken.ThrowIfCancellationRequested();

                var result = await ReadTransportAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                if (result.IsCanceled)
                {
                    _input.AdvanceTo(buffer.Start);
                    continue;
                }

                var outcome = ParseOutcome.NeedMoreData;
                Command? command = null;
                try
                {
                    outcome = Parse(ref buffer, out command);
                }
                finally
                {
                    _input.AdvanceTo(buffer.Start,
                        outcome == ParseOutcome.NeedMoreData ? result.Buffer.End : buffer.Start);
                }

                switch (outcome)
                {
                    case ParseOutcome.Command:
                        return command!;
                    case ParseOutcome.GoAway:
                        _goAwayReceived = true;
                        BeginClose(CommandChannelCloseInitiator.Remote, _receivedReason, []);
                        _readTerminal = (Exception?)_closedException ?? GetAbortedException();
                        TryFinishClose();
                        throw _readTerminal;
                }

                if (result.IsCompleted)
                {
                    if (!buffer.IsEmpty || _readPhase == ReadPhase.Payload || _reassembly.Count > 0)
                        throw new CommandChannelProtocolException(CommandChannelErrorCode.Truncated,
                            "The transport ended in the middle of a fragment or command.");

                    Abort(new EndOfStreamException("The peer ended the transport without closing the connection."));
                    throw GetAbortedException();
                }
            }
        }
        catch (CommandChannelProtocolException ex)
        {
            Abort(ex);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _readerState, ReaderState.Idle);
            TryCompleteInput();
        }
    }

    /// <summary>
    /// Sends one command. Concurrent calls interleave on the wire, so the peer receives them in the
    /// order they finish, not the order they were called; await one call before the next to keep order.
    /// </summary>
    /// <remarks>
    /// <paramref name="cancellationToken"/> is honored while waiting and between fragments. A flush
    /// that is already under way is only interrupted by <see cref="Abort"/>, because stopping it
    /// halfway would corrupt the stream.
    /// </remarks>
    /// <exception cref="ArgumentException">The command type is not registered.</exception>
    /// <exception cref="CommandChannelClosedException">The connection is closing; nothing was sent.</exception>
    /// <exception cref="CommandChannelAbortedException">The connection was aborted.</exception>
    public async ValueTask SendAsync(Command command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfNotOpen();
        if (!_registry.TryGetTypeId(command.GetType(), out var typeId))
            throw new ArgumentException($"Command type '{command.GetType()}' is not registered.", nameof(command));

        cancellationToken.ThrowIfCancellationRequested();

        var payload = new SegmentBuffer();
        try
        {
            command.Serialize(payload);
            var data = payload.AsSequence();
            var fragmented = data.Length > _maxFragmentSize;
            if (fragmented) await WaitAsync(_fragmentSlots, true, cancellationToken).ConfigureAwait(false);

            try
            {
                await SendFragmentsAsync(typeId, data, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (fragmented) _fragmentSlots.Release();
            }
        }
        finally
        {
            payload.Dispose();
        }
    }

    /// <summary>Protocol-level ping. Returns the round trip time.</summary>
    public async ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotOpen();
        cancellationToken.ThrowIfCancellationRequested();

        var ping = new PendingPing();
        ulong id;
        do
        {
            id = (ulong)Random.Shared.NextInt64(long.MinValue, long.MaxValue);
        } while (!_pendingPings.TryAdd(id, ping));

        try
        {
            await WaitAsync(_writeLock, true, cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfNotOpen();
                WritePrologue();
                ping.StartTimestamp = Stopwatch.GetTimestamp();
                WriteControl(ControlType.Ping, id);
                await FlushLockedAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseWriteLock();
            }

            using var wait = CreateWaitCancelScope(cancellationToken, _closingCts.Token, closingCancels: true);
            try
            {
                return await ping.Completion.Task.WaitAsync(wait.WaitToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw wait.ToException();
            }
        }
        finally
        {
            _pendingPings.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Graceful close: stops new sends, finishes sends already on the wire, then exchanges GoAway with
    /// the peer. Returns once both sides are done. If <paramref name="cancellationToken"/> fires first
    /// the connection is aborted.
    /// </summary>
    public async ValueTask CloseAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(reason)) reason = null;

        var reasonBytes = reason is null ? [] : Encoding.UTF8.GetBytes(reason);
        if (reasonBytes.Length > ushort.MaxValue)
            throw new ArgumentException($"The reason must not exceed {ushort.MaxValue} bytes in UTF-8.",
                nameof(reason));

        ThrowIfAborted();
        BeginClose(CommandChannelCloseInitiator.Local, reason, reasonBytes);
        try
        {
            await _closeCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Abort(ex);
            throw;
        }

        ThrowIfAborted();
    }

    /// <summary>
    /// Aborts immediately and completes the transport pipes with the reason. The reason is not sent to
    /// the peer. The transport itself is not disposed.
    /// </summary>
    public void Abort(Exception? reason = null)
    {
        CommandChannelAbortedException aborted;
        lock (_stateLock)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed) return;

            aborted = new CommandChannelAbortedException(reason);
            _abortedException = aborted;
            _state = ConnectionState.Aborted;
        }

        _ = _abortCts.CancelAsync();
        _ = _closingCts.CancelAsync();
        _sendsDrained.TrySetResult();
        _closeCompletion.TrySetResult();

        try
        {
            _input.CancelPendingRead();
        }
        catch
        {
        }

        try
        {
            _output.CancelPendingFlush();
        }
        catch
        {
        }

        RequestCompletion(aborted);
    }

    // ---------------------------------------------------------------- lifecycle

    private void BeginClose(CommandChannelCloseInitiator initiator, string? reason, byte[] reasonBytes)
    {
        lock (_stateLock)
        {
            if (_state != ConnectionState.Open) return;

            _closedException = new CommandChannelClosedException(initiator, reason, _closingCts.Token);
            _state = ConnectionState.Closing;
        }

        _ = _closingCts.CancelAsync();
        if (Volatile.Read(ref _activeSends) == 0) _sendsDrained.TrySetResult();

        _ = SendGoAwayAsync(initiator == CommandChannelCloseInitiator.Local ? reasonBytes : []);
    }

    private async Task SendGoAwayAsync(byte[] reason)
    {
        try
        {
            await _sendsDrained.Task.ConfigureAwait(false);
            await _writeLock.WaitAsync(_abortCts.Token).ConfigureAwait(false);
            try
            {
                ThrowIfAborted();
                WritePrologue();
                WriteControl(ControlType.GoAway, reason);
                _goAwaySent = true;
                await FlushLockedAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseWriteLock();
            }

            _goAwayFlushed = true;
            TryFinishClose();
        }
        catch (OperationCanceledException) when (_abortedException is not null)
        {
        }
        catch (Exception ex)
        {
            Abort(ex);
        }
    }

    private void TryFinishClose()
    {
        lock (_stateLock)
        {
            if (_state != ConnectionState.Closing || !_goAwayFlushed || !_goAwayReceived) return;

            _state = ConnectionState.Closed;
        }

        RequestCompletion(null);
        _closeCompletion.TrySetResult();
    }

    private void RequestCompletion(Exception? exception)
    {
        _completionException = exception;
        _completionRequested = true;
        TryCompleteInput();
        TryCompleteOutput();
    }

    private void TryCompleteInput()
    {
        if (!_completionRequested ||
            Interlocked.CompareExchange(ref _readerState, ReaderState.Completed, ReaderState.Idle) !=
            ReaderState.Idle) return;

        foreach (var reassembly in _reassembly.Values) reassembly.Buffer.Dispose();

        _reassembly.Clear();
        _target?.Buffer.Dispose();
        _target = null;

        try
        {
            _input.Complete(_completionException);
        }
        catch
        {
            // ignored
        }
    }

    private void TryCompleteOutput()
    {
        // The write lock is never released after completion, so nothing can write afterwards.
        if (!_completionRequested || !_writeLock.Wait(0)) return;

        _pendingControl.Clear();
        try
        {
            _output.Complete(_completionException);
        }
        catch
        {
            // ignored
        }
    }

    private void ThrowIfAborted()
    {
        if (_abortedException is { } aborted) throw aborted;
    }

    private void ThrowIfNotOpen()
    {
        switch (_state)
        {
            case ConnectionState.Open:
                return;
            case ConnectionState.Aborted:
                throw GetAbortedException();
            case ConnectionState.Closing:
            case ConnectionState.Closed:
            default:
                throw (Exception?)_closedException ?? GetAbortedException();
        }
    }

    private CommandChannelAbortedException GetAbortedException()
    {
        return _abortedException ?? new CommandChannelAbortedException(null);
    }

    private CancellationWaitScope CreateWaitCancelScope(
        CancellationToken userToken,
        CancellationToken lifetime,
        bool closingCancels)
    {
        return CancellationWaitScope.Create(
            userToken,
            lifetime,
            [
                () => _abortedException,
                () => userToken.IsCancellationRequested ? new OperationCanceledException(userToken) : null,
                () => closingCancels && _closedException is { } closed ? closed : null,
                static () => new OperationCanceledException()
            ]);
    }

    private async ValueTask WaitAsync(SemaphoreSlim semaphore, bool closingCancels, CancellationToken cancellationToken)
    {
        if (await semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;

        var lifetime = closingCancels ? _closingCts.Token : _abortCts.Token;
        using var wait = CreateWaitCancelScope(cancellationToken, lifetime, closingCancels);
        try
        {
            await semaphore.WaitAsync(wait.WaitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw wait.ToException();
        }
    }

    // ---------------------------------------------------------------- write side

    private async ValueTask SendFragmentsAsync(int typeId, ReadOnlySequence<byte> data,
        CancellationToken cancellationToken)
    {
        var streamId = (ulong)Interlocked.Increment(ref _lastStreamId);
        var length = data.Length;
        long offset = 0;
        var started = false;
        try
        {
            do
            {
                if (started) cancellationToken.ThrowIfCancellationRequested();

                // Once the first fragment is out the command must be finished, so closing no longer cancels it.
                await WaitAsync(_writeLock, !started, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (started)
                    {
                        ThrowIfAborted();
                    }
                    else
                    {
                        // Counted before the state check so a concurrent close either sees this send or rejects it.
                        Interlocked.Increment(ref _activeSends);
                        if (_state != ConnectionState.Open)
                        {
                            ExitSend();
                            ThrowIfNotOpen();
                        }

                        started = true;
                    }

                    var size = (int)Math.Min(_maxFragmentSize, length - offset);
                    var flags = (offset == 0 ? FragmentFlags.Start : FragmentFlags.None)
                                | (offset + size == length ? FragmentFlags.End : FragmentFlags.None);

                    WritePrologue();
                    WriteHeader(flags, typeId, streamId, size);
                    foreach (var memory in data.Slice(offset, size)) _output.Write(memory.Span);

                    offset += size;
                    await FlushLockedAsync().ConfigureAwait(false);
                }
                finally
                {
                    ReleaseWriteLock();
                }
            } while (offset < length);
        }
        catch (OperationCanceledException) when (started && offset < length && _abortedException is null &&
                                                 cancellationToken.IsCancellationRequested)
        {
            EnqueueControl(new PendingControl(PendingControlKind.Reset, streamId));
            throw;
        }
        finally
        {
            if (started) ExitSend();
        }
    }

    private void ExitSend()
    {
        if (Interlocked.Decrement(ref _activeSends) == 0 && _state != ConnectionState.Open)
            _sendsDrained.TrySetResult();
    }

    /// <summary>
    /// Writes the preface and queued control fragments. Caller holds the write lock.
    /// Returns whether anything was written.
    /// </summary>
    private bool WritePrologue()
    {
        var wrote = false;
        if (!_prefaceSent)
        {
            _output.Write(Preface);
            _prefaceSent = true;
            wrote = true;
        }

        while (_pendingControl.TryDequeue(out var control))
        {
            if (control.Kind == PendingControlKind.Reset)
                WriteHeader(FragmentFlags.Reset, 0, control.Value, 0);
            else
                WriteControl(ControlType.PingAck, control.Value);

            wrote = true;
        }

        return wrote;
    }

    private void WriteHeader(FragmentFlags flags, int typeId, ulong streamId, int length)
    {
        var header = _output.GetSpan(HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)length);
        header[2] = (byte)flags;
        header[3] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], typeId);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], streamId);
        _output.Advance(HeaderSize);
    }

    private void WriteControl(ControlType type, ulong value)
    {
        Span<byte> payload = stackalloc byte[PingPayloadSize];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, value);
        WriteControl(type, payload);
    }

    private void WriteControl(ControlType type, ReadOnlySpan<byte> payload)
    {
        WriteHeader(FragmentFlags.Control, (int)type, 0, payload.Length);
        _output.Write(payload);
    }

    private async ValueTask FlushLockedAsync()
    {
        FlushResult result;
        try
        {
            result = await _output.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Abort(ex);
            throw GetAbortedException();
        }

        if (result.IsCanceled || result.IsCompleted)
        {
            Abort(result.IsCompleted
                ? new IOException("The transport stopped reading.")
                : new OperationCanceledException("The pending flush was canceled."));
            throw GetAbortedException();
        }
    }

    private void EnqueueControl(PendingControl control)
    {
        _pendingControl.Enqueue(control);
        ScheduleControlDrain();
    }

    // Queued control fragments never wait behind a busy writer: whoever releases the lock retries this.
    private void ScheduleControlDrain()
    {
        if (_pendingControl.IsEmpty || !_writeLock.Wait(0)) return;

        _ = DrainControlAsync();
    }

    private async Task DrainControlAsync()
    {
        try
        {
            if (_abortedException is not null || _goAwaySent)
            {
                _pendingControl.Clear();
            }
            else
            {
                if (WritePrologue()) await FlushLockedAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Abort(ex);
        }
        finally
        {
            ReleaseWriteLock();
        }
    }

    private void ReleaseWriteLock()
    {
        _writeLock.Release();
        if (_completionRequested)
            TryCompleteOutput();
        else
            ScheduleControlDrain();
    }

    // ---------------------------------------------------------------- read side

    private async ValueTask<ReadResult> ReadTransportAsync(CancellationToken cancellationToken)
    {
        var cancellable = cancellationToken.CanBeCanceled;
        CancellationTokenRegistration registration = default;
        if (cancellable)
            registration = cancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(),
                _readCts);

        try
        {
            return cancellable
                ? await _input.ReadAsync(_readCts.Token).ConfigureAwait(false)
                : await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellable && _readCts.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            Abort(ex);
            throw GetAbortedException();
        }
        finally
        {
            if (cancellable)
            {
                registration.Dispose();
                if (!_readCts.TryReset())
                {
                    _readCts.Dispose();
                    _readCts = new CancellationTokenSource();
                }
            }
        }
    }

    private ParseOutcome Parse(ref ReadOnlySequence<byte> buffer, out Command? command)
    {
        command = null;
        Span<byte> scratch = stackalloc byte[HeaderSize];
        while (!buffer.IsEmpty || (_readPhase == ReadPhase.Payload && _remaining == 0))
            switch (_readPhase)
            {
                case ReadPhase.Preface:
                {
                    var count = (int)Math.Min(buffer.Length, Preface.Length);
                    buffer.Slice(0, count).CopyTo(scratch);
                    if (!scratch[..count].SequenceEqual(Preface[..count]))
                        throw new CommandChannelProtocolException(CommandChannelErrorCode.InvalidPreface,
                            "The peer did not start with the command channel preface.");

                    if (count < Preface.Length) return ParseOutcome.NeedMoreData;

                    buffer = buffer.Slice(Preface.Length);
                    _readPhase = ReadPhase.Header;
                    break;
                }

                case ReadPhase.Header:
                {
                    if (buffer.Length < HeaderSize) return ParseOutcome.NeedMoreData;

                    buffer.Slice(0, HeaderSize).CopyTo(scratch);
                    buffer = buffer.Slice(HeaderSize);
                    var header = new FragmentHeader(
                        BinaryPrimitives.ReadUInt16LittleEndian(scratch),
                        (FragmentFlags)scratch[2] & FragmentFlags.Known,
                        BinaryPrimitives.ReadInt32LittleEndian(scratch[4..]),
                        BinaryPrimitives.ReadUInt64LittleEndian(scratch[8..]));
                    if (BeginFragment(header, ref buffer, out command)) return ParseOutcome.Command;

                    break;
                }

                case ReadPhase.Payload:
                {
                    if (_remaining > 0)
                    {
                        var count = (int)Math.Min(_remaining, buffer.Length);
                        var data = buffer.Slice(0, count);
                        if (_payloadKind == PayloadKind.Data)
                        {
                            _target!.Buffer.Write(data);
                        }
                        else if (_payloadKind == PayloadKind.Control)
                        {
                            data.CopyTo(_controlPayload.AsSpan(_controlFilled));
                            _controlFilled += count;
                        }

                        buffer = buffer.Slice(count);
                        _remaining -= count;
                        if (_remaining > 0) return ParseOutcome.NeedMoreData;
                    }

                    if (EndFragment(out command) is { } outcome) return outcome;

                    break;
                }
            }

        return ParseOutcome.NeedMoreData;
    }

    /// <summary>Validates the header. Returns true when a whole single-fragment command was read without copying.</summary>
    private bool BeginFragment(FragmentHeader header, ref ReadOnlySequence<byte> buffer, out Command? command)
    {
        command = null;
        _readPhase = ReadPhase.Payload;
        _header = header;
        _remaining = header.Length;
        var flags = header.Flags;

        if ((flags & FragmentFlags.Control) != 0)
        {
            if ((flags & (FragmentFlags.Start | FragmentFlags.End | FragmentFlags.Reset)) != 0)
                throw InvalidHeader("A control fragment cannot carry Start, End or Reset.");

            switch ((ControlType)header.TypeId)
            {
                case ControlType.Ping:
                case ControlType.PingAck:
                    if (header.Length != PingPayloadSize)
                        throw InvalidHeader($"A ping payload must be {PingPayloadSize} bytes.");

                    break;
                case ControlType.GoAway:
                    break;
                default:
                    _payloadKind = PayloadKind.Skip;
                    return false;
            }

            _payloadKind = PayloadKind.Control;
            _controlPayload = new byte[header.Length];
            _controlFilled = 0;
            return false;
        }

        if ((flags & FragmentFlags.Reset) != 0)
        {
            if ((flags & (FragmentFlags.Start | FragmentFlags.End)) != 0)
                throw InvalidHeader("A reset fragment cannot carry Start or End.");

            if (_reassembly.Remove(header.StreamId, out var abandoned)) abandoned.Buffer.Dispose();

            _payloadKind = PayloadKind.Skip;
            return false;
        }

        _payloadKind = PayloadKind.Data;
        if ((flags & FragmentFlags.Start) != 0)
        {
            if (_reassembly.ContainsKey(header.StreamId))
                throw new CommandChannelProtocolException(CommandChannelErrorCode.StreamIdReused,
                    $"Stream {header.StreamId} is already being received.");

            if (!_registry.Contains(header.TypeId))
                throw new CommandChannelProtocolException(CommandChannelErrorCode.UnknownTypeId,
                    $"Type id {header.TypeId} is not registered.");

            var single = (flags & FragmentFlags.End) != 0;
            if (single && buffer.Length >= header.Length)
            {
                command = CreateCommand(header.TypeId, buffer.Slice(0, header.Length));
                buffer = buffer.Slice(header.Length);
                _readPhase = ReadPhase.Header;
                return true;
            }

            _target = new Reassembly(header.TypeId, new SegmentBuffer());
            if (!single) _reassembly.Add(header.StreamId, _target);

            return false;
        }

        if (!_reassembly.TryGetValue(header.StreamId, out var reassembly))
            throw new CommandChannelProtocolException(CommandChannelErrorCode.OrphanFragment,
                $"Stream {header.StreamId} has no Start fragment.");

        if (reassembly.TypeId != header.TypeId)
            throw new CommandChannelProtocolException(CommandChannelErrorCode.TypeIdMismatch,
                $"Stream {header.StreamId} changed type id from {reassembly.TypeId} to {header.TypeId}.");

        _target = reassembly;
        return false;
    }

    private ParseOutcome? EndFragment(out Command? command)
    {
        command = null;
        _readPhase = ReadPhase.Header;

        switch (_payloadKind)
        {
            case PayloadKind.Data:
                var target = _target!;
                _target = null;
                if ((_header.Flags & FragmentFlags.End) == 0) return null;

                _reassembly.Remove(_header.StreamId);
                try
                {
                    command = CreateCommand(target.TypeId, target.Buffer.AsSequence());
                }
                finally
                {
                    target.Buffer.Dispose();
                }

                return ParseOutcome.Command;

            case PayloadKind.Control:
                var payload = _controlPayload!;
                _controlPayload = null;
                switch ((ControlType)_header.TypeId)
                {
                    case ControlType.Ping:
                        EnqueueControl(new PendingControl(PendingControlKind.PingAck,
                            BinaryPrimitives.ReadUInt64LittleEndian(payload)));
                        return null;
                    case ControlType.PingAck:
                        if (_pendingPings.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(payload), out var ping))
                            ping.Completion.TrySetResult(Stopwatch.GetElapsedTime(ping.StartTimestamp));

                        return null;
                    default:
                        _receivedReason = payload.Length == 0 ? null : Encoding.UTF8.GetString(payload);
                        return ParseOutcome.GoAway;
                }

            default:
                return null;
        }
    }

    private Command CreateCommand(int typeId, in ReadOnlySequence<byte> payload)
    {
        var command = _registry.Create(typeId);
        try
        {
            command.Deserialize(payload);
        }
        catch (Exception ex)
        {
            throw new CommandChannelProtocolException(CommandChannelErrorCode.InvalidPayload,
                $"Failed to deserialize a command with type id {typeId}.", ex);
        }

        return command;
    }

    private static CommandChannelProtocolException InvalidHeader(string message)
    {
        return new CommandChannelProtocolException(CommandChannelErrorCode.InvalidHeader, message);
    }

    private readonly record struct FragmentHeader(ushort Length, FragmentFlags Flags, int TypeId, ulong StreamId);

    private readonly record struct PendingControl(PendingControlKind Kind, ulong Value);

    private sealed record Reassembly(int TypeId, SegmentBuffer Buffer);

    private sealed class PendingPing
    {
        public TaskCompletionSource<TimeSpan> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long StartTimestamp { get; set; }
    }

    /// <summary>
    /// Pooled blocks. The first block matches the request; later blocks are <see cref="BlockSize"/>
    /// unless one span asks for more. Growing never copies bytes already written.
    /// </summary>
    private sealed class SegmentBuffer : IBufferWriter<byte>, IDisposable
    {
        private const int BlockSize = 64 * 1024;

        private Segment? _first;
        private Segment? _last;
        private Segment? _tail;

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0) return;

            if (_tail is null || (uint)count > (uint)(_tail.Array.Length - _tail.Count))
                throw new InvalidOperationException("Cannot advance past the end of the buffer.");

            if (_tail.Count == 0)
            {
                if (_last is null)
                    _first = _tail;
                else
                    _last.SetNext(_tail);

                _last = _tail;
            }

            _tail.Count += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            return Reserve(sizeHint).Array.AsMemory(_tail!.Count);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            return Reserve(sizeHint).Array.AsSpan(_tail!.Count);
        }

        public void Write(in ReadOnlySequence<byte> data)
        {
            foreach (var memory in data)
            {
                var source = memory.Span;
                while (!source.IsEmpty)
                {
                    var available = _tail is null ? 0 : _tail.Array.Length - _tail.Count;
                    var target = GetSpan(available > 0 ? 1 : source.Length);
                    var count = Math.Min(target.Length, source.Length);
                    source[..count].CopyTo(target);
                    Advance(count);
                    source = source[count..];
                }
            }
        }

        /// <summary>Valid until the next write or <see cref="Dispose"/>. Bytes reserved but not advanced are excluded.</summary>
        public ReadOnlySequence<byte> AsSequence()
        {
            if (_first is null) return ReadOnlySequence<byte>.Empty;

            long runningIndex = 0;
            for (var segment = _first; segment is not null; segment = (Segment?)segment.Next)
            {
                segment.Seal(runningIndex);
                runningIndex += segment.Count;
            }

            return new ReadOnlySequence<byte>(_first, 0, _last!, _last!.Count);
        }

        public void Dispose()
        {
            for (var segment = _first; segment is not null; segment = (Segment?)segment.Next)
                ArrayPool<byte>.Shared.Return(segment.Array);

            if (_tail is not null && !ReferenceEquals(_tail, _last)) ArrayPool<byte>.Shared.Return(_tail.Array);

            _first = null;
            _last = null;
            _tail = null;
        }

        private Segment Reserve(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (sizeHint == 0) sizeHint = 1;

            if (_tail is not null && _tail.Array.Length - _tail.Count >= sizeHint) return _tail;

            if (_tail is { Count: 0 })
            {
                ArrayPool<byte>.Shared.Return(_tail.Array);
                _tail = null;
            }

            var minimum = _last is null ? sizeHint : Math.Max(sizeHint, BlockSize);
            _tail = new Segment(ArrayPool<byte>.Shared.Rent(minimum));
            return _tail;
        }

        private sealed class Segment : ReadOnlySequenceSegment<byte>
        {
            public Segment(byte[] array)
            {
                Array = array;
            }

            public byte[] Array { get; }

            public int Count { get; set; }

            public void SetNext(Segment next)
            {
                Next = next;
            }

            public void Seal(long runningIndex)
            {
                Memory = Array.AsMemory(0, Count);
                RunningIndex = runningIndex;
            }
        }
    }


    /// <summary>
    /// One application command. Subclasses own the payload; <see cref="CommandChannel"/> owns framing.
    /// The type id is assigned when the command is registered in a <see cref="CommandRegistry"/>.
    /// </summary>
    public abstract class Command
    {
        /// <summary>Writes the payload only. Any size is allowed.</summary>
        public abstract void Serialize(IBufferWriter<byte> writer);

        /// <summary>
        /// Reads one complete payload into this instance.
        /// <paramref name="payload"/> is valid only for this call; copy any bytes that must outlive it.
        /// </summary>
        public abstract void Deserialize(in ReadOnlySequence<byte> payload);
    }
}