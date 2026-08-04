using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PowerScalerLabs.App.Companions;
using PowerScalerLabs.App.Models;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.App;

public partial class MainWindow : Window
{
    private const ushort ControllerDPadLeft = 0x0004;
    private const ushort ControllerDPadRight = 0x0008;
    private const ushort ControllerA = 0x1000;
    private const ushort ControllerB = 0x2000;
    private const ushort ControllerLeftThumb = 0x0040;
    private const ushort ControllerRightThumb = 0x0080;
    private const ushort ControllerSafetyChord = ControllerLeftThumb | ControllerRightThumb;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        internal ushort Buttons;
        internal byte LeftTrigger;
        internal byte RightTrigger;
        internal short LeftThumbX;
        internal short LeftThumbY;
        internal short RightThumbX;
        internal short RightThumbY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        internal uint PacketNumber;
        internal XInputGamepad Gamepad;
    }

    private static class XInput
    {
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        internal static extern uint GetState(uint userIndex, out XInputState state);
    }

    private enum AppShutdownState
    {
        Running,
        CleanupInProgress,
        CleanupCompleted,
        FinalClose
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _windowLifetime = new();
    private readonly SemaphoreSlim _pipeWriteLock = new(1, 1);
    private readonly string _logsDirectory;
    private readonly string _appLogPath;
    private readonly HealthScaleCompanionManager _healthScaleCompanion;
    private readonly ProbeHostClient _probeClient;
    private readonly Dictionary<int, FighterRow> _fighterBySlot = [];
    private readonly List<ChronologySampleRow> _chronologyDisplayRows = [];

    private Process? _runtimeProcess;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _pipeWriter;
    private Task? _connectionTask;
    private bool _runtimeDesired = true;
    private int? _currentGameProcessId;
    private AppShutdownState _shutdownState;
    private Task? _shutdownCleanupTask;
    private ProbeStatusMessage? _lastProbeStatus;
    private long _transportRequested;
    private long _transportAcknowledged;
    private long _transportMissing;
    private long _transportDuplicates;
    private long _transportMalformed;
    private long _transportDropped;
    private ulong _lastTransportSequence;
    private readonly HashSet<ulong> _transportSequences = [];
    private readonly Dictionary<ulong, int> _receivedByTraceSession = [];
    private readonly List<string> _transportRuns = [];
    private readonly Dictionary<string, string> _transportGateResults = [];

    private sealed record TransportRunOutcome(
        string Name,
        int Requested,
        int Acknowledged,
        int Received,
        long Dropped,
        long Unaccounted,
        string State,
        bool Success);
    private sealed record HpWriteTraceSession(ulong TraceSessionId, ulong WatchId, ulong ActorAddress, int Slot,
        long SlotGeneration, long BattleInstanceId, string IdentityKey, ulong TargetAddress, uint TargetOffset,
        float CurrentHealthAtArm, float MaximumHealthAtArm, int InstrumentedThreadCount, long StartedQpc,
        string Stimulus);
    private sealed class WriterEvidence
    {
        internal required string Origin { get; init; }
        internal required ulong TrapRip { get; init; }
        internal int Count { get; set; }
        internal uint FirstScalar0Bits { get; set; }
        internal uint LastScalar0Bits { get; set; }
        internal uint FirstScalar1Bits { get; set; }
        internal uint LastScalar1Bits { get; set; }
        internal Dictionary<uint, int> Scalar0BitCounts { get; } = [];
    }
    private sealed class FighterLifetime
    {
        internal required ulong ActorAddress { get; init; }
        internal required int Slot { get; init; }
        internal required long Generation { get; init; }
        internal required long BattleInstanceId { get; init; }
        internal required string IdentityKey { get; init; }
        internal required long AcquiredQpc { get; init; }
        internal long? ReleasedQpc { get; set; }
    }
    private readonly List<FighterLifetime> _fighterLifetimes = [];
    private HpWriteTraceSession? _hpTraceSession;
    private HpWriteTraceSession? _lastHpTraceSession;
    private string _hpTraceEndDetail = string.Empty;
    private int _hpTraceCapturedEventCount;
    private readonly Dictionary<string, WriterEvidence> _hpWriterEvidence = [];
    private CancellationTokenSource? _hpAutoDisarmCancellation;
    private float? _hpTraceLastDetectedHealth;
    private int _hpTraceDetectedSubtractionCount;
    private bool _hpTraceDisarmPending;
    private bool _hpTraceSummaryWritten;
    private bool _probeReady;
    private long _nextTraceId = DateTime.UtcNow.Ticks;
    private readonly DispatcherTimer _controllerShortcutTimer;
    private ushort _previousControllerButtons;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _controllerShortcutTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _controllerShortcutTimer.Tick += ControllerShortcutTimer_Tick;
        _controllerShortcutTimer.Start();
        Closed += (_, _) => _controllerShortcutTimer.Stop();

        string persistentRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PowerScaler Labs");
        _logsDirectory = Path.Combine(persistentRoot, "Logs");
        _appLogPath = Path.Combine(_logsDirectory, "PowerScalerLabs.log");
        Directory.CreateDirectory(_logsDirectory);

        _healthScaleCompanion = new HealthScaleCompanionManager(persistentRoot);
        _probeClient = new ProbeHostClient(
            _windowLifetime.Token,
            status => Dispatcher.Invoke(() => ApplyProbeStatus(status)),
            message => Dispatcher.Invoke(() => AddLog(message)),
            traceEvent => Dispatcher.Invoke(() => ApplyProbeEvent(traceEvent)));
        InitializeFindings();

        AddLog("PowerScaler Labs Native Causal Probe Foundation started.");
        AddLog("Legacy guided overlay, generic recording, candidate ranking, and broad scanner UI are retired.");
        AddLog("The managed Runtime remains external/read-only; ProbeHost is a separate explicit-attach privilege lane.");
        AddLog("Controller research controls active: hold L3+R3; D-pad left/right changes stimulus, A arms, B disarms.");
    }

    private void ControllerShortcutTimer_Tick(object? sender, EventArgs e)
    {
        XInputState state = default;
        bool connected = false;
        try
        {
            for (uint userIndex = 0; userIndex < 4; userIndex++)
            {
                if (XInput.GetState(userIndex, out state) == 0)
                {
                    connected = true;
                    break;
                }
            }
        }
        catch (DllNotFoundException)
        {
            _controllerShortcutTimer.Stop();
            AddLog("Controller research controls unavailable: xinput1_4.dll was not found.");
            return;
        }
        if (!connected)
        {
            _previousControllerButtons = 0;
            return;
        }

        ushort buttons = state.Gamepad.Buttons;
        if ((buttons & ControllerSafetyChord) == ControllerSafetyChord)
        {
            if (Pressed(buttons, ControllerDPadLeft)) CycleStimulus(-1);
            else if (Pressed(buttons, ControllerDPadRight)) CycleStimulus(1);
            else if (Pressed(buttons, ControllerA) && ArmHpTraceButton.IsEnabled)
                ArmHpTraceButton_Click(this, new RoutedEventArgs());
            else if (Pressed(buttons, ControllerB) && DisarmHpTraceButton.IsEnabled)
                _ = DisarmHpTraceAsync("HP write trace disarmed from controller.", "ControllerDisarm");
        }
        _previousControllerButtons = buttons;
    }

    private bool Pressed(ushort current, ushort button) =>
        (current & button) != 0 && (_previousControllerButtons & button) == 0;

    private void CycleStimulus(int direction)
    {
        int count = HpTraceStimulusCombo.Items.Count;
        if (count == 0) return;
        int current = Math.Max(0, HpTraceStimulusCombo.SelectedIndex);
        HpTraceStimulusCombo.SelectedIndex = (current + direction + count) % count;
        AddLog($"Controller selected stimulus: {HpTraceStimulusCombo.SelectedItem}.");
    }

    public ObservableCollection<FighterRow> FighterRows { get; } = [];
    public ObservableCollection<SessionEventRow> EventRows { get; } = [];
    public BulkObservableCollection<ChronologySampleRow> ChronologyRows { get; } = [];
    public ObservableCollection<FindingRow> FindingRows { get; } = [];
    public ObservableCollection<ProbeTraceEventRow> ProbeTraceRows { get; } = [];

    private void InitializeFindings()
    {
        FindingRows.Add(new(
            "Current health",
            $"Battle_Mob + 0x{RuntimeProtocol.CurrentHealthOffset:X}",
            "Verified field",
            "Observed directly from validated live fighter objects and used by BattleCore fighter validation.",
            "State anchor"));
        FindingRows.Add(new(
            "Maximum health",
            $"Battle_Mob + 0x{RuntimeProtocol.MaximumHealthOffset:X}",
            "Verified field",
            "Observed directly from validated live fighter objects and used by BattleCore fighter validation.",
            "State anchor"));
        FindingRows.Add(new(
            "Current Ki",
            $"Battle_Mob + 0x{RuntimeProtocol.CurrentKiOffset:X}",
            "Source-backed candidate",
            "XV2 Patcher source identifies this current-value field; live spend/gain validation is still required.",
            "Research anchor"));
        FindingRows.Add(new(
            "Maximum Ki",
            $"Battle_Mob + 0x{RuntimeProtocol.MaximumKiOffset:X}",
            "Correlated candidate",
            "Retained as a chronology watch target; capacity semantics are not yet proven.",
            "Research anchor"));
        FindingRows.Add(new(
            "Current stamina",
            $"Battle_Mob + 0x{RuntimeProtocol.CurrentStaminaOffset:X}",
            "Source-backed candidate",
            "XV2 Patcher source identifies this current-value field; live spend/recovery validation is still required.",
            "Research anchor"));
        FindingRows.Add(new(
            "Maximum stamina",
            $"Battle_Mob + 0x{RuntimeProtocol.MaximumStaminaOffset:X}",
            "Correlated candidate",
            "Retained as a chronology watch target; capacity semantics are not yet proven.",
            "Research anchor"));
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => FitWindowToWorkArea();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FitWindowToWorkArea();
        RefreshHealthScaleCompanion();
        _probeClient.Start();
        await StartRuntimeAsync().ConfigureAwait(true);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownState == AppShutdownState.FinalClose)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownState != AppShutdownState.Running)
        {
            return;
        }

        _shutdownState = AppShutdownState.CleanupInProgress;
        _shutdownCleanupTask = PerformShutdownCleanupAsync();
    }

    private async Task PerformShutdownCleanupAsync()
    {
        try
        {
            ProbeCommandResult probeResult = await _probeClient.ShutdownAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            AddLog(probeResult.Success
                ? "Probe cleanup completed before App exit."
                : $"Probe cleanup unresolved at App exit: {probeResult.Detail}");

            await StopRuntimeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AddLog($"Shutdown cleanup encountered a recoverable error: {exception.Message}");
        }
        finally
        {
            _runtimeDesired = false;
            _probeClient.Dispose();
            _windowLifetime.Cancel();
            _pipeWriter?.Dispose();
            _pipeWriter = null;
            _pipe?.Dispose();
            _pipe = null;
            _runtimeProcess?.Dispose();
            _runtimeProcess = null;
            _shutdownState = AppShutdownState.CleanupCompleted;
            _ = Dispatcher.BeginInvoke(() =>
            {
                _shutdownState = AppShutdownState.FinalClose;
                Close();
            });
        }
    }

    private void FitWindowToWorkArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        const double edgeGap = 22;
        double usableWidth = Math.Max(MinWidth, workArea.Width - edgeGap * 2);
        double usableHeight = Math.Max(MinHeight, workArea.Height - edgeGap * 2);
        double targetWidth = Math.Min(980, workArea.Width * 0.82);
        double targetHeight = Math.Min(660, workArea.Height * 0.82);

        Width = Math.Clamp(targetWidth, MinWidth, usableWidth);
        Height = Math.Clamp(targetHeight, MinHeight, usableHeight);
        WindowState = WindowState.Normal;
        Left = Math.Max(workArea.Left + edgeGap, workArea.Left + (workArea.Width - Width) / 2);
        Top = Math.Max(workArea.Top + edgeGap, workArea.Top + (workArea.Height - Height) / 2);
    }

    private Task StartRuntimeAsync()
    {
        _runtimeDesired = true;
        if (_runtimeProcess is { HasExited: false })
        {
            AddLog("Research runtime is already running.");
            EnsureConnectionLoop();
            return Task.CompletedTask;
        }

        string runtimePath = Path.Combine(AppContext.BaseDirectory, "Runtime", "PowerScalerLabs.Runtime.exe");
        if (!File.Exists(runtimePath))
        {
            SetDisconnectedState("Runtime executable missing", $"Expected: {runtimePath}");
            AddLog($"ERROR: Runtime executable was not found at {runtimePath}");
            return Task.CompletedTask;
        }

        try
        {
            ProcessStartInfo startInfo = new(runtimePath)
            {
                WorkingDirectory = Path.GetDirectoryName(runtimePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _runtimeProcess = Process.Start(startInfo);
            if (_runtimeProcess is null)
            {
                throw new InvalidOperationException("Windows did not return a runtime process handle.");
            }

            AddLog($"Started external research runtime PID {_runtimeProcess.Id}.");
            SetConnectingState("Runtime started. Connecting to the research pipe…");
            EnsureConnectionLoop();
        }
        catch (Exception exception)
        {
            SetDisconnectedState("Runtime start failed", exception.Message);
            AddLog($"ERROR: Unable to start research runtime: {exception}");
        }

        return Task.CompletedTask;
    }

    private void EnsureConnectionLoop()
    {
        if (_connectionTask is { IsCompleted: false })
        {
            return;
        }
        _connectionTask = ConnectAndReadAsync(_windowLifetime.Token);
    }

    private async Task ConnectAndReadAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _runtimeDesired)
        {
            try
            {
                await DisconnectPipeAsync().ConfigureAwait(false);
                NamedPipeClientStream pipe = new(
                    ".",
                    RuntimeProtocol.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
                _pipe = pipe;
                _pipeWriter = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using StreamReader reader = new(pipe, leaveOpen: true);
                await Dispatcher.InvokeAsync(() => AddLog("Connected to the external research runtime."));

                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    RuntimeStatusMessage? message = JsonSerializer.Deserialize<RuntimeStatusMessage>(line, JsonOptions);
                    if (message is not null)
                    {
                        await Dispatcher.InvokeAsync(() => ApplyStatus(message));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (TimeoutException)
            {
                await Dispatcher.InvokeAsync(() => SetConnectingState("Waiting for the external research runtime…"));
            }
            catch (IOException exception)
            {
                await Dispatcher.InvokeAsync(() => AddLog($"Runtime pipe disconnected: {exception.Message}"));
            }
            catch (Exception exception)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SetDisconnectedState("Connection error", exception.Message);
                    AddLog($"ERROR: Runtime connection failed: {exception}");
                });
            }

            if (!_runtimeDesired)
            {
                return;
            }

            try
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ApplyStatus(RuntimeStatusMessage message)
    {
        if (message.ProtocolVersion != RuntimeProtocol.ProtocolVersion)
        {
            SetDisconnectedState(
                "Protocol mismatch",
                $"App protocol {RuntimeProtocol.ProtocolVersion}; runtime protocol {message.ProtocolVersion}.");
            return;
        }

        Brush stateBrush = BrushForState(message.State);
        string stateText = StateText(message.State);
        HeaderRuntimeStateText.Text = stateText;
        HeaderRuntimeStateText.Foreground = stateBrush;
        DashboardRuntimeStateText.Text = "Connected";
        DashboardRuntimeStateText.Foreground = (Brush)FindResource("SuccessBrush");
        DashboardRuntimePidText.Text = $"PID {message.RuntimeProcessId}";
        RuntimeStateText.Text = stateText;
        RuntimeStateText.Foreground = stateBrush;
        RuntimeProcessText.Text = $"Runtime PID {message.RuntimeProcessId}";
        RuntimeDetailText.Text = message.Detail;
        DashboardDetailText.Text = message.Detail;
        DashboardFighterStateText.Text = stateText;
        DashboardFighterStateText.Foreground = stateBrush;

        if (message.GameProcessId is int gameProcessId)
        {
            _currentGameProcessId = gameProcessId;
            DashboardGameStateText.Text = "Detected";
            DashboardGameStateText.Foreground = (Brush)FindResource("SuccessBrush");
            DashboardGamePidText.Text = $"PID {gameProcessId}";
            GameProcessText.Text = $"Game PID {gameProcessId}";
        }
        else
        {
            if (_hpTraceSession is not null)
                _ = DisarmHpTraceAsync("HP trace ended because DBXV2 exited.", "GameExited");
            _currentGameProcessId = null;
            DashboardGameStateText.Text = "Not detected";
            DashboardGameStateText.Foreground = (Brush)FindResource("WarningBrush");
            DashboardGamePidText.Text = "PID —";
            GameProcessText.Text = "Game PID —";
        }

        GameVersionText.Text = $"Game version {message.GameVersion ?? "—"}";
        PatcherDetailText.Text = message.PatcherDetected
            ? $"XV2 Patcher detected · image 0x{message.PatcherImageSize:X}"
            : "XV2 Patcher —";
        string coreText = message.BattleCoreAddress.HasValue
            ? $"BattleCore 0x{message.BattleCoreAddress.Value:X16} · stable {message.StableCoreSamples}/4"
            : $"BattleCore — · stable {message.StableCoreSamples}/4";
        BattleCoreDetailText.Text = coreText;
        DashboardCoreText.Text = coreText;

        RuntimeAccessGateText.Text = message.RuntimeAccess.ArchitectureGate +
            $" · read-only {message.RuntimeAccess.ExternalReadOnly} · injection {message.RuntimeAccess.InjectionUsed} · hooks {message.RuntimeAccess.HooksUsed} · writes {message.RuntimeAccess.GameWritesUsed}";
        LocatorDetailText.Text = $"BattleCore provider: {message.RuntimeAccess.ActiveLocatorId ?? "none"} · {message.RuntimeAccess.LocatorDetail}";
        MemoryAccessMetricsMessage observerAccess = message.RuntimeAccess.ObserverMetrics;
        MemoryAccessMetricsMessage chronologyAccess = message.RuntimeAccess.ChronologyMetrics;
        ReadBudgetText.Text =
            $"Observer: {observerAccess.ReadRequests:N0} requests / {observerAccess.ReadProcessMemoryCalls:N0} OS reads / " +
            $"{observerAccess.CompletedBytes:N0} bytes / {observerAccess.RejectedReadRequests:N0} rejected. Chronology: " +
            $"{chronologyAccess.ReadRequests:N0} requests / {chronologyAccess.ReadProcessMemoryCalls:N0} OS reads / " +
            $"{chronologyAccess.CompletedBytes:N0} bytes / {chronologyAccess.RejectedReadRequests:N0} rejected.";
        ComparisonPolicyMessage comparison = message.RuntimeAccess.ComparisonPolicy;
        ComparisonPolicyText.Text =
            $"Comparison policy {comparison.PolicyId}: absolute {comparison.AbsoluteTolerance:G3}, relative {comparison.RelativeTolerance:G3}. {comparison.RawChronologyPolicy}";

        ReconcileFighters(message.Fighters, message.MonotonicTicks);
        DashboardFighterCountText.Text = $"{message.Fighters.Count} fighter{(message.Fighters.Count == 1 ? string.Empty : "s")}";
        FighterSummaryText.Text = message.BattleCoreAddress.HasValue
            ? $"{message.Fighters.Count} active fighter object(s) · {coreText}"
            : message.Detail;

        ApplyChronologyStatus(message.Chronology);
        if (message.ChronologySamples.Count > 0)
        {
            _chronologyDisplayRows.InsertRange(0, message.ChronologySamples
                .OrderByDescending(sample => sample.Sequence)
                .Select(ChronologySampleRow.FromSample));
            if (_chronologyDisplayRows.Count > 1000)
            {
                _chronologyDisplayRows.RemoveRange(1000, _chronologyDisplayRows.Count - 1000);
            }
            ChronologyRows.ReplaceAll(_chronologyDisplayRows);
        }

        foreach (TelemetryEventMessage telemetryEvent in message.Events)
        {
            if (telemetryEvent.Kind is TelemetryEventKind.FighterAcquired or
                TelemetryEventKind.FighterReleased or
                TelemetryEventKind.ValueObserved or
                TelemetryEventKind.ValueChanged or
                TelemetryEventKind.Snapshot)
            {
                AddEventRow(telemetryEvent);
            }

            if (telemetryEvent.Kind is TelemetryEventKind.FighterAcquired or TelemetryEventKind.FighterReleased)
            {
                AddLog(telemetryEvent.Label);
            }
        }

        string heartbeat = $"Heartbeat {message.HeartbeatSequence:N0} · {message.TimestampUtc.ToLocalTime():T}";
        DashboardHeartbeatText.Text = heartbeat;
        RuntimeHeartbeatText.Text = heartbeat;
        FooterHeartbeatText.Text = heartbeat;
    }

    private void ApplyProbeStatus(ProbeStatusMessage message)
    {
        _lastProbeStatus = message;
        _transportDropped = message.DroppedNativeEventCount;
        UpdateTransportMetrics();
        if (message.ProtocolVersion != ProbeProtocol.ProtocolVersion ||
            message.NativeAbiVersion != ProbeProtocol.NativeAbiVersion)
        {
            ProbeStateText.Text = "Protocol mismatch";
            ProbeDetailText.Text = $"App probe protocol {ProbeProtocol.ProtocolVersion}/{ProbeProtocol.NativeAbiVersion}; host {message.ProtocolVersion}/{message.NativeAbiVersion}.";
            ProbeStateText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        Brush brush = message.State switch
        {
            ProbeState.Ready => (Brush)FindResource("SuccessBrush"),
            ProbeState.Faulted => (Brush)FindResource("DangerBrush"),
            _ => (Brush)FindResource("WarningBrush")
        };
        string state = message.State == ProbeState.Idle ? "Not attached" : message.State.ToString();
        DashboardProbeStateText.Text = state;
        DashboardProbeStateText.Foreground = brush;
        DashboardProbeDetailText.Text = message.GameProcessId is int gamePid
            ? $"Game PID {gamePid}"
            : $"Host PID {message.HostProcessId}";
        ProbeStateText.Text = state;
        ProbeStateText.Foreground = brush;
        ProbeDetailText.Text = message.Detail;
        ProbeIdentityText.Text =
            $"Host PID {message.HostProcessId} · Game PID {(message.GameProcessId?.ToString() ?? "—")} · ABI {message.NativeAbiVersion} · session {message.SessionId ?? "—"}";
        ProbeHeartbeatText.Text =
            $"Host heartbeat {message.HeartbeatSequence:N0} · native QPC {message.NativeHeartbeatMonotonicTicks:N0} · dropped {message.DroppedNativeEventCount:N0} · " +
            $"watchpoints {message.ActiveWatchpointCount:N0} · eligible {message.EligibleThreadCount:N0} · instrumented {message.InstrumentedThreadCount:N0} · " +
            $"new {message.NewlyArmedThreadCount:N0} · exited {message.ExitedThreadCount:N0} · conflicts {message.ConflictThreadCount:N0}";
        AttachProbeButton.IsEnabled = message.State is ProbeState.Idle or ProbeState.Faulted && !message.ProbeDllLoaded;
        DetachProbeButton.IsEnabled = message.ProbeDllLoaded;
        _probeReady = message.State == ProbeState.Ready;
        if (_hpTraceSession is not null && message.State is ProbeState.Faulted or ProbeState.Disconnected or ProbeState.Idle)
        {
            string reason = message.Detail.Contains("heartbeat", StringComparison.OrdinalIgnoreCase)
                ? "HeartbeatLoss"
                : message.State == ProbeState.Faulted ? "CoverageFault" : "ProbeDisconnected";
            _ = DisarmHpTraceAsync($"HP trace ended because the probe entered {message.State}: {message.Detail}", reason);
        }
        ArmHpTraceButton.IsEnabled = _probeReady && _hpTraceSession is null && HpTraceFighterList.SelectedItem is FighterRow;
        DisarmHpTraceButton.IsEnabled = _probeReady || message.ActiveWatchpointCount > 0 || _hpTraceSession is not null;
        SequentialTraceButton.IsEnabled = _hpTraceSession is null;
        WraparoundTraceButton.IsEnabled = _hpTraceSession is null;
        OverflowTraceButton.IsEnabled = _hpTraceSession is null;
        UpdateHpTracePresentation();
    }

    private void ApplyProbeEvent(ProbeEventMessage traceEvent)
    {
        if (traceEvent.EventType == ProbeEventType.InstrumentationFault)
        {
            HpTraceStateText.Text = $"Trace fault: complete thread coverage was lost (native {traceEvent.Registers[0]} / cleanup {traceEvent.Registers[1]}).";
            CompleteHpTraceSession(_hpTraceSession, "CoverageFault", false, HpTraceStateText.Text);
            ArmHpTraceButton.IsEnabled = false;
            DisarmHpTraceButton.IsEnabled = false;
            AddLog(HpTraceStateText.Text);
        }
        if (traceEvent.EventType == ProbeEventType.HardwareWriteTrap &&
            _hpTraceSession is HpWriteTraceSession hpTrace &&
            traceEvent.TraceSessionId == hpTrace.TraceSessionId && traceEvent.WatchId == hpTrace.WatchId)
        {
            _hpTraceCapturedEventCount++;
            LogHardwareWriteTrap(traceEvent, hpTrace);
            DetectHpSubtractionAndScheduleAutoDisarm(traceEvent, hpTrace);
            HpTraceTrapBanner.Visibility = Visibility.Visible;
            HpTraceTrapDetailText.Text =
                $"Sequence {traceEvent.Sequence:N0} · QPC {traceEvent.MonotonicTicks:N0} · Thread {traceEvent.ThreadId:N0}\n" +
                $"Watch {traceEvent.WatchId:N0} · Target Battle_Mob + 0x{hpTrace.TargetOffset:X}\n{traceEvent.Origin}";
        }
        if (traceEvent.ThreadId <= 0 || (traceEvent.EventType == ProbeEventType.Synthetic && traceEvent.Origin != "NativeProbe")) _transportMalformed++;
        if (!_transportSequences.Add(traceEvent.Sequence)) _transportDuplicates++;
        if (_lastTransportSequence != 0 && traceEvent.Sequence > _lastTransportSequence + 1)
            _transportMissing += checked((long)(traceEvent.Sequence - _lastTransportSequence - 1));
        if (traceEvent.Sequence > _lastTransportSequence) _lastTransportSequence = traceEvent.Sequence;
        _receivedByTraceSession.TryGetValue(traceEvent.TraceSessionId, out int traceCount);
        _receivedByTraceSession[traceEvent.TraceSessionId] = traceCount + 1;
        string rcx = CorrelateFighter(traceEvent.Registers.Count > 2 ? traceEvent.Registers[2] : 0, traceEvent.MonotonicTicks);
        string rdx = CorrelateFighter(traceEvent.Registers.Count > 3 ? traceEvent.Registers[3] : 0, traceEvent.MonotonicTicks);
        ProbeTraceRows.Add(new(traceEvent.Sequence, traceEvent.EventType.ToString(), traceEvent.MonotonicTicks,
            traceEvent.ThreadId, traceEvent.TraceSessionId, traceEvent.WatchId,
            traceEvent.EventType == ProbeEventType.HardwareWriteTrap ? traceEvent.Origin : "—", rcx, rdx));
        while (ProbeTraceRows.Count > 2_000) ProbeTraceRows.RemoveAt(0);
        UpdateTransportMetrics();
        UpdateHpTracePresentation();
    }

    private void UpdateTransportMetrics() => TransportMetricsText.Text =
        $"requested {_transportRequested:N0} · acknowledged {_transportAcknowledged:N0} · received {_transportSequences.Count:N0} · " +
        $"missing {_transportMissing:N0} · duplicate {_transportDuplicates:N0} · malformed {_transportMalformed:N0} · dropped {_transportDropped:N0}";

    private void ApplyChronologyStatus(ChronologyStatusMessage chronology)
    {
        Brush brush = chronology.SamplingActive
            ? (Brush)FindResource("SuccessBrush")
            : chronology.Enabled
                ? (Brush)FindResource("WarningBrush")
                : (Brush)FindResource("SecondaryTextBrush");
        ChronologyStatusText.Text = chronology.Detail;
        ChronologyStatusText.Foreground = brush;
        ChronologyMetricsText.Text =
            $"epoch {chronology.Epoch:N0} · {chronology.WatchedTargetCount:N0} anchors · {chronology.Configuration.IntervalMs:N0} ms · " +
            $"samples {chronology.EpochEmittedSampleCount:N0} ({chronology.EpochChangedSampleCount:N0} changed) · " +
            $"queue {chronology.PendingSampleCount:N0} · dropped {chronology.EpochDroppedSampleCount:N0} · " +
            $"poll {chronology.LastPollDurationMilliseconds:F2} ms / max {chronology.EpochMaximumPollDurationMilliseconds:F2} ms";
    }

    private void ReconcileFighters(IReadOnlyList<FighterSnapshot> fighters, long nowQpc)
    {
        HashSet<int> activeSlots = fighters.Select(fighter => fighter.Slot).ToHashSet();
        foreach (int staleSlot in _fighterBySlot.Keys.Where(slot => !activeSlots.Contains(slot)).ToArray())
        {
            FighterRow row = _fighterBySlot[staleSlot];
            ReleaseFighterLifetime(row.IdentityKey, nowQpc);
            if (_hpTraceSession?.IdentityKey == row.IdentityKey)
                _ = DisarmHpTraceAsync(
                    $"HP trace stopped: selected fighter generation released. Slot {row.Slot}, Generation {row.SlotGeneration}, Actor 0x{row.ActorAddress:X16}. Trace was automatically disarmed.",
                    "TargetGenerationReleased");
            FighterRows.Remove(row);
            _fighterBySlot.Remove(staleSlot);
        }

        foreach (FighterSnapshot fighter in fighters.OrderBy(fighter => fighter.Slot))
        {
            if (!_fighterBySlot.TryGetValue(fighter.Slot, out FighterRow? row))
            {
                row = new FighterRow(fighter.Slot);
                _fighterBySlot.Add(fighter.Slot, row);
                int insertIndex = FighterRows.TakeWhile(existing => existing.Slot < fighter.Slot).Count();
                FighterRows.Insert(insertIndex, row);
            }
            else if (!string.IsNullOrWhiteSpace(row.IdentityKey) && row.IdentityKey != fighter.Identity.IdentityKey)
            {
                ReleaseFighterLifetime(row.IdentityKey, nowQpc);
                if (_hpTraceSession?.IdentityKey == row.IdentityKey)
                    _ = DisarmHpTraceAsync(
                        $"HP trace stopped: selected fighter generation released. Slot {row.Slot}, Generation {row.SlotGeneration}, Actor 0x{row.ActorAddress:X16}. Trace was automatically disarmed.",
                        "TargetGenerationReleased");
            }
            if (_fighterLifetimes.All(lifetime => lifetime.IdentityKey != fighter.Identity.IdentityKey))
                _fighterLifetimes.Add(new FighterLifetime
                {
                    ActorAddress = fighter.ActorAddress, Slot = fighter.Slot, Generation = fighter.Identity.SlotGeneration,
                    BattleInstanceId = fighter.Identity.BattleInstanceId, IdentityKey = fighter.Identity.IdentityKey,
                    AcquiredQpc = fighter.Identity.FirstSeenMonotonicTicks
                });
            row.Update(fighter);
        }
        UpdateHpTracePresentation();
    }

    private void ReleaseFighterLifetime(string identityKey, long releasedQpc)
    {
        FighterLifetime? lifetime = _fighterLifetimes.LastOrDefault(item => item.IdentityKey == identityKey && item.ReleasedQpc is null);
        if (lifetime is not null) lifetime.ReleasedQpc = releasedQpc;
        while (_fighterLifetimes.Count > 256) _fighterLifetimes.RemoveAt(0);
    }

    private string CorrelateFighter(ulong address, long eventQpc)
    {
        if (address == 0) return "—";
        FighterLifetime? lifetime = _fighterLifetimes.LastOrDefault(item => item.ActorAddress == address &&
            item.AcquiredQpc <= eventQpc && (item.ReleasedQpc is null || eventQpc <= item.ReleasedQpc));
        return lifetime is null ? "—" : $"Slot {lifetime.Slot} / Gen {lifetime.Generation}";
    }

    private void AddEventRow(TelemetryEventMessage telemetryEvent)
    {
        EventRows.Insert(0, SessionEventRow.FromTelemetry(telemetryEvent));
        while (EventRows.Count > 500)
        {
            EventRows.RemoveAt(EventRows.Count - 1);
        }
    }

    private async Task StopRuntimeAsync()
    {
        _runtimeDesired = false;
        await SendCommandAsync(new RuntimeCommand("shutdown")).ConfigureAwait(true);
        AddLog("Shutdown command sent to external research runtime.");

        if (_runtimeProcess is not null)
        {
            try
            {
                await _runtimeProcess.WaitForExitAsync(_windowLifetime.Token)
                    .WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                AddLog("Runtime did not exit within four seconds. It was left running rather than force-terminated.");
            }
            catch (OperationCanceledException)
            {
                // Window is closing.
            }
        }

        await DisconnectPipeAsync().ConfigureAwait(true);
        _runtimeProcess?.Dispose();
        _runtimeProcess = null;
        SetDisconnectedState("Offline", "The external research runtime is stopped.");
    }

    private async Task SendCommandAsync(RuntimeCommand command)
    {
        StreamWriter? writer = _pipeWriter;
        if (writer is null)
        {
            AddLog("No runtime pipe is connected; the command was not sent.");
            return;
        }

        await _pipeWriteLock.WaitAsync().ConfigureAwait(true);
        try
        {
            string json = JsonSerializer.Serialize(command, JsonOptions);
            await writer.WriteLineAsync(json).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            AddLog($"Runtime command failed: {exception.Message}");
        }
        finally
        {
            _pipeWriteLock.Release();
        }
    }

    private async Task DisconnectPipeAsync()
    {
        await _pipeWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _pipeWriter?.Dispose();
            _pipeWriter = null;
            _pipe?.Dispose();
            _pipe = null;
        }
        finally
        {
            _pipeWriteLock.Release();
        }
    }

    private void SetConnectingState(string detail)
    {
        Brush warning = (Brush)FindResource("WarningBrush");
        HeaderRuntimeStateText.Text = "Connecting";
        HeaderRuntimeStateText.Foreground = warning;
        DashboardRuntimeStateText.Text = "Connecting";
        DashboardRuntimeStateText.Foreground = warning;
        RuntimeStateText.Text = "Connecting";
        RuntimeStateText.Foreground = warning;
        DashboardFighterStateText.Text = "Connecting";
        DashboardFighterStateText.Foreground = warning;
        DashboardDetailText.Text = detail;
        RuntimeDetailText.Text = detail;
        ChronologyStatusText.Text = "Chronology sampler is connecting.";
        ChronologyStatusText.Foreground = warning;
        ChronologyMetricsText.Text = "Waiting for chronology diagnostics.";
    }

    private void SetDisconnectedState(string state, string detail)
    {
        if (_hpTraceSession is not null)
            _ = DisarmHpTraceAsync($"HP trace ended because passive Runtime disconnected: {detail}", "RuntimeDisconnected");
        Brush warning = (Brush)FindResource("WarningBrush");
        HeaderRuntimeStateText.Text = state;
        HeaderRuntimeStateText.Foreground = warning;
        DashboardRuntimeStateText.Text = state;
        DashboardRuntimeStateText.Foreground = warning;
        DashboardRuntimePidText.Text = "PID —";
        RuntimeStateText.Text = state;
        RuntimeStateText.Foreground = warning;
        RuntimeProcessText.Text = "Runtime PID —";
        DashboardFighterStateText.Text = state;
        DashboardFighterStateText.Foreground = warning;
        DashboardDetailText.Text = detail;
        RuntimeDetailText.Text = detail;
        DashboardGameStateText.Text = "Not detected";
        _currentGameProcessId = null;
        DashboardGameStateText.Foreground = warning;
        DashboardGamePidText.Text = "PID —";
        GameProcessText.Text = "Game PID —";
        GameVersionText.Text = "Game version —";
        PatcherDetailText.Text = "XV2 Patcher —";
        BattleCoreDetailText.Text = "BattleCore —";
        DashboardCoreText.Text = "BattleCore —";
        RuntimeAccessGateText.Text = "Runtime access architecture —";
        LocatorDetailText.Text = "BattleCore provider —";
        ReadBudgetText.Text = "Read budget —";
        ComparisonPolicyText.Text = "Comparison policy —";
        DashboardFighterCountText.Text = "0 fighters";
        FighterSummaryText.Text = detail;
        ChronologyStatusText.Text = detail;
        ChronologyStatusText.Foreground = warning;
        ChronologyMetricsText.Text = "No chronology connection.";
        FooterHeartbeatText.Text = "Heartbeat —";
        DashboardHeartbeatText.Text = "Last heartbeat —";
        RuntimeHeartbeatText.Text = "Heartbeat —";
        _fighterBySlot.Clear();
        FighterRows.Clear();
    }

    private void TrySendShutdownSynchronously()
    {
        StreamWriter? writer = _pipeWriter;
        if (writer is null)
        {
            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(new RuntimeCommand("shutdown"), JsonOptions);
            writer.WriteLine(json);
            writer.Flush();
        }
        catch (IOException)
        {
            // Runtime already disconnected.
        }
        catch (ObjectDisposedException)
        {
            // Pipe already closing.
        }
    }

    private void AddLog(string message)
    {
        string line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
        LogList.Items.Add(line);
        while (LogList.Items.Count > 1000)
        {
            LogList.Items.RemoveAt(0);
        }
        LogList.ScrollIntoView(line);
        try
        {
            File.AppendAllText(_appLogPath, line + Environment.NewLine);
        }
        catch
        {
            // On-screen diagnostics remain available.
        }
    }

    private void ShowPage(FrameworkElement page, Button selectedButton, string title, string subtitle)
    {
        FrameworkElement[] pages = [DashboardPage, RuntimePage, FightersPage, ResearchPage, FindingsPage, LogsPage, ToolsPage];
        Button[] buttons = [DashboardNavButton, RuntimeNavButton, FightersNavButton, ResearchNavButton, FindingsNavButton, LogsNavButton, ToolsNavButton];
        foreach (FrameworkElement candidatePage in pages)
        {
            candidatePage.Visibility = candidatePage == page ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (Button button in buttons)
        {
            button.Tag = button == selectedButton ? "Selected" : null;
        }
        PageTitleText.Text = title;
        PageSubtitleText.Text = subtitle;
    }

    private static string StateText(RuntimeState state) => state switch
    {
        RuntimeState.WaitingForGame => "Waiting for game",
        RuntimeState.ReadPermissionGranted => "Read access ready",
        RuntimeState.WaitingForPatcher => "Waiting for patcher",
        RuntimeState.WaitingForBattleCore => "Waiting for BattleCore",
        RuntimeState.WaitingForFighters => "Waiting for fighters",
        RuntimeState.ObservingFighters => "Observing fighters",
        RuntimeState.ScanningCapabilities => "Observing fighters",
        RuntimeState.ReadPermissionDenied => "Access denied",
        _ => state.ToString()
    };

    private Brush BrushForState(RuntimeState state) => state switch
    {
        RuntimeState.ObservingFighters or RuntimeState.ScanningCapabilities or RuntimeState.WaitingForFighters or RuntimeState.ReadPermissionGranted =>
            (Brush)FindResource("SuccessBrush"),
        RuntimeState.ReadPermissionDenied or RuntimeState.Error =>
            (Brush)FindResource("DangerBrush"),
        _ => (Brush)FindResource("WarningBrush")
    };

    private void DashboardNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(DashboardPage, DashboardNavButton, "Dashboard", "Live fighter state and causal-research readiness");

    private void RuntimeNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(RuntimePage, RuntimeNavButton, "Runtime", "External read-only observer, BattleCore discovery, and access diagnostics");

    private void FightersNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(FightersPage, FightersNavButton, "Fighters", "Live Battle_Mob registry with generation-safe instance tracking");

    private void ResearchNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(ResearchPage, ResearchNavButton, "Research", "Explicit native probe lifecycle with chronology as supporting evidence");

    private void FindingsNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(FindingsPage, FindingsNavButton, "Findings", "Durable anchors only — no candidate-tier or noise-ranking workflow");

    private void LogsNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(LogsPage, LogsNavButton, "Diagnostics", "Fighter lifetime, known-field, app, and runtime connection events");

    private void ToolsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(ToolsPage, ToolsNavButton, "Tools", "Sealed companion utilities kept outside the PowerScaler research workflow");
        RefreshHealthScaleCompanion();
    }

    private async void StartRuntimeButton_Click(object sender, RoutedEventArgs e) =>
        await StartRuntimeAsync().ConfigureAwait(true);

    private async void StopRuntimeButton_Click(object sender, RoutedEventArgs e) =>
        await StopRuntimeAsync().ConfigureAwait(true);

    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _runtimeDesired = true;
        await DisconnectPipeAsync().ConfigureAwait(true);
        AddLog("Runtime pipe reconnect requested.");
        EnsureConnectionLoop();
    }

    private async void AttachProbeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGameProcessId is not int gameProcessId)
        {
            AddLog("Attach Probe requires a currently detected DBXV2 PID from the passive Runtime.");
            return;
        }
        ProbeCommandResult result = await _probeClient.SendAsync(_probeClient.CreateCommand("attach", gameProcessId)).ConfigureAwait(true);
        AddLog(result.Success ? $"Probe attached to DBXV2 PID {gameProcessId}." : $"Probe attach failed: {result.Detail}");
    }

    private async void DetachProbeButton_Click(object sender, RoutedEventArgs e)
    {
        ProbeCommandResult result = await _probeClient.SendAsync(_probeClient.CreateCommand("detach")).ConfigureAwait(true);
        AddLog(result.Success ? "Probe detach and module removal confirmed." : $"Probe detach unresolved: {result.Detail}");
    }

    private void HpTraceFighterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_hpTraceSession is not null)
        {
            HpTraceFighterList.SelectedItem = _fighterBySlot.TryGetValue(_hpTraceSession.Slot, out FighterRow? armedRow) &&
                armedRow.IdentityKey == _hpTraceSession.IdentityKey ? armedRow : null;
            return;
        }
        UpdateHpTracePresentation();
    }

    private async void ArmHpTraceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_probeReady || HpTraceFighterList.SelectedItem is not FighterRow selected ||
            !_fighterBySlot.TryGetValue(selected.Slot, out FighterRow? live) || live.IdentityKey != selected.IdentityKey)
        {
            HpTraceStateText.Text = "Arm rejected: select a currently live fighter generation while Probe is Ready.";
            return;
        }
        if (!float.IsFinite(live.CurrentHealth) || !float.IsFinite(live.MaximumHealth) || live.MaximumHealth <= 0)
        {
            HpTraceStateText.Text = "Arm rejected: validated HP evidence is unavailable for the selected fighter generation.";
            AddLog(HpTraceStateText.Text);
            return;
        }
        ulong traceSessionId = unchecked((ulong)Interlocked.Increment(ref _nextTraceId));
        ulong watchId = unchecked((ulong)Interlocked.Increment(ref _nextTraceId));
        ulong targetAddress = checked(selected.ActorAddress + RuntimeProtocol.CurrentHealthOffset);
        string stimulus = HpTraceStimulusCombo.SelectedItem?.ToString() ?? "Unspecified";
        HpWriteTraceSession pendingTrace = new(traceSessionId, watchId, selected.ActorAddress, selected.Slot,
            selected.SlotGeneration, selected.BattleInstanceId, selected.IdentityKey, targetAddress,
            RuntimeProtocol.CurrentHealthOffset, selected.CurrentHealth, selected.MaximumHealth, 0,
            Stopwatch.GetTimestamp(), stimulus);
        ProbeCommand command = _probeClient.CreateCommand("arm_write_watch", traceSessionId: traceSessionId,
            watchId: watchId, address: targetAddress, width: 4, accessType: ProbeAccessTypes.Write,
            simdRegister0: 0, simdRegister1: 6);
        ProbeCommandResult result = await _probeClient.SendAsync(command, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        if (result.Success)
        {
            HpWriteTraceSession trace = pendingTrace with { InstrumentedThreadCount = result.GeneratedEventCount };
            _hpTraceSession = trace;
            _lastHpTraceSession = trace;
            _hpTraceEndDetail = string.Empty;
            _hpTraceCapturedEventCount = 0;
            _hpWriterEvidence.Clear();
            _hpTraceLastDetectedHealth = trace.CurrentHealthAtArm;
            _hpTraceDetectedSubtractionCount = 0;
            CancelHpAutoDisarm();
            _hpTraceSummaryWritten = false;
            HpTraceTrapBanner.Visibility = Visibility.Collapsed;
            HpTraceStateText.Text = $"Armed · DR0 write/4 · {result.GeneratedEventCount} threads · trace {traceSessionId} · watch {watchId}";
            ArmHpTraceButton.IsEnabled = false;
            DisarmHpTraceButton.IsEnabled = true;
            HpTraceFighterList.IsEnabled = false;
            NormalTraceButton.IsEnabled = false;
            SequentialTraceButton.IsEnabled = false;
            WraparoundTraceButton.IsEnabled = false;
            OverflowTraceButton.IsEnabled = false;
        }
        else HpTraceStateText.Text = $"Arm failed: {result.Detail}";
        AddLog(HpTraceStateText.Text);
        if (result.Success) LogHpTraceArmed(_hpTraceSession!);
        UpdateHpTracePresentation();
    }

    private async void DisarmHpTraceButton_Click(object sender, RoutedEventArgs e) =>
        await DisarmHpTraceAsync("HP write trace disarmed by user.", "ManualDisarm").ConfigureAwait(true);

    private async Task DisarmHpTraceAsync(string successDetail, string endReason = "AutomaticDisarm")
    {
        if (_hpTraceDisarmPending || (!_probeReady && _hpTraceSession is null && (_lastProbeStatus?.ActiveWatchpointCount ?? 0) == 0)) return;
        _hpTraceDisarmPending = true;
        HpWriteTraceSession? endingTrace = _hpTraceSession;
        ProbeCommandResult result = await _probeClient.SendAsync(
            _probeClient.CreateCommand("disarm_watch"), TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        if (result.Success)
        {
            CompleteHpTraceSession(endingTrace, endReason, true, result.Detail);
            HpTraceStateText.Text = successDetail;
            ArmHpTraceButton.IsEnabled = _probeReady && HpTraceFighterList.SelectedItem is FighterRow;
            DisarmHpTraceButton.IsEnabled = false;
            HpTraceFighterList.IsEnabled = true;
            NormalTraceButton.IsEnabled = true;
            SequentialTraceButton.IsEnabled = true;
            WraparoundTraceButton.IsEnabled = true;
            OverflowTraceButton.IsEnabled = true;
        }
        else
        {
            HpTraceStateText.Text = $"Disarm failed; safe cleanup requested: {result.Detail}";
            CompleteHpTraceSession(endingTrace, endReason, false, result.Detail);
            _ = await _probeClient.SendAsync(_probeClient.CreateCommand("shutdown"), TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        }
        _hpTraceDisarmPending = false;
        AddLog(HpTraceStateText.Text);
        UpdateHpTracePresentation();
    }

    private void LogHpTraceArmed(HpWriteTraceSession trace)
    {
        AddLog(
            $"HP TRACE ARMED\n" +
            $"Slot: {trace.Slot}\nGeneration: {trace.SlotGeneration}\nBattleInstanceId: {trace.BattleInstanceId}\nIdentityKey: {trace.IdentityKey}\n" +
            $"ActorAddress: 0x{trace.ActorAddress:X16}\nCurrentHealthOffset: 0x{trace.TargetOffset:X}\nWatchedAddress: 0x{trace.TargetAddress:X16}\n" +
            $"HP at arm: {trace.CurrentHealthAtArm:G9}\nMax HP: {trace.MaximumHealthAtArm:G9}\n" +
            $"Health percent: {(trace.CurrentHealthAtArm / trace.MaximumHealthAtArm):P2}\nArm QPC: {trace.StartedQpc}\n" +
            $"TraceSessionId: {trace.TraceSessionId}\nWatchId: {trace.WatchId}\n" +
            $"Stimulus: {trace.Stimulus}\n" +
            $"Eligible threads: {trace.InstrumentedThreadCount}\n" +
            $"Instrumented threads: {trace.InstrumentedThreadCount}\n" +
            $"Conflict threads: 0");
    }

    private void LogHardwareWriteTrap(ProbeEventMessage traceEvent, HpWriteTraceSession trace)
    {
        RecordWriterEvidence(traceEvent);
        static ulong Register(IReadOnlyList<ulong> registers, int index) => index < registers.Count ? registers[index] : 0;
        string[] names = ["RAX", "RBX", "RCX", "RDX", "RSI", "RDI", "RBP", "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15"];
        string registerText = string.Join("\n", names.Select((name, index) =>
            $"{name}: 0x{Register(traceEvent.Registers, index):X16}"));
        string correlations =
            $"RCX -> {CorrelateFighter(Register(traceEvent.Registers, 2), traceEvent.MonotonicTicks)}\n" +
            $"RDX -> {CorrelateFighter(Register(traceEvent.Registers, 3), traceEvent.MonotonicTicks)}";
        AddLog(
            $"HARDWARE WRITE TRAP\nSequence: {traceEvent.Sequence}\nTraceSessionId: {traceEvent.TraceSessionId}\nWatchId: {traceEvent.WatchId}\n" +
            $"ThreadId: {traceEvent.ThreadId}\nTrapRip: 0x{traceEvent.TrapRip:X16}\nNormalizedTrapRip / code context: {traceEvent.Origin}\n" +
            $"WatchedAddress: 0x{trace.TargetAddress:X16}\nAccessWidth: {traceEvent.AccessWidth}\nAccessType: Write\n" +
            $"XMM{traceEvent.SimdRegister0}.scalar: {BitConverter.Int32BitsToSingle(unchecked((int)traceEvent.SimdScalarBits0)):G9} " +
            $"(0x{traceEvent.SimdScalarBits0:X8})\n" +
            $"XMM{traceEvent.SimdRegister1}.scalar: {BitConverter.Int32BitsToSingle(unchecked((int)traceEvent.SimdScalarBits1)):G9} " +
            $"(0x{traceEvent.SimdScalarBits1:X8})\n" +
            $"DR6: 0x{traceEvent.Dr6:X16}\nDR7: 0x{traceEvent.Dr7:X16}\n{registerText}\nFighter correlations:\n{correlations}");
    }

    private void RecordWriterEvidence(ProbeEventMessage traceEvent)
    {
        string key = traceEvent.Origin;
        if (!_hpWriterEvidence.TryGetValue(key, out WriterEvidence? evidence))
        {
            evidence = new WriterEvidence
            {
                Origin = traceEvent.Origin,
                TrapRip = traceEvent.TrapRip,
                FirstScalar0Bits = traceEvent.SimdScalarBits0,
                FirstScalar1Bits = traceEvent.SimdScalarBits1
            };
            _hpWriterEvidence.Add(key, evidence);
        }
        evidence.Count++;
        evidence.LastScalar0Bits = traceEvent.SimdScalarBits0;
        evidence.LastScalar1Bits = traceEvent.SimdScalarBits1;
        evidence.Scalar0BitCounts[traceEvent.SimdScalarBits0] =
            evidence.Scalar0BitCounts.GetValueOrDefault(traceEvent.SimdScalarBits0) + 1;
    }

    private void DetectHpSubtractionAndScheduleAutoDisarm(ProbeEventMessage traceEvent, HpWriteTraceSession trace)
    {
        float resultingHealth = BitConverter.Int32BitsToSingle(unchecked((int)traceEvent.SimdScalarBits0));
        float subtraction = BitConverter.Int32BitsToSingle(unchecked((int)traceEvent.SimdScalarBits1));
        float referenceHealth = _hpTraceLastDetectedHealth ?? trace.CurrentHealthAtArm;
        float reconstructedHealth = resultingHealth + subtraction;
        float tolerance = Math.Max(0.01f, Math.Abs(referenceHealth) * 0.0002f);
        if (!float.IsFinite(resultingHealth) || !float.IsFinite(subtraction) ||
            resultingHealth <= 0 || subtraction <= 0 || resultingHealth >= referenceHealth ||
            Math.Abs(reconstructedHealth - referenceHealth) > tolerance)
        {
            return;
        }

        _hpTraceLastDetectedHealth = resultingHealth;
        _hpTraceDetectedSubtractionCount++;
        AddLog($"HP subtraction detected: {referenceHealth:G9} - {subtraction:G9} = {resultingHealth:G9}; " +
            $"auto-disarm quiet timer restarted (event {_hpTraceDetectedSubtractionCount}).");
        if (HpTraceAutoDisarmCheckBox.IsChecked != true) return;

        CancelHpAutoDisarm();
        _hpAutoDisarmCancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _ = AutoDisarmAfterDamageQuietPeriodAsync(trace.TraceSessionId, trace.WatchId, _hpAutoDisarmCancellation.Token);
    }

    private async Task AutoDisarmAfterDamageQuietPeriodAsync(
        ulong traceSessionId, ulong watchId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(true);
            if (_hpTraceSession is not { } trace || trace.TraceSessionId != traceSessionId || trace.WatchId != watchId)
                return;
            await DisarmHpTraceAsync(
                $"HP write trace auto-disarmed after {_hpTraceDetectedSubtractionCount} detected subtraction event(s).",
                "AutoDisarmAfterDamage").ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void CancelHpAutoDisarm()
    {
        _hpAutoDisarmCancellation?.Cancel();
        _hpAutoDisarmCancellation?.Dispose();
        _hpAutoDisarmCancellation = null;
    }

    private void CompleteHpTraceSession(
        HpWriteTraceSession? trace, string endReason, bool disarmSucceeded, string disarmDetail)
    {
        if (trace is null || _hpTraceSummaryWritten) return;
        CancelHpAutoDisarm();
        FighterRow? live = _fighterBySlot.TryGetValue(trace.Slot, out FighterRow? candidate) &&
            candidate.IdentityKey == trace.IdentityKey ? candidate : null;
        float? hpAtEnd = live?.CurrentHealth;
        float? maximumAtEnd = live?.MaximumHealth;
        float? delta = hpAtEnd - trace.CurrentHealthAtArm;
        ProbeStatusMessage? status = _lastProbeStatus;
        string classification = !delta.HasValue
            ? "Insufficient HP evidence to classify this trace."
            : delta.Value == 0
                ? _hpTraceDetectedSubtractionCount > 0
                    ? $"End-state HP returned to baseline after {_hpTraceDetectedSubtractionCount} captured subtraction event(s). " +
                      "Transient damage and later recovery are both present; causal writer evidence is retained."
                    : "No HP change observed on selected target. No hardware trap was required for this test."
                : _hpTraceCapturedEventCount > 0
                    ? "Selected fighter HP changed and at least one hardware write trap was captured. WATCHPOINT GATE FIRED."
                    : "Selected fighter HP changed without a captured hardware write trap. WATCHPOINT DIAGNOSTIC REQUIRED.";
        string hpEndText = hpAtEnd.HasValue
            ? $"{hpAtEnd.Value:G9} / {maximumAtEnd.GetValueOrDefault():G9}"
            : "unavailable - target generation released or passive Runtime disconnected";
        string deltaText = delta.HasValue ? delta.Value.ToString("+0.########;-0.########;0") : "unavailable";
        uint? finalHpBits = hpAtEnd.HasValue && delta.HasValue && delta.Value != 0
            ? unchecked((uint)BitConverter.SingleToInt32Bits(hpAtEnd.Value))
            : null;
        string writerSummary = string.Join("\n", _hpWriterEvidence.Values
            .OrderByDescending(evidence => finalHpBits.HasValue && evidence.Scalar0BitCounts.ContainsKey(finalHpBits.Value))
            .ThenBy(evidence => evidence.Count)
            .Select(evidence =>
            {
                int exactMatches = finalHpBits.HasValue
                    ? evidence.Scalar0BitCounts.GetValueOrDefault(finalHpBits.Value)
                    : 0;
                float first0 = BitConverter.Int32BitsToSingle(unchecked((int)evidence.FirstScalar0Bits));
                float last0 = BitConverter.Int32BitsToSingle(unchecked((int)evidence.LastScalar0Bits));
                float first1 = BitConverter.Int32BitsToSingle(unchecked((int)evidence.FirstScalar1Bits));
                float last1 = BitConverter.Int32BitsToSingle(unchecked((int)evidence.LastScalar1Bits));
                return $"RIP 0x{evidence.TrapRip:X16} | count {evidence.Count} | final-HP XMM0 matches {exactMatches} | " +
                    $"XMM0 {first0:G9}->{last0:G9} | XMM6 {first1:G9}->{last1:G9} | {evidence.Origin}";
            }));
        if (string.IsNullOrEmpty(writerSummary)) writerSummary = "No writer events captured.";
        AddLog(
            $"HP TRACE ENDED\nEndReason: {endReason}\nSlot: {trace.Slot}\nGeneration: {trace.SlotGeneration}\n" +
            $"BattleInstanceId: {trace.BattleInstanceId}\nStimulus: {trace.Stimulus}\nIdentityKey: {trace.IdentityKey}\nActorAddress: 0x{trace.ActorAddress:X16}\n" +
            $"WatchedAddress: 0x{trace.TargetAddress:X16}\nHP at arm: {trace.CurrentHealthAtArm:G9} / {trace.MaximumHealthAtArm:G9}\n" +
            $"HP at end: {hpEndText}\nHP delta: {deltaText}\nHardwareWriteTrap count: {_hpTraceCapturedEventCount}\n" +
            $"Detected subtraction events: {_hpTraceDetectedSubtractionCount}\n" +
            $"Eligible threads at end: {status?.EligibleThreadCount ?? 0}\nInstrumented threads at end: {status?.InstrumentedThreadCount ?? 0}\n" +
            $"Exited threads since arm: {status?.ExitedThreadCount ?? 0}\nNewly instrumented threads: {status?.NewlyArmedThreadCount ?? 0}\n" +
            $"Conflict threads: {status?.ConflictThreadCount ?? 0}\nDisarm result: {(disarmSucceeded ? "Success" : "Failed")}\n" +
            $"Disarm detail: {disarmDetail}\nHP TRACE RESULT:\n{classification}\nWRITER EVIDENCE SUMMARY:\n{writerSummary}");
        _lastHpTraceSession = trace;
        _hpTraceEndDetail = $"{endReason}: {classification}";
        _hpTraceSession = null;
        _hpTraceSummaryWritten = true;
    }

    private void UpdateHpTracePresentation()
    {
        HpWriteTraceSession? trace = _hpTraceSession ?? _lastHpTraceSession;
        FighterRow? selected = HpTraceFighterList.SelectedItem as FighterRow;
        if (_hpTraceSession is null && selected is not null)
        {
            ulong target = checked(selected.ActorAddress + RuntimeProtocol.CurrentHealthOffset);
            HpTraceTargetText.Text =
                $"✓ HP TRACE TARGET · Slot {selected.Slot} · Generation {selected.SlotGeneration} · Battle {selected.BattleInstanceId} · " +
                $"Actor 0x{selected.ActorAddress:X16} · HP {selected.CurrentHealth:N0} / {selected.MaximumHealth:N0} · " +
                $"Battle_Mob + 0x{RuntimeProtocol.CurrentHealthOffset:X} = 0x{target:X16}";
        }
        else if (_hpTraceSession is null && selected is null)
        {
            HpTraceTargetText.Text = $"Select a live fighter generation. Target: Battle_Mob + 0x{RuntimeProtocol.CurrentHealthOffset:X}.";
        }

        ArmHpTraceButton.IsEnabled = _probeReady && _hpTraceSession is null && selected is not null;
        HpTraceFighterList.IsEnabled = _hpTraceSession is null;
        if (trace is null)
        {
            HpTraceSummaryBanner.Visibility = Visibility.Collapsed;
            return;
        }

        bool isActive = _hpTraceSession is not null;
        FighterRow? live = _fighterBySlot.TryGetValue(trace.Slot, out FighterRow? candidate) &&
            candidate.IdentityKey == trace.IdentityKey ? candidate : null;
        float? currentHealth = live?.CurrentHealth;
        float? currentMaximum = live?.MaximumHealth;
        float? delta = currentHealth - trace.CurrentHealthAtArm;

        HpTraceSummaryBanner.Visibility = Visibility.Visible;
        HpTraceSummaryBanner.Background = (Brush)FindResource(isActive
            ? "TraceTargetBackgroundBrush"
            : "ReleasedTargetBackgroundBrush");
        HpTraceSummaryBanner.BorderBrush = (Brush)FindResource(isActive
            ? "TraceTargetBorderBrush"
            : "WarningBrush");
        HpTraceBannerTitleText.Text = isActive ? "HP WRITE TRACE — ARMED" : "HP TRACE ENDED";
        HpTraceBannerTargetText.Text =
            $"TARGET  Slot {trace.Slot} · Generation {trace.SlotGeneration} · Battle {trace.BattleInstanceId}\n" +
            $"Actor  0x{trace.ActorAddress:X16}\nWatching  0x{trace.TargetAddress:X16}  (Battle_Mob + 0x{trace.TargetOffset:X})\n" +
            $"Threads  eligible {(_lastProbeStatus?.EligibleThreadCount ?? 0):N0} · instrumented {(_lastProbeStatus?.InstrumentedThreadCount ?? trace.InstrumentedThreadCount):N0} · " +
            $"new {(_lastProbeStatus?.NewlyArmedThreadCount ?? 0):N0} · exited {(_lastProbeStatus?.ExitedThreadCount ?? 0):N0} · conflicts {(_lastProbeStatus?.ConflictThreadCount ?? 0):N0}\n" +
            $"Trace {trace.TraceSessionId:N0} · Watch {trace.WatchId:N0}";
        HpTraceBannerHealthText.Text =
            $"HP at arm  {trace.CurrentHealthAtArm:N0} / {trace.MaximumHealthAtArm:N0}\n" +
            (currentHealth.HasValue
                ? $"Current HP  {currentHealth:N0} / {currentMaximum:N0}\nDelta  {delta:+0;-0;0}"
                : "Current HP  unavailable · target generation is no longer live");

        string evidence = !string.IsNullOrWhiteSpace(_hpTraceEndDetail) && !isActive
            ? _hpTraceEndDetail
            : !delta.HasValue || delta.Value == 0
                ? $"HP events captured: {_hpTraceCapturedEventCount:N0} · HP unchanged. No trap expected yet."
                : _hpTraceCapturedEventCount > 0
                    ? $"HP events captured: {_hpTraceCapturedEventCount:N0} · HP changed. Trap captured."
                    : "HP events captured: 0 · HP changed without captured hardware event. Diagnostic review required.";
        HpTraceBannerResultText.Text = evidence;
        HpTraceBannerResultText.Foreground = (Brush)FindResource(
            delta.HasValue && delta.Value != 0 && _hpTraceCapturedEventCount == 0 ? "WarningBrush" : "PrimaryTextBrush");
    }

    private async void TestTraceTransportButton_Click(object sender, RoutedEventArgs e) => await RunTransportTestAsync("normal", 1, 0).ConfigureAwait(true);
    private async void SequentialTraceButton_Click(object sender, RoutedEventArgs e) => await RunTransportTestAsync("sequential-25", 25, 2).ConfigureAwait(true);
    private async void WraparoundTraceButton_Click(object sender, RoutedEventArgs e) => await RunTransportTestAsync("wraparound-512", 512, 2).ConfigureAwait(true);
    private async void OverflowTraceButton_Click(object sender, RoutedEventArgs e) => await RunOverflowTestAsync().ConfigureAwait(true);

    private async Task<TransportRunOutcome> RunTransportTestAsync(string name, int count, int intervalMilliseconds)
    {
        ulong traceSession = unchecked((ulong)DateTime.UtcNow.Ticks);
        ulong watchId = unchecked((ulong)(_transportRuns.Count + 1));
        ProbeCommandResult baseline = await _probeClient.SendAsync(_probeClient.CreateCommand("ping"), TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        long droppedBefore = baseline.DroppedNativeEventCount;
        _transportDropped = Math.Max(_transportDropped, droppedBefore);
        _transportRequested += count;
        UpdateTransportMetrics();
        ProbeCommand command = _probeClient.CreateCommand("emit_synthetic_event", traceSessionId: traceSession,
            watchId: watchId, eventCount: count, intervalMilliseconds: intervalMilliseconds);
        ProbeCommandResult result = await _probeClient.SendAsync(command, TimeSpan.FromSeconds(Math.Max(20, count * intervalMilliseconds / 1000 + 15))).ConfigureAwait(true);
        if (result.Success) _transportAcknowledged += result.GeneratedEventCount;
        _transportDropped = Math.Max(_transportDropped, result.DroppedNativeEventCount);
        (int received, long dropped, bool settled) = await WaitForTransportSettlementAsync(
            traceSession, droppedBefore, result.GeneratedEventCount, count >= 10_000 ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        long unaccounted = Math.Max(0, result.GeneratedEventCount - received - dropped);
        string state = !result.Success ? "Failed" : settled ? "Settled" : "TimedOutWithPendingAccounting";
        bool success = result.Success && settled && unaccounted == 0;
        string summary = $"{DateTimeOffset.Now:O} {name}: requested={count}, acknowledged={result.GeneratedEventCount}, received={received}, native_dropped={dropped}, unaccounted={unaccounted}, transport_state={state}, success={success}, detail={result.Detail}";
        _transportRuns.Add(summary);
        AddLog(summary);
        string gateName = name switch { "normal" => "Normal", "sequential-25" => "Sequential 25", "wraparound-512" => "Wraparound 512", _ => name };
        _transportGateResults[gateName] = success ? "PASS" : state;
        UpdateTransportGateResult();
        UpdateTransportMetrics();
        return new(name, count, result.GeneratedEventCount, received, dropped, unaccounted, state, success);
    }

    private async Task RunOverflowTestAsync()
    {
        TransportRunOutcome overflow = await RunTransportTestAsync("overflow-10000", 10_000, 0).ConfigureAwait(true);
        TransportRunOutcome recovery = await RunTransportTestAsync("post-overflow-recovery", 1, 0).ConfigureAwait(true);
        bool overflowPass = overflow.Acknowledged == overflow.Requested && overflow.Dropped > 0 &&
            overflow.Unaccounted == 0 && recovery.Success && _lastProbeStatus?.State == ProbeState.Ready;
        _transportGateResults["Overflow"] = overflowPass ? "PASS · drops expected" : overflow.State;
        _transportGateResults["Post-overflow recovery"] = recovery.Success ? "PASS" : recovery.State;
        UpdateTransportGateResult();
        AddLog($"Post-overflow recovery: {(recovery.Success ? "PASS" : "FAIL")}");
    }

    private async Task<(int Received, long Dropped, bool Settled)> WaitForTransportSettlementAsync(
        ulong traceSession, long droppedBefore, int acknowledged, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        DateTime lastChange = DateTime.UtcNow;
        int previousReceived = -1;
        long previousDropped = -1;
        while (DateTime.UtcNow < deadline)
        {
            _receivedByTraceSession.TryGetValue(traceSession, out int received);
            long dropped = Math.Max(0, _transportDropped - droppedBefore);
            if (received != previousReceived || dropped != previousDropped)
            {
                previousReceived = received;
                previousDropped = dropped;
                lastChange = DateTime.UtcNow;
            }
            bool accounted = received + dropped >= acknowledged;
            if (accounted && DateTime.UtcNow - lastChange >= TimeSpan.FromMilliseconds(500))
            {
                return (received, dropped, true);
            }
            await Task.Delay(100).ConfigureAwait(true);
        }
        _receivedByTraceSession.TryGetValue(traceSession, out int finalReceived);
        long finalDropped = Math.Max(0, _transportDropped - droppedBefore);
        return (finalReceived, finalDropped, false);
    }

    private void UpdateTransportGateResult()
    {
        string Value(string name) => _transportGateResults.TryGetValue(name, out string? value) ? value : "NOT RUN";
        TransportGateResultText.Text = $"LIVE TRANSPORT GATE · Normal: {Value("Normal")} · Sequential 25: {Value("Sequential 25")} · " +
            $"Wraparound 512: {Value("Wraparound 512")} · Overflow: {Value("Overflow")} · Post-overflow recovery: {Value("Post-overflow recovery")}";
    }

    private void ExportTransportReportButton_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(_logsDirectory, $"CausalTraceTransportGate_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        ProbeStatusMessage? status = _lastProbeStatus;
        string[] lines =
        [
            "PowerScaler Labs - Native Causal Trace Transport Gate Report",
            $"Generated: {DateTimeOffset.Now:O}",
            $"Game PID: {status?.GameProcessId?.ToString() ?? "not attached"}",
            $"Protocol/ABI: {ProbeProtocol.ProtocolVersion}/{ProbeProtocol.NativeAbiVersion}",
            $"State: {status?.State.ToString() ?? "disconnected"}",
            $"Handshake: {status?.NativeHandshakeEstablished ?? false}",
            $"Requested: {_transportRequested}", $"Acknowledged: {_transportAcknowledged}",
            $"Received: {_transportSequences.Count}", $"Missing: {_transportMissing}",
            $"Duplicate: {_transportDuplicates}", $"Malformed: {_transportMalformed}", $"Dropped: {_transportDropped}",
            $"Heartbeat continuity: {(status is null ? "NOT OBSERVED" : "OBSERVED AT EXPORT")}",
            "Detach/module removal: REQUIRED - perform the live detach gate",
            "Reattach result: REQUIRED - perform the live reattach gate",
            "Manual DBXV2 stability: REQUIRED - user confirmation has not been recorded",
            "",
            "Runs:",
            .. _transportRuns
        ];
        File.WriteAllLines(path, lines);
        AddLog($"Transport gate report exported: {path}");
    }

    private async void NewChronologyEpochButton_Click(object sender, RoutedEventArgs e)
    {
        await SendCommandAsync(new RuntimeCommand("new_chronology_epoch", "Manual causal-research epoch")).ConfigureAwait(true);
        AddLog("Requested a fresh chronology epoch.");
    }

    private async void PauseChronologyButton_Click(object sender, RoutedEventArgs e)
    {
        await SendCommandAsync(new RuntimeCommand("pause_chronology", "Research timeline paused")).ConfigureAwait(true);
        AddLog("Chronology pause requested.");
    }

    private async void ResumeChronologyButton_Click(object sender, RoutedEventArgs e)
    {
        await SendCommandAsync(new RuntimeCommand("resume_chronology", "Research timeline resumed")).ConfigureAwait(true);
        AddLog("Chronology resume requested.");
    }

    private void ClearChronologyViewButton_Click(object sender, RoutedEventArgs e)
    {
        _chronologyDisplayRows.Clear();
        ChronologyRows.ReplaceAll([]);
        AddLog("Chronology view cleared; runtime sampling state was not changed.");
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        LogList.Items.Clear();
        EventRows.Clear();
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => OpenDirectory(_logsDirectory, "logs folder");

    private void RefreshHealthScaleCompanion()
    {
        HealthScaleCompanionStatus status = _healthScaleCompanion.Refresh();
        ApplyHealthScaleStatus(status);
    }

    private void ApplyHealthScaleStatus(HealthScaleCompanionStatus status)
    {
        HealthScaleStateText.Text = status.StateText;
        HealthScaleDetailText.Text = status.Detail;

        Brush stateBrush = status.State switch
        {
            HealthScaleCompanionState.InstalledVerified => (Brush)FindResource("SuccessBrush"),
            HealthScaleCompanionState.Conflict or HealthScaleCompanionState.Error or HealthScaleCompanionState.PayloadUnavailable =>
                (Brush)FindResource("DangerBrush"),
            _ => (Brush)FindResource("WarningBrush")
        };
        HealthScaleStateText.Foreground = stateBrush;

        HealthScaleGameRunningText.Text = status.GameRunning ? "Running · changes locked" : "Not running";
        HealthScaleGameRunningText.Foreground = status.GameRunning
            ? (Brush)FindResource("WarningBrush")
            : (Brush)FindResource("SuccessBrush");

        if (!string.IsNullOrWhiteSpace(status.GameBinPath))
        {
            HealthScaleGamePathTextBox.Text = status.GameBinPath;
        }
        else if (string.IsNullOrWhiteSpace(HealthScaleGamePathTextBox.Text))
        {
            HealthScaleGamePathTextBox.Text = _healthScaleCompanion.ConfiguredGameBinPath;
        }

        HealthScalePayloadText.Text = $"Payload: {DisplayPath(status.PayloadPath)}";
        HealthScaleInstalledText.Text = $"Installed: {DisplayPath(status.InstalledPath)}";
        HealthScalePayloadHashText.Text = $"Payload SHA-256: {DisplayHash(status.PayloadHash)}";
        HealthScaleInstalledHashText.Text = $"Installed SHA-256: {DisplayHash(status.InstalledHash)}";
        HealthScaleInstallButton.IsEnabled = status.CanInstall;
        HealthScaleUninstallButton.IsEnabled = status.CanUninstall;
        HealthScaleVerifyButton.IsEnabled = status.CanVerify;
    }

    private void BrowseHealthScaleGamePathButton_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFolderDialog dialog = new()
        {
            Title = "Select DB Xenoverse 2 or its bin folder",
            Multiselect = false
        };
        string currentPath = HealthScaleGamePathTextBox.Text.Trim();
        if (Directory.Exists(currentPath))
        {
            dialog.InitialDirectory = currentPath;
        }

        if (dialog.ShowDialog(this) == true)
        {
            HealthScaleGamePathTextBox.Text = dialog.FolderName;
            SaveHealthScaleGameLocation(dialog.FolderName);
        }
    }

    private void SaveHealthScaleGamePathButton_Click(object sender, RoutedEventArgs e) =>
        SaveHealthScaleGameLocation(HealthScaleGamePathTextBox.Text);

    private void SaveHealthScaleGameLocation(string path)
    {
        try
        {
            string normalizedPath = _healthScaleCompanion.ConfigureGameLocation(path);
            AddLog($"HealthScale companion DBXV2 location saved: {normalizedPath}");
            RefreshHealthScaleCompanion();
        }
        catch (Exception exception)
        {
            AddLog($"HealthScale location rejected: {exception.Message}");
            MessageBox.Show(exception.Message, "HealthScale Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshHealthScaleButton_Click(object sender, RoutedEventArgs e) => RefreshHealthScaleCompanion();

    private void InstallHealthScaleButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirmation = MessageBox.Show(
            "Install or adopt the bundled HealthScale 1.1.1 DLL?\n\nPowerScaler Labs will refuse to overwrite an unknown xinput_other.dll, will preserve an existing HealthScale.ini, and requires DBXV2 to be closed.",
            "Install HealthScale Companion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            HealthScaleCompanionStatus status = _healthScaleCompanion.InstallOrAdopt();
            ApplyHealthScaleStatus(status);
            AddLog($"HealthScale companion installed or adopted: {status.InstalledPath}");
            MessageBox.Show(status.Detail, "HealthScale Companion", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AddLog($"HealthScale install blocked: {exception.Message}");
            MessageBox.Show(exception.Message, "HealthScale Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshHealthScaleCompanion();
        }
    }

    private void VerifyHealthScaleButton_Click(object sender, RoutedEventArgs e)
    {
        HealthScaleCompanionStatus status = _healthScaleCompanion.Refresh();
        ApplyHealthScaleStatus(status);
        AddLog($"HealthScale verification: {status.StateText}. {status.Detail}");
        MessageBoxImage image = status.State is HealthScaleCompanionState.InstalledVerified or HealthScaleCompanionState.InstalledUnmanaged
            ? MessageBoxImage.Information
            : MessageBoxImage.Warning;
        MessageBox.Show(status.Detail, $"HealthScale Verification — {status.StateText}", MessageBoxButton.OK, image);
    }

    private void UninstallHealthScaleButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirmation = MessageBox.Show(
            "Remove the managed HealthScale DLL?\n\nOnly the DLL matching PowerScaler Labs' installation receipt will be removed. A changed HealthScale.ini will be preserved.",
            "Uninstall HealthScale Companion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            HealthScaleUninstallResult result = _healthScaleCompanion.Uninstall();
            string configurationResult = result.ConfigurationRemoved
                ? "The unchanged default HealthScale.ini was also removed."
                : result.ConfigurationPreserved
                    ? "HealthScale.ini was preserved."
                    : "No HealthScale.ini was present.";
            AddLog($"Managed HealthScale companion uninstalled. {configurationResult}");
            RefreshHealthScaleCompanion();
            MessageBox.Show($"Managed HealthScale DLL removed.\n\n{configurationResult}", "HealthScale Companion", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AddLog($"HealthScale uninstall blocked: {exception.Message}");
            MessageBox.Show(exception.Message, "HealthScale Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshHealthScaleCompanion();
        }
    }

    private void OpenHealthScaleGameBinButton_Click(object sender, RoutedEventArgs e)
    {
        HealthScaleCompanionStatus status = _healthScaleCompanion.Refresh();
        ApplyHealthScaleStatus(status);
        OpenDirectory(status.GameBinPath, "DBXV2 bin folder");
    }

    private void OpenHealthScaleCompanionFilesButton_Click(object sender, RoutedEventArgs e) =>
        OpenDirectory(_healthScaleCompanion.DocumentationDirectory, "HealthScale companion files");

    private void OpenDirectory(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show($"The {description} is not available in this build.", "PowerScaler Labs", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            AddLog($"Unable to open {description}: {exception.Message}");
            MessageBox.Show(exception.Message, "PowerScaler Labs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string DisplayPath(string path) => string.IsNullOrWhiteSpace(path) ? "—" : path;
    private static string DisplayHash(string hash) => string.IsNullOrWhiteSpace(hash) ? "—" : hash;
}
