using System.Diagnostics;
using System.IO.Pipelines;
using Kaling.IO;

namespace Kaling.StressTest;

internal sealed class StressEngine : IAsyncDisposable
{
    private readonly StressOptions _options;
    private readonly StressCounters _counters = new();
    private readonly Stopwatch _clock = new();
    private readonly DashboardSnapshot _frameA = new();
    private readonly DashboardSnapshot _frameB = new();
    private int _publishedIndex;

    private CommandChannel? _sender;
    private CommandChannel? _receiver;
    private CancellationTokenSource? _runCts;
    private Task[]? _sendTasks;
    private Task? _receiveTask;
    private Task? _samplerTask;

    private long _lastSent;
    private long _lastReceived;
    private TimeSpan _lastSampleAt;
    private byte[] _payloadBytes = [];
    private volatile bool _measuring;
    private volatile StressPhase _phase = StressPhase.Idle;
    private string? _startError;
    private int _startScheduled;

    public StressEngine(StressOptions options)
    {
        _options = options;
        _payloadBytes = CreatePayloadBytes(options.PayloadBytes);
        PayloadBytes = _payloadBytes.Length;
    }

    private static byte[] CreatePayloadBytes(int length)
    {
        length = Math.Max(0, length);
        if (length == 0) return [];

        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)'x';

