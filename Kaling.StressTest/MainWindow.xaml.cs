using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Kaling.StressTest;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush RunningBrush = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush StoppedBrush = new(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly SolidColorBrush WarmupBrush = new(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly SolidColorBrush FailedBrush = new(Color.FromRgb(0xF8, 0x51, 0x49));

    private readonly StressEngine _engine;
    private readonly DispatcherTimer _uiTimer;

    public MainWindow()
    {
        InitializeComponent();

        var options = StressOptions.Parse(Environment.GetCommandLineArgs());
        _engine = new StressEngine(options);

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _uiTimer.Tick += (_, _) => ApplyReading(_engine.BuildLiveReading());

        Loaded += (_, _) =>
        {
            ConfigText.Text =
                $"payload {options.PayloadBytes} B · parallel {options.ParallelSenders} · fragment {options.MaxFragmentSize} · slots {options.MaxConcurrentFragmentedCommands}";

            _uiTimer.Start();
            _engine.BeginStart();
        };

        Closed += async (_, _) =>
        {
            _uiTimer.Stop();
            await _engine.DisposeAsync();
        };
    }

    private async void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        await _engine.StopAsync().ConfigureAwait(true);
        ApplyReading(_engine.BuildLiveReading());
    }

    private void ApplyReading(LiveDashboardReading s)
    {
        ElapsedText.Text = s.Elapsed.ToString(@"hh\:mm\:ss\.ff");
        InstRecvText.Text = FormatRate(s.InstRecvPerSec);
        InstSendText.Text = FormatRate(s.InstSendPerSec);
        MibText.Text = s.ReceiveMibPerSec.ToString("F2");
        InFlightText.Text = s.InFlight.ToString("N0");
        SentText.Text = s.Sent.ToString("N0");
        RecvText.Text = s.Received.ToString("N0");
        AvgSendText.Text = $"平均 {s.AvgSendPerSec:N0} /s";
        AvgRecvText.Text = $"平均 {s.AvgRecvPerSec:N0} /s";
        ErrorsText.Text = $"send {s.SendErrors} / read {s.ReadErrors}";

        switch (s.Phase)
        {
            case StressPhase.WarmingUp:
                StatusText.Text = "预热中（JIT / 管道）…";
                StatusText.Foreground = WarmupBrush;
                StatusDot.Fill = WarmupBrush;
                break;
            case StressPhase.Measuring:
                StatusText.Text = "运行中";
                StatusText.Foreground = RunningBrush;
                StatusDot.Fill = RunningBrush;
                break;
            case StressPhase.Failed:
                StatusText.Text = string.IsNullOrEmpty(s.StartError) ? "启动失败" : $"启动失败: {s.StartError}";
                StatusText.Foreground = FailedBrush;
                StatusDot.Fill = FailedBrush;
                break;
            case StressPhase.Stopped:
                StatusText.Text = "已停止";
                StatusText.Foreground = StoppedBrush;
                StatusDot.Fill = StoppedBrush;
                break;
            default:
                StatusText.Text = "就绪";
                StatusText.Foreground = StoppedBrush;
                StatusDot.Fill = StoppedBrush;
                break;
        }
    }

    private static string FormatRate(double rate)
    {
        if (rate >= 1_000_000) return (rate / 1_000_000).ToString("F2") + "M";
        if (rate >= 10_000) return (rate / 1_000).ToString("F1") + "K";
        return rate.ToString("N0");
    }
}
