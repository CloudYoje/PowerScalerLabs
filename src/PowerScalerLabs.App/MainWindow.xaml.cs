using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerScalerLabs.App.Companions;
using PowerScalerLabs.App.Models;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.App;

public partial class MainWindow : Window
{
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
    private bool _closeCommitted;
    private ProbeStatusMessage? _lastProbeStatus;
    private long _transportRequested;
    private long _transportAcknowledged;
    private long _transportMissing;
    private long _transportDuplicates;
    private long _transportMalformed;
    private long _transportDropped;
    private ulong _lastTransportSequence;
    private readonly HashSet<ulong> _transportSequences = [];
    private readonly List<string> _transportRuns = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

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
            "Correlated",
            "Retained as a chronology watch target; causal ownership is not yet proven.",
            "Research anchor"));
        FindingRows.Add(new(
            "Maximum Ki",
            $"Battle_Mob + 0x{RuntimeProtocol.MaximumKiOffset:X}",
            "Correlated",
            "Retained as a chronology watch target; causal ownership is not yet proven.",
            "Research anchor"));
        FindingRows.Add(new(
            "Current stamina",
            $"Battle_Mob + 0x{RuntimeProtocol.CurrentStaminaOffset:X}",
            "Correlated",
            "Retained as a chronology watch target; causal ownership is not yet proven.",
            "Research anchor"));
        FindingRows.Add(new(
            "Maximum stamina",
            $"Battle_Mob + 0x{RuntimeProtocol.MaximumStaminaOffset:X}",
            "Correlated",
            "Retained as a chronology watch target; causal ownership is not yet proven.",
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

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeCommitted)
        {
            e.Cancel = true;
            ProbeCommandResult result = await _probeClient.ShutdownAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(true);
            AddLog(result.Success ? "ProbeHost confirmed native shutdown and module removal." : $"Probe cleanup unresolved at App exit: {result.Detail}");
            _closeCommitted = true;
            Close();
            return;
        }
        _runtimeDesired = false;
        _probeClient.Dispose();
        TrySendShutdownSynchronously();
        _windowLifetime.Cancel();
        _pipeWriter?.Dispose();
        _pipeWriter = null;
        _pipe?.Dispose();
        _pipe = null;
        _runtimeProcess?.Dispose();
        _runtimeProcess = null;
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

        ReconcileFighters(message.Fighters);
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
            $"Host heartbeat {message.HeartbeatSequence:N0} · native QPC {message.NativeHeartbeatMonotonicTicks:N0} · dropped {message.DroppedNativeEventCount:N0} · watchpoints {message.ActiveWatchpointCount:N0}";
        AttachProbeButton.IsEnabled = message.State is ProbeState.Idle or ProbeState.Faulted && !message.ProbeDllLoaded;
        DetachProbeButton.IsEnabled = message.ProbeDllLoaded;
    }

    private void ApplyProbeEvent(ProbeEventMessage traceEvent)
    {
        if (traceEvent.EventType != ProbeEventType.Synthetic || traceEvent.Origin != "NativeProbe" || traceEvent.ThreadId <= 0) _transportMalformed++;
        if (!_transportSequences.Add(traceEvent.Sequence)) _transportDuplicates++;
        if (_lastTransportSequence != 0 && traceEvent.Sequence > _lastTransportSequence + 1)
            _transportMissing += checked((long)(traceEvent.Sequence - _lastTransportSequence - 1));
        if (traceEvent.Sequence > _lastTransportSequence) _lastTransportSequence = traceEvent.Sequence;
        ProbeTraceRows.Add(new(traceEvent.Sequence, traceEvent.EventType.ToString(), traceEvent.MonotonicTicks,
            traceEvent.ThreadId, traceEvent.TraceSessionId, traceEvent.WatchId));
        while (ProbeTraceRows.Count > 2_000) ProbeTraceRows.RemoveAt(0);
        UpdateTransportMetrics();
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

    private void ReconcileFighters(IReadOnlyList<FighterSnapshot> fighters)
    {
        HashSet<int> activeSlots = fighters.Select(fighter => fighter.Slot).ToHashSet();
        foreach (int staleSlot in _fighterBySlot.Keys.Where(slot => !activeSlots.Contains(slot)).ToArray())
        {
            FighterRow row = _fighterBySlot[staleSlot];
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
            row.Update(fighter);
        }
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

    private void ShowPage(Grid page, Button selectedButton, string title, string subtitle)
    {
        Grid[] pages = [DashboardPage, RuntimePage, FightersPage, ResearchPage, FindingsPage, LogsPage, ToolsPage];
        Button[] buttons = [DashboardNavButton, RuntimeNavButton, FightersNavButton, ResearchNavButton, FindingsNavButton, LogsNavButton, ToolsNavButton];
        foreach (Grid candidatePage in pages)
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

    private async void TestTraceTransportButton_Click(object sender, RoutedEventArgs e) => await RunTransportTestAsync("normal", 1, 0).ConfigureAwait(true);
    private async void SequentialTraceButton_Click(object sender, RoutedEventArgs e) => await RunTransportTestAsync("sequential-25", 25, 2).ConfigureAwait(true);
    private async void WraparoundTraceButton_Click(object sender, RoutedEventArgs e) => await RunTransportTestAsync("wraparound-512", 512, 2).ConfigureAwait(true);
    private async void OverflowTraceButton_Click(object sender, RoutedEventArgs e) => await RunTransportTestAsync("overflow-10000", 10_000, 0).ConfigureAwait(true);

    private async Task RunTransportTestAsync(string name, int count, int intervalMilliseconds)
    {
        ulong traceSession = unchecked((ulong)DateTime.UtcNow.Ticks);
        ulong watchId = unchecked((ulong)(_transportRuns.Count + 1));
        int receivedBefore = _transportSequences.Count;
        long droppedBefore = _transportDropped;
        _transportRequested += count;
        UpdateTransportMetrics();
        ProbeCommand command = _probeClient.CreateCommand("emit_synthetic_event", traceSessionId: traceSession,
            watchId: watchId, eventCount: count, intervalMilliseconds: intervalMilliseconds);
        ProbeCommandResult result = await _probeClient.SendAsync(command, TimeSpan.FromSeconds(Math.Max(20, count * intervalMilliseconds / 1000 + 15))).ConfigureAwait(true);
        if (result.Success) _transportAcknowledged += result.GeneratedEventCount;
        _transportDropped = Math.Max(_transportDropped, result.DroppedNativeEventCount);
        await Task.Delay(count >= 10_000 ? 1500 : 500).ConfigureAwait(true);
        int received = _transportSequences.Count - receivedBefore;
        string summary = $"{DateTimeOffset.Now:O} {name}: requested={count}, acknowledged={result.GeneratedEventCount}, received={received}, dropped_delta={_transportDropped - droppedBefore}, success={result.Success}, detail={result.Detail}";
        _transportRuns.Add(summary);
        AddLog(summary);
        UpdateTransportMetrics();
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
