using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using PowerScalerLabs.App.Companions;
using PowerScalerLabs.App.Models;
using PowerScalerLabs.App.Recording;
using PowerScalerLabs.App.Overlay;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.App;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _windowLifetime = new();
    private readonly SemaphoreSlim _pipeWriteLock = new(1, 1);
    private readonly string _logsDirectory;
    private readonly string _appLogPath;
    private readonly SessionRecorder _sessionRecorder;
    private readonly CandidateStore _candidateStore;
    private readonly HealthScaleCompanionManager _healthScaleCompanion;
    private readonly Dictionary<int, FighterRow> _fighterBySlot = [];
    private readonly List<ScannerObservationRow> _scannerDisplayRows = [];
    private readonly List<ChronologySampleRow> _chronologyDisplayRows = [];
    private Process? _runtimeProcess;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _pipeWriter;
    private Task? _connectionTask;
    private RuntimeStatusMessage? _latestStatus;
    private bool _runtimeDesired = true;
    private bool _dataStoresReady;
    private DateTimeOffset _lastCandidateRefreshUtc = DateTimeOffset.MinValue;
    private bool _recordingStorageFaulted;
    private bool _recordingTransitionActive;
    private ExperimentOverlayWindow? _experimentOverlay;
    private GlobalHotKey? _overlayHotKey;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        string persistentRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PowerScaler Labs");
        _logsDirectory = Path.Combine(persistentRoot, "Logs");
        _appLogPath = Path.Combine(_logsDirectory, "PowerScalerLabs.log");
        string dataRoot = Path.Combine(persistentRoot, "Data");
        Directory.CreateDirectory(_logsDirectory);
        Directory.CreateDirectory(dataRoot);

        _sessionRecorder = new SessionRecorder(dataRoot);
        _candidateStore = new CandidateStore(dataRoot);
        _healthScaleCompanion = new HealthScaleCompanionManager(persistentRoot);
        _dataStoresReady = true;
        RefreshCandidateRows();
        AddLog("PowerScaler Labs Runtime Access Architecture Gate 0 started.");
        AddLog($"Persistent research data: {dataRoot}");
        AddLog("HealthScale 1.1.1 is registered as a sealed companion. The PowerScaler runtime remains external and read-only.");
    }

    public ObservableCollection<FighterRow> FighterRows { get; } = [];
    public ObservableCollection<SessionEventRow> EventRows { get; } = [];
    public BulkObservableCollection<ChronologySampleRow> ChronologyRows { get; } = [];
    public BulkObservableCollection<ScannerObservationRow> ScannerObservationRows { get; } = [];
    public BulkObservableCollection<CandidateGroupRecord> CandidateRows { get; } = [];

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        FitWindowToWorkArea();
        TryRegisterOverlayHotKey();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FitWindowToWorkArea();
        RefreshHealthScaleCompanion();
        await StartRuntimeAsync().ConfigureAwait(true);
        ShowExperimentOverlay();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _runtimeDesired = false;
        _overlayHotKey?.Dispose();
        _overlayHotKey = null;
        _experimentOverlay?.ForceClose();
        _experimentOverlay = null;
        _sessionRecorder.Dispose();
        _candidateStore.Dispose();
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
        double targetWidth = Math.Min(880, workArea.Width * 0.76);
        double targetHeight = Math.Min(590, workArea.Height * 0.78);

        Width = Math.Clamp(targetWidth, MinWidth, usableWidth);
        Height = Math.Clamp(targetHeight, MinHeight, usableHeight);
        WindowState = WindowState.Normal;
        Left = Math.Max(workArea.Left + edgeGap, workArea.Left + (workArea.Width - Width) / 2);
        Top = Math.Max(workArea.Top + edgeGap, workArea.Top + (workArea.Height - Height) / 2);
    }

    private void TryRegisterOverlayHotKey()
    {
        try
        {
            _overlayHotKey = GlobalHotKey.Register(this, Key.F11);
            _overlayHotKey.Pressed += (_, _) => Dispatcher.Invoke(() => ToggleExperimentOverlay());
            AddLog("F11 registered to show or hide the guided test overlay.");
        }
        catch (Exception exception)
        {
            AddLog($"Guided overlay shortcut unavailable: {exception.Message}");
        }
    }

    private void ShowExperimentOverlay()
    {
        _experimentOverlay ??= new ExperimentOverlayWindow(this);
        _experimentOverlay.UpdateState(BuildOverlayState());
        _experimentOverlay.ShowOverlay();
    }

    private void ToggleExperimentOverlay()
    {
        _experimentOverlay ??= new ExperimentOverlayWindow(this);
        _experimentOverlay.UpdateState(BuildOverlayState());
        _experimentOverlay.ToggleOverlay();
    }

    private OverlayViewState BuildOverlayState()
    {
        RuntimeStatusMessage? status = _latestStatus;
        ScannerStatusMessage? scanner = status?.Scanner;
        return new OverlayViewState(
            status is null ? "Offline" : StateText(status.State),
            status is not null,
            status?.GameProcessId.HasValue == true,
            status?.Fighters.Count ?? 0,
            _sessionRecorder.IsRecording,
            _sessionRecorder.SessionId ?? string.Empty,
            scanner?.HasBaseline == true,
            scanner?.BaselineLabel ?? string.Empty,
            scanner?.PendingObservationCount ?? 0,
            scanner?.LastChangedCount ?? 0,
            scanner?.LastStableCount ?? 0,
            scanner?.DroppedObservationCount ?? 0,
            scanner?.Detail ?? status?.Detail ?? "Waiting for the companion runtime.");
    }

    private void RefreshOverlayState() => _experimentOverlay?.UpdateState(BuildOverlayState());

    internal void ShowMainApp()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    internal void SetGuidedActionLabel(string label)
    {
        ActionLabelComboBox.Text = string.IsNullOrWhiteSpace(label) ? "Custom Action" : label.Trim();
    }

    internal async Task CaptureGuidedBaselineAsync(string actionLabel)
    {
        EnsureGuidedCaptureReady(requireBaseline: false);
        SetGuidedActionLabel(actionLabel);
        string label = CurrentActionLabel();
        DateTimeOffset requestUtc = DateTimeOffset.UtcNow;
        await ApplyScannerConfigurationAsync().ConfigureAwait(true);
        await SendCommandAsync(new RuntimeCommand("capture_baseline", label)).ConfigureAwait(true);
        AddLog($"Guided baseline requested: {label}.");
        await WaitForScannerStateAsync(
            scanner => scanner.HasBaseline &&
                string.Equals(scanner.BaselineLabel, label, StringComparison.Ordinal) &&
                scanner.LastCaptureUtc >= requestUtc,
            $"baseline capture for '{label}'").ConfigureAwait(true);
    }

    internal async Task CompareGuidedResultsAsync(string actionLabel)
    {
        EnsureGuidedCaptureReady(requireBaseline: true);
        SetGuidedActionLabel(actionLabel);
        string label = CurrentActionLabel();
        string baselineLabel = _latestStatus?.Scanner.BaselineLabel ?? string.Empty;
        if (!string.Equals(baselineLabel, label, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The armed baseline belongs to '{baselineLabel}', not '{label}'. Select Cancel Test or Repeat Test before changing experiments.");
        }

        DateTimeOffset requestUtc = DateTimeOffset.UtcNow;
        await SendCommandAsync(new RuntimeCommand("compare_after", label)).ConfigureAwait(true);
        AddLog($"Guided comparison requested: {label}.");
        await WaitForScannerStateAsync(
            scanner => scanner.LastCaptureUtc >= requestUtc && scanner.HasBaseline,
            $"comparison for '{label}'").ConfigureAwait(true);
    }

    internal async Task RepeatGuidedTestAsync(string actionLabel)
    {
        EnsureGuidedCaptureReady(requireBaseline: true);
        SetGuidedActionLabel(actionLabel);
        string label = CurrentActionLabel();
        string baselineLabel = _latestStatus?.Scanner.BaselineLabel ?? string.Empty;
        if (!string.Equals(baselineLabel, label, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The armed baseline belongs to '{baselineLabel}', not '{label}'. Cancel it before selecting another test.");
        }

        DateTimeOffset requestUtc = DateTimeOffset.UtcNow;
        await ApplyScannerConfigurationAsync().ConfigureAwait(true);
        await SendCommandAsync(new RuntimeCommand("capture_baseline", label)).ConfigureAwait(true);
        AddLog($"Guided repeat baseline requested: {label}.");
        await WaitForScannerStateAsync(
            scanner => scanner.HasBaseline &&
                string.Equals(scanner.BaselineLabel, label, StringComparison.Ordinal) &&
                scanner.LastCaptureUtc >= requestUtc,
            $"repeat baseline capture for '{label}'").ConfigureAwait(true);
    }

    internal async Task CaptureGuidedSnapshotAsync(string actionLabel)
    {
        EnsureGuidedCaptureReady(requireBaseline: false);
        SetGuidedActionLabel(actionLabel);
        string label = CurrentActionLabel();
        DateTimeOffset requestUtc = DateTimeOffset.UtcNow;
        await SendCommandAsync(new RuntimeCommand("capture_full_snapshot", label)).ConfigureAwait(true);
        AddLog($"Guided full snapshot requested: {label}.");
        await WaitForScannerStateAsync(
            scanner => scanner.LastCaptureUtc >= requestUtc,
            $"full snapshot for '{label}'").ConfigureAwait(true);
    }

    internal async Task CancelGuidedTestAsync()
    {
        if ((_latestStatus?.Scanner.PendingObservationCount ?? 0) > 0)
        {
            throw new InvalidOperationException("Wait for Pending to reach 0 before cancelling the current baseline.");
        }
        await SendCommandAsync(new RuntimeCommand("clear_baseline", "Guided test cancelled")).ConfigureAwait(true);
        AddLog("Guided test baseline clear requested.");
        await WaitForScannerStateAsync(
            scanner => !scanner.HasBaseline,
            "baseline cancellation").ConfigureAwait(true);
    }

    internal async Task StartGuidedRecordingAsync(string actionLabel)
    {
        if (_sessionRecorder.IsRecording)
        {
            return;
        }
        string safeAction = string.IsNullOrWhiteSpace(actionLabel) ? "Capability Test" : actionLabel.Trim();
        string name = $"Guided {safeAction} {DateTimeOffset.Now:yyyy-MM-dd HHmmss}";
        SessionNameTextBox.Text = name;
        await StartRecordingCoreAsync(name).ConfigureAwait(true);
    }

    internal async Task StopGuidedRecordingAsync()
    {
        _ = await StopRecordingCoreAsync().ConfigureAwait(true);
        if (_latestStatus?.Scanner.HasBaseline == true)
        {
            await SendCommandAsync(new RuntimeCommand("clear_baseline", "Recording stopped")).ConfigureAwait(true);
            await WaitForScannerStateAsync(
                scanner => !scanner.HasBaseline,
                "post-recording baseline cleanup").ConfigureAwait(true);
            AddLog("Guided baseline cleared after recording stopped.");
        }
    }

    private async Task WaitForScannerStateAsync(
        Func<ScannerStatusMessage, bool> condition,
        string operation,
        int timeoutMilliseconds = 20000)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            _windowLifetime.Token.ThrowIfCancellationRequested();
            ScannerStatusMessage? scanner = _latestStatus?.Scanner;
            if (scanner is not null && condition(scanner))
            {
                return;
            }
            await Task.Delay(100, _windowLifetime.Token).ConfigureAwait(true);
        }

        throw new TimeoutException(
            $"PowerScaler Labs did not receive confirmation for {operation} within {timeoutMilliseconds / 1000} seconds. Check the overlay status and Diagnostics log.");
    }

    private async Task WaitForChronologyStateAsync(
        Func<ChronologyStatusMessage, bool> condition,
        string operation,
        int timeoutMilliseconds = 20000)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            _windowLifetime.Token.ThrowIfCancellationRequested();
            ChronologyStatusMessage? chronology = _latestStatus?.Chronology;
            if (chronology is not null && condition(chronology))
            {
                return;
            }
            await Task.Delay(50, _windowLifetime.Token).ConfigureAwait(true);
        }

        throw new TimeoutException(
            $"PowerScaler Labs did not receive confirmation for {operation} within {timeoutMilliseconds / 1000} seconds. Check Recording and Diagnostics.");
    }

    private async Task WaitForRuntimeQueuesAsync(int timeoutMilliseconds = 20000)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            _windowLifetime.Token.ThrowIfCancellationRequested();
            RuntimeStatusMessage? status = _latestStatus;
            if (status is not null &&
                status.Scanner.PendingObservationCount == 0 &&
                status.Chronology.PendingSampleCount == 0 &&
                !status.Chronology.SamplingActive)
            {
                return;
            }
            await Task.Delay(50, _windowLifetime.Token).ConfigureAwait(true);
        }

        int scannerPending = _latestStatus?.Scanner.PendingObservationCount ?? -1;
        int chronologyPending = _latestStatus?.Chronology.PendingSampleCount ?? -1;
        throw new TimeoutException(
            $"Runtime queues did not drain before stop. Scanner pending: {scannerPending:N0}; chronology pending: {chronologyPending:N0}.");
    }

    private void EnsureGuidedCaptureReady(bool requireBaseline)
    {
        if (!_sessionRecorder.IsRecording)
        {
            throw new InvalidOperationException("Start Recording before capturing a baseline, comparison, or snapshot.");
        }
        if (_latestStatus is null)
        {
            throw new InvalidOperationException("The companion runtime is not connected.");
        }
        if (!_latestStatus.GameProcessId.HasValue)
        {
            throw new InvalidOperationException("Xenoverse 2 is not detected.");
        }
        if (_latestStatus.Fighters.Count == 0)
        {
            throw new InvalidOperationException("No validated fighters are active. Enter Training mode or a battle first.");
        }
        if (_latestStatus.Scanner.PendingObservationCount > 0)
        {
            throw new InvalidOperationException($"Wait for Pending to reach 0. {_latestStatus.Scanner.PendingObservationCount:N0} observations are still being delivered.");
        }
        if (requireBaseline && !_latestStatus.Scanner.HasBaseline)
        {
            throw new InvalidOperationException("Capture a baseline for the selected test before comparing results.");
        }
    }

    private Task StartRuntimeAsync()
    {
        _runtimeDesired = true;
        if (_runtimeProcess is { HasExited: false })
        {
            AddLog("Companion runtime is already running.");
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

            AddLog($"Started capability-scanner runtime PID {_runtimeProcess.Id}.");
            SetConnectingState("Runtime started. Connecting to the Capability Scanner pipe…");
            EnsureConnectionLoop();
        }
        catch (Exception exception)
        {
            SetDisconnectedState("Runtime start failed", exception.Message);
            AddLog($"ERROR: Unable to start companion runtime: {exception}");
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
                await Dispatcher.InvokeAsync(() => AddLog("Connected to the Capability Scanner runtime pipe."));

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
                await Dispatcher.InvokeAsync(() => SetConnectingState("Waiting for the companion runtime pipe…"));
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
        _latestStatus = message;
        if (message.ProtocolVersion != RuntimeProtocol.ProtocolVersion)
        {
            SetDisconnectedState("Protocol mismatch",
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
        DashboardObserverStateText.Text = stateText;
        DashboardObserverStateText.Foreground = stateBrush;

        if (message.GameProcessId is int gameProcessId)
        {
            DashboardGameStateText.Text = "Detected";
            DashboardGameStateText.Foreground = (Brush)FindResource("SuccessBrush");
            DashboardGamePidText.Text = $"PID {gameProcessId}";
            GameProcessText.Text = $"Game PID {gameProcessId}";
        }
        else
        {
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
            $" · read-only {message.RuntimeAccess.ExternalReadOnly} · hooks {message.RuntimeAccess.HooksUsed} · writes {message.RuntimeAccess.GameWritesUsed}";
        LocatorDetailText.Text = $"BattleCore provider: {message.RuntimeAccess.ActiveLocatorId ?? "none"} · {message.RuntimeAccess.LocatorDetail}";
        MemoryAccessMetricsMessage observerAccess = message.RuntimeAccess.ObserverMetrics;
        MemoryAccessMetricsMessage chronologyAccess = message.RuntimeAccess.ChronologyMetrics;
        ReadBudgetText.Text =
            $"Read budget: observer {observerAccess.ReadRequests:N0} requests / {observerAccess.ReadProcessMemoryCalls:N0} OS reads / " +
            $"{observerAccess.CompletedBytes:N0} bytes / {observerAccess.RejectedReadRequests:N0} rejected / " +
            $"{observerAccess.VirtualQueryCalls:N0} queries / {observerAccess.FailedReadCalls:N0} OS failures · chronology " +
            $"{chronologyAccess.ReadRequests:N0} requests / {chronologyAccess.ReadProcessMemoryCalls:N0} OS reads / " +
            $"{chronologyAccess.CompletedBytes:N0} bytes / {chronologyAccess.RejectedReadRequests:N0} rejected / " +
            $"{chronologyAccess.VirtualQueryCalls:N0} queries / {chronologyAccess.FailedReadCalls:N0} OS failures";
        ComparisonPolicyMessage comparison = message.RuntimeAccess.ComparisonPolicy;
        ComparisonPolicyText.Text =
            $"Comparison policy {comparison.PolicyId}: absolute {comparison.AbsoluteTolerance:G3}, relative {comparison.RelativeTolerance:G3}. {comparison.RawChronologyPolicy}";

        ReconcileFighters(message.Fighters);
        DashboardFighterCountText.Text = $"{message.Fighters.Count} fighter{(message.Fighters.Count == 1 ? string.Empty : "s")}";
        FighterSummaryText.Text = message.BattleCoreAddress.HasValue
            ? $"{message.Fighters.Count} active fighter object(s) · {coreText}"
            : message.Detail;

        ApplyScannerStatus(message.Scanner);
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

        string heartbeat = $"Heartbeat {message.HeartbeatSequence:N0} · {message.TimestampUtc.ToLocalTime():T}";
        DashboardHeartbeatText.Text = heartbeat;
        RuntimeHeartbeatText.Text = heartbeat;
        FooterHeartbeatText.Text = heartbeat;

        foreach (TelemetryEventMessage telemetryEvent in message.Events)
        {
            AddEventRow(telemetryEvent);
            if (telemetryEvent.Kind is TelemetryEventKind.FighterAcquired or
                TelemetryEventKind.FighterReleased or
                TelemetryEventKind.ScannerBaselineCaptured or
                TelemetryEventKind.ScannerComparisonCompleted or
                TelemetryEventKind.ScannerSnapshotCaptured or
                TelemetryEventKind.ScannerWarning)
            {
                AddLog(telemetryEvent.Label);
            }

            if (telemetryEvent.Kind is TelemetryEventKind.ScannerBaselineCaptured or
                TelemetryEventKind.ScannerComparisonCompleted or
                TelemetryEventKind.ScannerSnapshotCaptured or
                TelemetryEventKind.ScannerBaselineCleared or
                TelemetryEventKind.ScannerWarning)
            {
                _experimentOverlay?.ShowFeedback(
                    telemetryEvent.Label,
                    telemetryEvent.Kind == TelemetryEventKind.ScannerWarning);
            }
        }

        if (message.ScanObservations.Count > 0)
        {
            IEnumerable<ScannerObservationMessage> displayBatch = message.ScanObservations
                .Where(observation => observation.Changed)
                .Concat(message.ScanObservations.Where(observation => !observation.Changed))
                .Take(150);
            _scannerDisplayRows.InsertRange(0, displayBatch.Select(ScannerObservationRow.FromObservation));
            if (_scannerDisplayRows.Count > 600)
            {
                _scannerDisplayRows.RemoveRange(600, _scannerDisplayRows.Count - 600);
            }
            ScannerObservationRows.ReplaceAll(_scannerDisplayRows);
        }

        if (_sessionRecorder.IsRecording && !_recordingStorageFaulted)
        {
            try
            {
                _sessionRecorder.RecordFrame(message);
                string sessionId = _sessionRecorder.SessionId ?? "unknown-session";
                bool candidatesChanged = _candidateStore.ObserveTelemetry(sessionId, message.Events);
                candidatesChanged |= _candidateStore.ObserveScanner(sessionId, message.ScanObservations);
                if (candidatesChanged && message.ScanObservations.Count > 0 &&
                    DateTimeOffset.UtcNow - _lastCandidateRefreshUtc >= TimeSpan.FromSeconds(2))
                {
                    RefreshCandidateRows();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _recordingStorageFaulted = true;
                AddLog($"ERROR: Recording storage failed: {exception.Message}");
                MessageBox.Show(
                    "PowerScaler Labs could not continue writing the active recording. Stop the recording and verify available disk space and folder permissions.\n\n" + exception.Message,
                    "Recording Storage Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        string? persistenceWarning = _candidateStore.TakePersistenceWarning();
        if (!string.IsNullOrWhiteSpace(persistenceWarning))
        {
            AddLog(persistenceWarning);
        }

        RefreshRecordingDisplay();
        RefreshOverlayState();
    }

    private void ApplyScannerStatus(ScannerStatusMessage scanner)
    {
        string state = scanner.HasBaseline ? "Baseline active" : "No baseline";
        Brush brush = scanner.HasBaseline
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("WarningBrush");
        DashboardScannerStateText.Text = state;
        DashboardScannerStateText.Foreground = brush;
        DashboardScannerMetricsText.Text =
            $"{scanner.LastObservationCount:N0} last · {scanner.PendingObservationCount:N0} queued";
        ScannerStatusText.Text = scanner.Detail;
        ScannerStatusText.Foreground = brush;
        ScannerMetricsText.Text =
            $"Baseline: {scanner.BaselineFighterCount} fighter(s), {scanner.BaselineRegionCount} region(s), " +
            $"{scanner.BaselineValueCount:N0} values · Last: {scanner.LastChangedCount:N0} changed, " +
            $"{scanner.LastStableCount:N0} stable · Queue: {scanner.PendingObservationCount:N0} · " +
            $"Dropped: {scanner.DroppedObservationCount:N0}";
    }

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
            $"epoch {chronology.Epoch:N0} · {chronology.WatchedTargetCount:N0} targets · {chronology.Configuration.IntervalMs:N0} ms · " +
            $"epoch samples {chronology.EpochEmittedSampleCount:N0} ({chronology.EpochInitialSampleCount:N0} initial, {chronology.EpochChangedSampleCount:N0} changed) · " +
            $"epoch polls {chronology.EpochPollCount:N0} · queue {chronology.PendingSampleCount:N0} · " +
            $"dropped {chronology.EpochDroppedSampleCount:N0} · invalidated {chronology.InvalidatedSampleCount:N0} · " +
            $"poll {chronology.LastPollDurationMilliseconds:F2} ms epoch max {chronology.EpochMaximumPollDurationMilliseconds:F2} ms · " +
            $"epoch overruns {chronology.EpochPollOverrunCount:N0}";
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

    private void RefreshCandidateRows()
    {
        if (!_dataStoresReady)
        {
            return;
        }

        string? selectedId = (CandidateGrid?.SelectedItem as CandidateGroupRecord)?.GroupId;
        string filter = CandidateFilterTextBox?.Text.Trim() ?? string.Empty;
        string familyFilter = SelectedComboContent(CandidateFamilyFilterComboBox);
        string roleFilter = SelectedComboContent(CandidateRoleFilterComboBox);
        string statusFilter = SelectedComboContent(CandidateStatusFilterComboBox);
        string tierFilter = SelectedComboContent(CandidateTierFilterComboBox);
        string validationFilter = SelectedComboContent(CandidateValidationFilterComboBox);
        IEnumerable<CandidateGroupRecord> groups = _candidateStore.Groups;

        if (tierFilter is "" or "Research view")
        {
            // The default is deliberately narrow: proven anchors plus genuinely correlated groups.
            // Promising hypotheses remain one dropdown selection away instead of occupying the main lab view.
            groups = groups.Where(group =>
                group.SignalTier is CandidateSignalTiers.KnownEffect or CandidateSignalTiers.HighConfidence);
        }
        else if (tierFilter != "All signal tiers")
        {
            groups = groups.Where(group => string.Equals(group.SignalTier, tierFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(validationFilter) && validationFilter != "All validation stages")
        {
            groups = groups.Where(group => string.Equals(group.ValidationStage, validationFilter, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            groups = groups.Where(group => CandidateMatches(group, filter));
        }
        if (!string.IsNullOrWhiteSpace(familyFilter) && familyFilter != "All stat families")
        {
            groups = groups.Where(group => string.Equals(group.StatFamily, familyFilter, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(roleFilter) && roleFilter != "All roles")
        {
            groups = groups.Where(group => string.Equals(group.StatRole, roleFilter, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(statusFilter) && statusFilter != "All statuses")
        {
            groups = groups.Where(group => string.Equals(group.Status, statusFilter, StringComparison.OrdinalIgnoreCase));
        }

        CandidateGroupRecord[] visibleGroups = groups
            .OrderBy(group => CandidateGroupBuilder.SignalTierRank(group.SignalTier))
            .ThenByDescending(group => CandidateGroupBuilder.ValidationStageRank(group.ValidationStage))
            .ThenBy(group => FamilyRank(group.StatFamily))
            .ThenBy(group => StatusRank(group.Status))
            .ThenByDescending(group => group.ClassificationConfidence)
            .ThenByDescending(group => group.Confidence)
            .ThenByDescending(group => group.ChangeCount)
            .Take(5000)
            .ToArray();
        CandidateRows.ReplaceAll(visibleGroups);

        if (CandidateGrid is not null && selectedId is not null)
        {
            CandidateGrid.SelectedItem = CandidateRows.FirstOrDefault(group => group.GroupId == selectedId);
        }

        IReadOnlyList<CandidateRecord> raw = _candidateStore.Records;
        IReadOnlyList<CandidateGroupRecord> allGroups = _candidateStore.Groups;
        int known = allGroups.Count(group => group.SignalTier == CandidateSignalTiers.KnownEffect);
        int high = allGroups.Count(group => group.SignalTier == CandidateSignalTiers.HighConfidence);
        int promising = allGroups.Count(group => group.SignalTier == CandidateSignalTiers.Promising);
        int unexplained = allGroups.Count(group => !group.IsExplained && group.SignalTier != CandidateSignalTiers.BackgroundNoise);
        double collapse = raw.Count == 0 ? 0 : (1.0 - allGroups.Count / (double)raw.Count) * 100.0;
        CandidateSummaryText.Text =
            $"{raw.Count:N0} raw interpretations collapsed into {allGroups.Count:N0} physical offsets ({collapse:F1}% less visible clutter) · " +
            $"{known:N0} known · {high:N0} high-confidence · {promising:N0} promising · {unexplained:N0} unresolved · {CandidateRows.Count:N0} focused rows shown. " +
            @"Raw evidence remains lossless in Candidates\candidates.json; grouped views are exported under physical-groups.json, ByTier, and ByValidation.";
        _lastCandidateRefreshUtc = DateTimeOffset.UtcNow;
    }

    private static int StatusRank(string status) => status switch
    {
        "Known" => 0,
        "Solid" => 1,
        "Strong" => 2,
        "Candidate" => 3,
        "Provisional" => 4,
        "Noise" => 5,
        _ => 6
    };

    private static int FamilyRank(string family)
    {
        for (int index = 0; index < CandidateTaxonomy.Families.Count; index++)
        {
            if (string.Equals(CandidateTaxonomy.Families[index], family, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return CandidateTaxonomy.Families.Count;
    }

    private static string SelectedComboContent(ComboBox? comboBox) =>
        comboBox?.SelectedItem is ComboBoxItem item ? item.Content?.ToString() ?? string.Empty : comboBox?.Text ?? string.Empty;

    private static bool CandidateMatches(CandidateGroupRecord group, string filter)
    {
        string searchable = string.Join(" ",
            group.SignalTier,
            group.ValidationStage,
            group.StatFamily,
            group.StatRole,
            group.ClassificationSource,
            group.ClassificationTagsText,
            group.RegionPath,
            group.OffsetText,
            group.PreferredValueType,
            group.AlternativeTypesText,
            group.ValueShape,
            group.Label,
            group.Status,
            group.TopActions,
            group.SlotSummary,
            group.PairRelationship,
            group.GroupId,
            group.PreferredCandidateId);
        return searchable.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshRecordingDisplay()
    {
        if (_sessionRecorder.IsRecording)
        {
            DashboardRecordingStateText.Text = "Recording";
            DashboardRecordingStateText.Foreground = (Brush)FindResource("DangerBrush");
            RecordingStatusText.Text = $"Recording session: {_sessionRecorder.SessionId}";
        }
        else
        {
            DashboardRecordingStateText.Text = "Stopped";
            DashboardRecordingStateText.Foreground = (Brush)FindResource("WarningBrush");
            RecordingStatusText.Text =
                "No active recording. Start recording before the baseline so all stable and changing candidates are retained.";
        }

        string metrics =
            $"{_sessionRecorder.FrameCount:N0} frames · {_sessionRecorder.EventCount:N0} events · " +
            $"{_sessionRecorder.ScannerObservationCount:N0} scan observations · " +
            $"{_sessionRecorder.ChronologySampleCount:N0} chronology samples";
        DashboardRecordingCountText.Text = metrics;
        RecordingMetricsText.Text = metrics + " · chronology and candidate evidence are written atomically";
        RefreshOverlayState();
    }

    private async Task StopRuntimeAsync()
    {
        _runtimeDesired = false;
        await SendCommandAsync(new RuntimeCommand("shutdown")).ConfigureAwait(true);
        AddLog("Shutdown command sent to companion runtime.");

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
        SetDisconnectedState("Offline", "The companion runtime is stopped.");
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
        DashboardObserverStateText.Text = "Connecting";
        DashboardObserverStateText.Foreground = warning;
        DashboardDetailText.Text = detail;
        RuntimeDetailText.Text = detail;
        ChronologyStatusText.Text = "Chronology sampler is connecting.";
        ChronologyStatusText.Foreground = warning;
        ChronologyMetricsText.Text = "Waiting for runtime chronology diagnostics.";
        RuntimeAccessGateText.Text = "Runtime access architecture — connecting";
        LocatorDetailText.Text = "BattleCore provider —";
        ReadBudgetText.Text = "Read budget —";
        ComparisonPolicyText.Text = "Comparison policy —";
        RefreshOverlayState();
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
        DashboardObserverStateText.Text = state;
        DashboardObserverStateText.Foreground = warning;
        DashboardDetailText.Text = detail;
        RuntimeDetailText.Text = detail;
        DashboardGameStateText.Text = "Not detected";
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
        DashboardScannerStateText.Text = "No baseline";
        DashboardScannerStateText.Foreground = warning;
        DashboardScannerMetricsText.Text = "0 observations";
        ScannerStatusText.Text = detail;
        ScannerStatusText.Foreground = warning;
        ScannerMetricsText.Text = "No scanner connection.";
        ChronologyStatusText.Text = detail;
        ChronologyStatusText.Foreground = warning;
        ChronologyMetricsText.Text = "No chronology connection.";
        FooterHeartbeatText.Text = "Heartbeat —";
        DashboardHeartbeatText.Text = "Last heartbeat —";
        RuntimeHeartbeatText.Text = "Heartbeat —";
        _latestStatus = null;
        _fighterBySlot.Clear();
        FighterRows.Clear();
        RefreshOverlayState();
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
        Grid[] pages = [DashboardPage, RuntimePage, FightersPage, ScannerPage, RecordingPage, CandidatesPage, CompanionAppsPage, LogsPage];
        Button[] buttons = [DashboardNavButton, RuntimeNavButton, FightersNavButton, ScannerNavButton, RecordingNavButton, CandidatesNavButton, CompanionAppsNavButton, LogsNavButton];
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

    private ScannerConfiguration BuildScannerConfiguration()
    {
        List<ScannerValueType> valueTypes = [];
        AddIfChecked(ScanFloat32CheckBox, ScannerValueType.Float32, valueTypes);
        AddIfChecked(ScanInt32CheckBox, ScannerValueType.Int32, valueTypes);
        AddIfChecked(ScanUInt32CheckBox, ScannerValueType.UInt32, valueTypes);
        AddIfChecked(ScanFloat64CheckBox, ScannerValueType.Float64, valueTypes);
        AddIfChecked(ScanInt64CheckBox, ScannerValueType.Int64, valueTypes);
        AddIfChecked(ScanUInt64CheckBox, ScannerValueType.UInt64, valueTypes);
        AddIfChecked(ScanInt16CheckBox, ScannerValueType.Int16, valueTypes);
        AddIfChecked(ScanUInt16CheckBox, ScannerValueType.UInt16, valueTypes);
        AddIfChecked(ScanByteCheckBox, ScannerValueType.Byte, valueTypes);
        AddIfChecked(ScanPointer64CheckBox, ScannerValueType.Pointer64, valueTypes);
        if (valueTypes.Count == 0)
        {
            throw new InvalidOperationException("Select at least one scanner value type.");
        }

        ScannerConfiguration configuration = new(
            ParseUInt(ScanStartOffsetTextBox.Text, "start offset"),
            ParseUInt(ScanEndOffsetTextBox.Text, "end offset"),
            ParseInt(ScanStrideTextBox.Text, "stride"),
            valueTypes,
            ParseInt(ScanMaximumFightersTextBox.Text, "maximum fighters"),
            ContinuousTrackingCheckBox.IsChecked == true,
            ParseInt(ScanIntervalTextBox.Text, "continuous interval"),
            ParseInt(ScanObservationLimitTextBox.Text, "batch size"),
            FollowPointersCheckBox.IsChecked == true,
            FollowPointersCheckBox.IsChecked == true ? ParseInt(PointerDepthTextBox.Text, "pointer depth") : 0,
            ParseUInt(ChildScanSizeTextBox.Text, "child scan size"),
            ParseInt(MaxChildObjectsTextBox.Text, "maximum child objects"));
        ValidateScannerConfiguration(configuration);
        return configuration;
    }

    private async Task ApplyScannerConfigurationAsync()
    {
        ScannerConfiguration configuration = BuildScannerConfiguration();
        await SendCommandAsync(new RuntimeCommand("configure_scanner", "App configuration", configuration)).ConfigureAwait(true);
        AddLog($"Scanner configuration sent: +0x{configuration.StartOffset:X}..+0x{configuration.EndOffset:X}, " +
            $"stride {configuration.Stride}, {configuration.ValueTypes.Count} type(s), {configuration.MaximumFighters} fighter(s).");
    }

    private static void ValidateScannerConfiguration(ScannerConfiguration configuration)
    {
        if (configuration.EndOffset < configuration.StartOffset)
        {
            throw new InvalidOperationException("End offset must be greater than or equal to start offset.");
        }
        if (configuration.EndOffset > RuntimeProtocol.MaximumScanEndOffset)
        {
            throw new InvalidOperationException($"End offset cannot exceed +0x{RuntimeProtocol.MaximumScanEndOffset:X}.");
        }
        if (configuration.Stride is not (1 or 2 or 4 or 8))
        {
            throw new InvalidOperationException("Stride must be 1, 2, 4, or 8 bytes.");
        }
        if (configuration.MaximumFighters is < 1 or > RuntimeProtocol.ObservedFighterSlotCount)
        {
            throw new InvalidOperationException($"Maximum fighters must be between 1 and {RuntimeProtocol.ObservedFighterSlotCount}.");
        }
        if (configuration.ContinuousIntervalMs is < 100 or > 5000)
        {
            throw new InvalidOperationException("Continuous interval must be between 100 and 5000 milliseconds.");
        }
        if (configuration.MaximumObservationsPerFrame is < 50 or > RuntimeProtocol.MaximumObservationBatch)
        {
            throw new InvalidOperationException($"Batch size must be between 50 and {RuntimeProtocol.MaximumObservationBatch:N0}.");
        }
        if (configuration.PointerDepth is < 0 or > RuntimeProtocol.MaximumPointerDepth)
        {
            throw new InvalidOperationException($"Pointer depth must be between 0 and {RuntimeProtocol.MaximumPointerDepth}.");
        }
        if (configuration.ChildScanSize is < 0x40 or > RuntimeProtocol.MaximumChildScanSize)
        {
            throw new InvalidOperationException($"Child scan size must be between 0x40 and 0x{RuntimeProtocol.MaximumChildScanSize:X} bytes.");
        }
        if (configuration.MaximumChildObjects is < 0 or > RuntimeProtocol.MaximumChildObjects)
        {
            throw new InvalidOperationException($"Maximum child objects must be between 0 and {RuntimeProtocol.MaximumChildObjects}.");
        }

        long rootOffsets = ((long)configuration.EndOffset - configuration.StartOffset) / configuration.Stride + 1;
        long childOffsets = configuration.FollowPointers && configuration.PointerDepth > 0
            ? (((long)configuration.ChildScanSize - 1) / configuration.Stride + 1) *
              configuration.MaximumChildObjects
            : 0;
        long projectedObservations = configuration.MaximumFighters *
            (rootOffsets + childOffsets) * configuration.ValueTypes.Count;
        if (projectedObservations > RuntimeProtocol.MaximumCompleteCaptureObservations)
        {
            throw new InvalidOperationException(
                $"This configuration projects {projectedObservations:N0} typed observations per complete capture, above the safe complete-capture limit of " +
                $"{RuntimeProtocol.MaximumCompleteCaptureObservations:N0}. Reduce the range, value types, fighters, pointer depth, or child objects.");
        }
        if (configuration.ContinuousTracking && projectedObservations > RuntimeProtocol.MaximumContinuousObservations)
        {
            throw new InvalidOperationException(
                $"Continuous tracking is limited to {RuntimeProtocol.MaximumContinuousObservations:N0} projected observations per pass, but this configuration projects " +
                $"{projectedObservations:N0}. Disable continuous tracking or reduce the scan scope; complete labeled captures can still use the larger limit.");
        }
    }

    private static void AddIfChecked(CheckBox checkBox, ScannerValueType type, List<ScannerValueType> values)
    {
        if (checkBox.IsChecked == true)
        {
            values.Add(type);
        }
    }

    private static uint ParseUInt(string text, string fieldName)
    {
        string value = text.Trim();
        NumberStyles style = NumberStyles.Integer;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            style = NumberStyles.AllowHexSpecifier;
        }
        if (!uint.TryParse(value, style, CultureInfo.InvariantCulture, out uint parsed))
        {
            throw new InvalidOperationException($"Invalid {fieldName}: '{text}'. Use decimal or 0x-prefixed hexadecimal.");
        }
        return parsed;
    }

    private static int ParseInt(string text, string fieldName)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            throw new InvalidOperationException($"Invalid {fieldName}: '{text}'.");
        }
        return parsed;
    }

    private string CurrentActionLabel()
    {
        string label = ActionLabelComboBox.Text.Trim();
        return string.IsNullOrWhiteSpace(label) ? "Unlabeled action" : label;
    }

    private static string StateText(RuntimeState state) => state switch
    {
        RuntimeState.WaitingForGame => "Waiting for game",
        RuntimeState.ReadPermissionGranted => "Read access ready",
        RuntimeState.WaitingForPatcher => "Waiting for patcher",
        RuntimeState.UnsupportedGameBuild => "Unsupported game build",
        RuntimeState.UnsupportedPatcher => "Unsupported patcher",
        RuntimeState.WaitingForBattleCore => "Waiting for BattleCore",
        RuntimeState.WaitingForFighters => "Waiting for fighters",
        RuntimeState.ObservingFighters => "Observing fighters",
        RuntimeState.ScanningCapabilities => "Scanning capabilities",
        RuntimeState.ReadPermissionDenied => "Access denied",
        _ => state.ToString()
    };

    private Brush BrushForState(RuntimeState state) => state switch
    {
        RuntimeState.ObservingFighters or RuntimeState.ScanningCapabilities or RuntimeState.WaitingForFighters or RuntimeState.ReadPermissionGranted =>
            (Brush)FindResource("SuccessBrush"),
        RuntimeState.UnsupportedGameBuild or RuntimeState.UnsupportedPatcher or RuntimeState.ReadPermissionDenied or RuntimeState.Error =>
            (Brush)FindResource("DangerBrush"),
        _ => (Brush)FindResource("WarningBrush")
    };

    private void DashboardNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(DashboardPage, DashboardNavButton, "Dashboard", "Full external character-state discovery and durable evidence");

    private void RuntimeNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(RuntimePage, RuntimeNavButton, "Runtime Connection", "Separate read-only companion, patcher detection, and BattleCore state");

    private void FightersNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(FightersPage, FightersNavButton, "Live Fighters", "Validated Battle_Mob objects and known health references");

    private void ScannerNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(ScannerPage, ScannerNavButton, "Capability Scanner", "Labeled baselines, full snapshots, comparisons, pointer children, and continuous deltas");

    private void RecordingNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(RecordingPage, RecordingNavButton, "Recording", "Persist raw frames, scanner observations, actions, experiments, and evidence");

    private void CandidatesNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(CandidatesPage, CandidatesNavButton, "Candidates & Findings", "Classify, label, reject, or promote repeatable character-state fields");

    private void CompanionAppsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(CompanionAppsPage, CompanionAppsNavButton, "Companion Apps", "Install, verify, or remove sealed companion products without merging their runtimes");
        RefreshHealthScaleCompanion();
    }

    private void LogsNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(LogsPage, LogsNavButton, "Diagnostics", "Visible app and runtime connection events");

    private void RefreshHealthScaleCompanion()
    {
        HealthScaleCompanionStatus status = _healthScaleCompanion.Refresh();
        ApplyHealthScaleStatus(status);
    }

    private void ApplyHealthScaleStatus(HealthScaleCompanionStatus status)
    {
        HealthScaleStateText.Text = status.StateText;
        HealthScaleDetailText.Text = status.Detail;
        SidebarHealthScaleStateText.Text = status.State switch
        {
            HealthScaleCompanionState.InstalledVerified => "Installed · Verified",
            HealthScaleCompanionState.InstalledUnmanaged => "Installed · Separate",
            HealthScaleCompanionState.Conflict => "Protected · Conflict",
            _ => "Sealed · Separate"
        };

        Brush stateBrush = status.State switch
        {
            HealthScaleCompanionState.InstalledVerified => (Brush)FindResource("SuccessBrush"),
            HealthScaleCompanionState.Conflict or HealthScaleCompanionState.Error or HealthScaleCompanionState.PayloadUnavailable =>
                (Brush)FindResource("DangerBrush"),
            _ => (Brush)FindResource("WarningBrush")
        };
        HealthScaleStateText.Foreground = stateBrush;
        SidebarHealthScaleStateText.Foreground = stateBrush;

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
            MessageBox.Show($"The {description} is not available in this build.", "HealthScale Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(exception.Message, "HealthScale Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string DisplayPath(string path) => string.IsNullOrWhiteSpace(path) ? "—" : path;

    private static string DisplayHash(string hash) => string.IsNullOrWhiteSpace(hash) ? "—" : hash;

    private void OpenGuidedOverlayButton_Click(object sender, RoutedEventArgs e) => ShowExperimentOverlay();

    private async void StartRuntimeButton_Click(object sender, RoutedEventArgs e) =>
        await StartRuntimeAsync().ConfigureAwait(true);

    private async void StopRuntimeButton_Click(object sender, RoutedEventArgs e) =>
        await StopRuntimeAsync().ConfigureAwait(true);

    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _runtimeDesired = true;
        await DisconnectPipeAsync().ConfigureAwait(true);
        AddLog("Pipe reconnect requested.");
        EnsureConnectionLoop();
    }

    private async void ApplyScannerConfigButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ApplyScannerConfigurationAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AddLog($"Scanner configuration rejected: {exception.Message}");
            MessageBox.Show(exception.Message, "Capability Scanner Configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CaptureBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CaptureGuidedBaselineAsync(CurrentActionLabel()).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Capture Baseline", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CompareAfterButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CompareGuidedResultsAsync(CurrentActionLabel()).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Compare Results", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void FullSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CaptureGuidedSnapshotAsync(CurrentActionLabel()).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Full Snapshot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ClearBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CancelGuidedTestAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Clear Baseline", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StartRecordingCoreAsync(string requestedName)
    {
        if (_recordingTransitionActive)
        {
            throw new InvalidOperationException("A recording start or stop transition is already active.");
        }
        if (_latestStatus is null || _pipeWriter is null)
        {
            throw new InvalidOperationException("The companion runtime must be connected before chronological recording starts.");
        }

        _recordingTransitionActive = true;
        try
        {
            long previousEpoch = _latestStatus.Chronology.Epoch;
            string sessionId = _sessionRecorder.Start(requestedName, _latestStatus);
            _recordingStorageFaulted = false;
            EventRows.Clear();
            _scannerDisplayRows.Clear();
            ScannerObservationRows.ReplaceAll([]);
            _chronologyDisplayRows.Clear();
            ChronologyRows.ReplaceAll([]);

            try
            {
                await SendCommandAsync(new RuntimeCommand("new_chronology_epoch", "Session chronology epoch")).ConfigureAwait(true);
                await WaitForChronologyStateAsync(
                    chronology => chronology.Epoch > previousEpoch &&
                        (chronology.ActiveFighterCount == 0 ||
                         chronology.EpochInitialSampleCount >=
                            (long)chronology.ActiveFighterCount * chronology.WatchedTargetCount),
                    "fresh chronology epoch and initial anchor capture").ConfigureAwait(true);
            }
            catch
            {
                try
                {
                    string incompleteFolder = _sessionRecorder.Stop();
                    AddLog($"Recording initialization failed; incomplete session closed safely: {incompleteFolder}");
                }
                catch (Exception cleanupException)
                {
                    AddLog($"ERROR: Could not close incomplete recording: {cleanupException.Message}");
                }
                throw;
            }

            AddLog($"Recording started: {sessionId}. Fresh chronology epoch {(_latestStatus?.Chronology.Epoch ?? 0):N0} confirmed with focused anchor baselines.");
            RefreshRecordingDisplay();
            RefreshOverlayState();
        }
        finally
        {
            _recordingTransitionActive = false;
        }
    }

    private async Task<string> StopRecordingCoreAsync()
    {
        if (_recordingTransitionActive)
        {
            throw new InvalidOperationException("A recording start or stop transition is already active.");
        }
        if (!_sessionRecorder.IsRecording)
        {
            throw new InvalidOperationException("No capability-scanner session is recording.");
        }
        if (_latestStatus is null || _pipeWriter is null)
        {
            throw new InvalidOperationException("The companion runtime must remain connected so chronology can pause and drain before the session closes.");
        }

        _recordingTransitionActive = true;
        bool resumeChronology = _latestStatus.Chronology.Enabled;
        try
        {
            if (resumeChronology)
            {
                await SendCommandAsync(new RuntimeCommand("pause_chronology", "Recording stop drain")).ConfigureAwait(true);
                await WaitForChronologyStateAsync(
                    chronology => !chronology.Enabled && !chronology.SamplingActive,
                    "chronology pause barrier").ConfigureAwait(true);
            }

            await WaitForRuntimeQueuesAsync().ConfigureAwait(true);
            string folder = _sessionRecorder.Stop();
            _recordingStorageFaulted = false;
            _candidateStore.Flush();
            AddLog($"Recording stopped after chronology pause/drain and saved: {folder}");
            RefreshCandidateRows();
            RefreshRecordingDisplay();
            RefreshOverlayState();
            return folder;
        }
        finally
        {
            if (resumeChronology && _pipeWriter is not null)
            {
                try
                {
                    await SendCommandAsync(new RuntimeCommand("resume_chronology", "Recording stop complete")).ConfigureAwait(true);
                    AddLog("Chronology sampling resumed after the session closed.");
                }
                catch (Exception exception)
                {
                    AddLog($"WARNING: Chronology resume command failed after recording stop: {exception.Message}");
                }
            }
            _recordingTransitionActive = false;
        }
    }

    private async void StartRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StartRecordingCoreAsync(SessionNameTextBox.Text).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AddLog($"ERROR: Could not start recording: {exception.Message}");
            MessageBox.Show(exception.Message, "PowerScaler Labs Recording", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void StopRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StopGuidedRecordingAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AddLog($"Recording stop ignored: {exception.Message}");
        }
    }

    private void PromoteCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateGrid.SelectedItem is not CandidateGroupRecord selected)
        {
            MessageBox.Show("Select a candidate first.", "PowerScaler Labs Candidates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (CandidateGroupBuilder.ValidationStageRank(selected.ValidationStage) >=
            CandidateGroupBuilder.ValidationStageRank(CandidateValidationStages.CodeAnchored))
        {
            AddLog($"Promotion ignored: {selected.PreferredCandidateId} already has protected {selected.ValidationStage} evidence.");
            return;
        }

        _candidateStore.PromoteToSolid(selected.PreferredCandidateId);
        AddLog($"Candidate promoted to Correlated: {selected.PreferredCandidateId}.");
        RefreshCandidateRows();
    }

    private void RejectCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateGrid.SelectedItem is not CandidateGroupRecord selected)
        {
            MessageBox.Show("Select a candidate first.", "PowerScaler Labs Candidates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (CandidateGroupBuilder.ValidationStageRank(selected.ValidationStage) >=
            CandidateGroupBuilder.ValidationStageRank(CandidateValidationStages.CodeAnchored))
        {
            AddLog($"Noise rejection blocked: {selected.PreferredCandidateId} has protected {selected.ValidationStage} evidence.");
            return;
        }

        _candidateStore.RejectAsNoise(selected.PreferredCandidateId);
        AddLog($"Candidate marked as noise: {selected.PreferredCandidateId}.");
        RefreshCandidateRows();
    }

    private void AssignCandidateLabelButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateGrid.SelectedItem is not CandidateGroupRecord selected || string.IsNullOrWhiteSpace(CandidateLabelTextBox.Text))
        {
            MessageBox.Show("Select a candidate and enter a label.", "PowerScaler Labs Candidates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _candidateStore.AssignLabel(selected.PreferredCandidateId, CandidateLabelTextBox.Text);
        AddLog($"Candidate labeled '{CandidateLabelTextBox.Text.Trim()}': {selected.PreferredCandidateId}.");
        CandidateLabelTextBox.Clear();
        RefreshCandidateRows();
    }

    private void AssignCandidateClassificationButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateGrid.SelectedItem is not CandidateGroupRecord selected)
        {
            MessageBox.Show("Select a candidate first.", "PowerScaler Labs Classification", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string family = SelectedComboContent(CandidateFamilyAssignComboBox);
        string role = SelectedComboContent(CandidateRoleAssignComboBox);
        _candidateStore.AssignClassification(selected.PreferredCandidateId, family, role);
        AddLog($"Candidate manually classified as {family} / {role}: {selected.PreferredCandidateId}.");
        RefreshCandidateRows();
    }

    private void AutoClassifyCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateGrid.SelectedItem is not CandidateGroupRecord selected)
        {
            MessageBox.Show("Select a candidate first.", "PowerScaler Labs Classification", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _candidateStore.RestoreAutomaticClassification(selected.PreferredCandidateId);
        AddLog($"Candidate returned to automatic evidence-based classification: {selected.PreferredCandidateId}.");
        RefreshCandidateRows();
    }

    private void CandidateClassificationFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCandidateRows();
    private void CandidateFilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshCandidateRows();
    private void OpenSessionsButton_Click(object sender, RoutedEventArgs e) => OpenDirectory(_sessionRecorder.SessionsRoot);
    private void OpenCandidatesButton_Click(object sender, RoutedEventArgs e) => OpenDirectory(_candidateStore.DirectoryPath);
    private void OpenFindingsButton_Click(object sender, RoutedEventArgs e) => OpenDirectory(_candidateStore.FindingsDirectoryPath);
    private void ClearLogsButton_Click(object sender, RoutedEventArgs e) => LogList.Items.Clear();
    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => OpenDirectory(_logsDirectory);

    private static void OpenDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }
}