        return bytes;
    }

    public int PayloadBytes { get; }

    public DashboardSnapshot GetPublishedSnapshot()
    {
        var index = Volatile.Read(ref _publishedIndex);
        return index == 0 ? _frameA : _frameB;
    }

    public LiveDashboardReading BuildLiveReading()
    {
        var measuringOrFinished = _measuring || _clock.Elapsed > TimeSpan.Zero;
        var sent = measuringOrFinished ? Interlocked.Read(ref _counters.Sent) : 0;
        var received = measuringOrFinished ? Interlocked.Read(ref _counters.Received) : 0;
        var elapsedSec = Math.Max(_clock.Elapsed.TotalSeconds, 0.001);
        var rates = GetPublishedSnapshot();

        return new LiveDashboardReading
        {
            Phase = _phase,
            StartError = _startError,
            Running = _measuring && _runCts is not null,
            Elapsed = _clock.Elapsed,
            Sent = sent,
            Received = received,
            InFlight = Math.Max(0, sent - received),
            AvgSendPerSec = measuringOrFinished ? sent / elapsedSec : 0,
            AvgRecvPerSec = measuringOrFinished ? received / elapsedSec : 0,
            InstSendPerSec = _measuring ? rates.InstSendPerSec : 0,
            InstRecvPerSec = _measuring ? rates.InstRecvPerSec : 0,
            ReceiveMibPerSec = measuringOrFinished ? received / elapsedSec * PayloadBytes / (1024.0 * 1024.0) : 0,
            SendErrors = measuringOrFinished ? Interlocked.Read(ref _counters.SendErrors) : 0,
            ReadErrors = measuringOrFinished ? Interlocked.Read(ref _counters.ReadErrors) : 0,
            PayloadBytes = PayloadBytes,
            ParallelSenders = _options.ParallelSenders,
            MaxFragmentSize = _options.MaxFragmentSize,
            MaxSlots = _options.MaxConcurrentFragmentedCommands
        };
    }

    /// <summary>Starts warmup + measure on a thread-pool thread; safe to call from the UI thread.</summary>
    public void BeginStart(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _startScheduled, 1, 0) != 0) return;

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(async () =>
        {
            try
            {
                await StartCoreAsync(_runCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _phase = StressPhase.Stopped;
            }
            catch (Exception ex)
            {
                _startError = ex.Message;
                _phase = StressPhase.Failed;
            }
        });
    }

    private async Task StartCoreAsync(CancellationToken stop)
    {
        if (_phase != StressPhase.Idle) return;

        _phase = StressPhase.WarmingUp;

        var channelOptions = new CommandChannelOptions
        {
            MaxFragmentSize = _options.MaxFragmentSize,
            MaxConcurrentFragmentedCommands = _options.MaxConcurrentFragmentedCommands
        };

        var registry = new CommandRegistry(b => b.Register<StressCommand>(1));
        var pipeOpts = new PipeOptions(pauseWriterThreshold: 1024 * 1024, resumeWriterThreshold: 512 * 1024,
            useSynchronizationContext: false);

        await WarmupAsync(registry, channelOptions, pipeOpts, stop).ConfigureAwait(false);
        stop.ThrowIfCancellationRequested();
        ScheduleMemoryTrim();

        var (pipeA, pipeB) = CreateLoopback(pipeOpts);
        _sender = new CommandChannel(pipeA, registry, channelOptions);
        _receiver = new CommandChannel(pipeB, registry, channelOptions);

        ResetMeasurementState();
        _measuring = true;
        _clock.Restart();
        PublishSnapshot(running: true, instantSend: 0, instantRecv: 0);
        _phase = StressPhase.Measuring;

        _receiveTask = Task.Run(() => ReceiveLoopAsync(stop), stop);
        _sendTasks = Enumerable.Range(0, _options.ParallelSenders).Select(senderIndex =>
            Task.Run(() => SendLoopAsync(senderIndex, stop), stop)).ToArray();
        _samplerTask = Task.Run(() => SamplerLoopAsync(stop), stop);
    }

    private void ResetMeasurementState()
    {
        Interlocked.Exchange(ref _counters.Sent, 0);
        Interlocked.Exchange(ref _counters.Received, 0);
        Interlocked.Exchange(ref _counters.SendErrors, 0);
        Interlocked.Exchange(ref _counters.ReadErrors, 0);
        _lastSent = 0;
        _lastReceived = 0;
        _lastSampleAt = TimeSpan.Zero;
    }

    private async Task WarmupAsync(
        CommandRegistry registry,
        CommandChannelOptions channelOptions,
        PipeOptions pipeOpts,
        CancellationToken cancellationToken)
    {
        const int warmupMessages = 4096;
        var (pipeA, pipeB) = CreateLoopback(pipeOpts);
        var sender = new CommandChannel(pipeA, registry, channelOptions);
        var receiver = new CommandChannel(pipeB, registry, channelOptions);
        var command = new StressCommand { Payload = _payloadBytes };

        try
        {
            var drain = Task.Run(async () =>
            {
                for (var i = 0; i < warmupMessages && !cancellationToken.IsCancellationRequested; i++)
                    await receiver.ReadAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken);

            for (var i = 0; i < warmupMessages && !cancellationToken.IsCancellationRequested; i++)
                await sender.SendAsync(command, cancellationToken).ConfigureAwait(false);

            await drain.ConfigureAwait(false);
        }
        finally
        {
            sender.Abort();
            receiver.Abort();
        }
    }

    /// <summary>Non-blocking trim; never call blocking GC from paths that affect UI responsiveness.</summary>
    private static void ScheduleMemoryTrim()
    {
        _ = Task.Run(static () =>
        {
            Thread.Sleep(300);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
        });
    }

    private async Task ReceiveLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
            try
            {
                _ = await _receiver!.ReadAsync(stop).ConfigureAwait(false);
                if (_measuring) Interlocked.Increment(ref _counters.Received);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                if (_measuring) Interlocked.Increment(ref _counters.ReadErrors);
                if (stop.IsCancellationRequested) break;
            }
    }

    private async Task SendLoopAsync(int senderIndex, CancellationToken stop)
    {
        if (senderIndex > 0)
        {
            try
            {
                await Task.Delay(senderIndex * 30, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
        }

        var command = new StressCommand { Payload = _payloadBytes };
        while (!stop.IsCancellationRequested)
        {
            if (_measuring) Interlocked.Increment(ref _counters.Sent);
            try
            {
                await _sender!.SendAsync(command, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                if (_measuring) Interlocked.Decrement(ref _counters.Sent);
                break;
            }
            catch
            {
                if (_measuring)
                {
                    Interlocked.Decrement(ref _counters.Sent);
                    Interlocked.Increment(ref _counters.SendErrors);
                }

                if (stop.IsCancellationRequested) break;
            }
        }
    }

    public async Task StopAsync()
    {
        if (_runCts is null && _phase is not (StressPhase.WarmingUp or StressPhase.Measuring)) return;

        _measuring = false;
        if (_runCts is not null) await _runCts.CancelAsync().ConfigureAwait(false);
        _sender?.Abort();
        _receiver?.Abort();

        try
        {
            if (_sendTasks is not null && _receiveTask is not null)
                await Task.WhenAll(_sendTasks.Append(_receiveTask)).WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        if (_samplerTask is not null)
        {
            try
            {
                await _samplerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _clock.Stop();
        PublishSnapshot(running: false, instantSend: 0, instantRecv: 0);
        _phase = StressPhase.Stopped;

        _runCts?.Dispose();
        _runCts = null;
        _sender = null;
        _receiver = null;
        _sendTasks = null;
        _receiveTask = null;
        _samplerTask = null;

        ScheduleMemoryTrim();
    }

    private async Task SamplerLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }

            if (!_measuring) continue;

            var now = _clock.Elapsed;
            var dt = (now - _lastSampleAt).TotalSeconds;
            if (dt <= 0) continue;

            var sent = Interlocked.Read(ref _counters.Sent);
            var received = Interlocked.Read(ref _counters.Received);
            var instSend = (sent - _lastSent) / dt;
            var instRecv = (received - _lastReceived) / dt;
            _lastSent = sent;
            _lastReceived = received;
            _lastSampleAt = now;

            PublishSnapshot(running: true, instSend, instRecv);
        }
    }

    private void PublishSnapshot(bool running, double instantSend, double instantRecv)
    {
        if (!_measuring && running) return;

        var sent = Interlocked.Read(ref _counters.Sent);
        var received = Interlocked.Read(ref _counters.Received);
        var elapsedSec = Math.Max(_clock.Elapsed.TotalSeconds, 0.001);

        var published = Volatile.Read(ref _publishedIndex);
        var writeIndex = 1 - published;
        var frame = writeIndex == 0 ? _frameA : _frameB;

        frame.Running = running;
        frame.Elapsed = _clock.Elapsed;
        frame.Sent = sent;
        frame.Received = received;
        frame.InFlight = Math.Max(0, sent - received);
        frame.AvgSendPerSec = sent / elapsedSec;
        frame.AvgRecvPerSec = received / elapsedSec;
        frame.InstSendPerSec = instantSend;
        frame.InstRecvPerSec = instantRecv;
        frame.ReceiveMibPerSec = received / elapsedSec * PayloadBytes / (1024.0 * 1024.0);
        frame.SendErrors = Interlocked.Read(ref _counters.SendErrors);
        frame.ReadErrors = Interlocked.Read(ref _counters.ReadErrors);
        frame.PayloadBytes = PayloadBytes;
        frame.ParallelSenders = _options.ParallelSenders;
        frame.MaxFragmentSize = _options.MaxFragmentSize;
        frame.MaxSlots = _options.MaxConcurrentFragmentedCommands;

        Interlocked.Exchange(ref _publishedIndex, writeIndex);
    }

    private static (IDuplexPipe A, IDuplexPipe B) CreateLoopback(PipeOptions options)
    {
        var aToB = new Pipe(options);
        var bToA = new Pipe(options);
        return (new DuplexPipe(bToA.Reader, aToB.Writer), new DuplexPipe(aToB.Reader, bToA.Writer));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
