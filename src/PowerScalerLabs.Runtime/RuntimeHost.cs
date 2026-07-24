using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.Runtime;

internal sealed class RuntimeHost
{
    private const int MaximumPendingCommands = 256;
    private static readonly TimeSpan AccessProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ExternalCapabilityObserver _observer = new();
    private readonly ChronologySampler _chronologySampler = new();
    private readonly ConcurrentQueue<RuntimeCommand> _pendingCommands = new();
    private long _heartbeatSequence;
    private RuntimeState? _lastLoggedState;
    private int? _lastLoggedGameProcessId;
    private ulong? _lastLoggedBattleCore;
    private Process? _trackedGameProcess;
    private int? _cachedAccessProcessId;
    private ProcessAccessProbe? _cachedAccessProbe;
    private DateTimeOffset _nextAccessProbeUtc = DateTimeOffset.MinValue;
    private int _pendingCommandCount;

    public async Task<int> RunAsync()
    {
        RuntimeLog.Write($"PowerScaler Labs Capability Scanner runtime started. PID {Environment.ProcessId}.");
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    await ServeOneConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException exception)
                {
                    RuntimeLog.Write($"Pipe I/O interruption: {exception.Message}");
                    await DelayBeforeReconnectAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RuntimeLog.Write($"Runtime loop error: {exception}");
                    await DelayBeforeReconnectAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _chronologySampler.Dispose();
            _observer.Dispose();
            DisposeTrackedGameProcess();
        }

        RuntimeLog.Write("PowerScaler Labs Capability Scanner runtime stopped normally.");
        return 0;
    }

    private async Task ServeOneConnectionAsync(CancellationToken cancellationToken)
    {
        await using NamedPipeServerStream pipe = new(
            RuntimeProtocol.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        RuntimeLog.Write("Waiting for PowerScaler Labs pipe connection.");
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        RuntimeLog.Write("PowerScaler Labs connected to the Capability Scanner pipe.");

        using StreamReader reader = new(pipe, leaveOpen: true);
        using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
        using CancellationTokenSource connectionLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task commandTask = ReadCommandsAsync(reader, connectionLifetime.Token);

        try
        {
            while (pipe.IsConnected && !connectionLifetime.IsCancellationRequested)
            {
                RuntimeStatusMessage status = CreateStatusMessage(DrainCommands());
                LogStateTransition(status);
                string json = JsonSerializer.Serialize(status, JsonOptions);
                await writer.WriteLineAsync(json).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(100), connectionLifetime.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            connectionLifetime.Cancel();
            try
            {
                await commandTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected while disconnecting.
            }
            catch (IOException)
            {
                // The app disconnected.
            }
        }
    }

    private async Task ReadCommandsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            RuntimeCommand? command;
            try
            {
                command = JsonSerializer.Deserialize<RuntimeCommand>(line, JsonOptions);
            }
            catch (JsonException exception)
            {
                RuntimeLog.Write($"Ignored malformed runtime command: {exception.Message}");
                continue;
            }

            if (command is null || string.IsNullOrWhiteSpace(command.Command))
            {
                continue;
            }

            if (string.Equals(command.Command, "shutdown", StringComparison.OrdinalIgnoreCase))
            {
                RuntimeLog.Write("Shutdown command received from PowerScaler Labs.");
                _shutdown.Cancel();
                return;
            }

            if (!TryQueueCommand(command))
            {
                RuntimeLog.Write($"Scanner command queue is full; command dropped: {command.Command}.");
                continue;
            }

            RuntimeLog.Write($"Scanner command queued: {command.Command}; label={command.Label ?? "none"}.");
        }
    }

    private IReadOnlyList<RuntimeCommand> DrainCommands()
    {
        List<RuntimeCommand> commands = [];
        while (_pendingCommands.TryDequeue(out RuntimeCommand? command))
        {
            Interlocked.Decrement(ref _pendingCommandCount);
            if (command is not null)
            {
                commands.Add(command);
            }
            if (commands.Count >= 32)
            {
                break;
            }
        }
        return commands;
    }

    private void Requeue(IReadOnlyList<RuntimeCommand> commands)
    {
        foreach (RuntimeCommand command in commands)
        {
            TryQueueCommand(command);
        }
    }

    private bool TryQueueCommand(RuntimeCommand command)
    {
        int count = Interlocked.Increment(ref _pendingCommandCount);
        if (count > MaximumPendingCommands)
        {
            Interlocked.Decrement(ref _pendingCommandCount);
            return false;
        }

        _pendingCommands.Enqueue(command);
        return true;
    }

    private RuntimeStatusMessage CreateStatusMessage(IReadOnlyList<RuntimeCommand> commands)
    {
        long sequence = Interlocked.Increment(ref _heartbeatSequence);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long monotonicTicks = Stopwatch.GetTimestamp();
        RuntimeCommand[] chronologyCommands = commands.Where(ChronologySampler.IsChronologyCommand).ToArray();
        RuntimeCommand[] observerCommands = commands.Where(command => !ChronologySampler.IsChronologyCommand(command)).ToArray();
        _chronologySampler.ApplyCommands(chronologyCommands);
        Process? game = FindGameProcess();

        if (game is null)
        {
            Requeue(observerCommands);
            IReadOnlyList<TelemetryEventMessage> releaseEvents = _observer.Detach();
            _chronologySampler.UpdateTarget(null, []);
            ChronologyFrame chronologyFrame = _chronologySampler.DrainFrame();
            return CreateMessage(
                now,
                monotonicTicks,
                RuntimeState.WaitingForGame,
                "Companion runtime is active. Waiting for DBXV2.exe.",
                null,
                false,
                false,
                sequence,
                null,
                false,
                0,
                null,
                0,
                [],
                releaseEvents,
                ScannerConfiguration.Default,
                "Waiting for DBXV2.exe.",
                chronologyFrame);
        }

        ProcessAccessProbe access = GetReadAccessProbe(game.Id, now);
        if (!access.CanRead)
        {
            Requeue(observerCommands);
            IReadOnlyList<TelemetryEventMessage> releaseEvents = _observer.Detach();
            _chronologySampler.UpdateTarget(null, []);
            ChronologyFrame chronologyFrame = _chronologySampler.DrainFrame();
            return CreateMessage(
                now,
                monotonicTicks,
                RuntimeState.ReadPermissionDenied,
                $"DBXV2.exe was detected, but read-only access was denied. {access.Error}",
                game.Id,
                access.CanQuery,
                access.CanRead,
                sequence,
                null,
                false,
                0,
                null,
                0,
                [],
                releaseEvents,
                ScannerConfiguration.Default,
                "Read-only process access is unavailable.",
                chronologyFrame);
        }

        ObserverFrame frame = _observer.Observe(game.Id, observerCommands, now, monotonicTicks);
        _chronologySampler.UpdateTarget(game.Id, frame.Fighters);
        ChronologyFrame activeChronologyFrame = _chronologySampler.DrainFrame();
        return new RuntimeStatusMessage(
            RuntimeProtocol.ProtocolVersion,
            now,
            monotonicTicks,
            Stopwatch.Frequency,
            Environment.ProcessId,
            frame.State,
            frame.Detail,
            game.Id,
            true,
            true,
            sequence,
            true,
            frame.GameVersion,
            frame.PatcherDetected,
            frame.PatcherImageSize,
            frame.BattleCoreAddress,
            frame.StableCoreSamples,
            frame.Fighters,
            frame.Events,
            frame.RawMemoryObservations,
            frame.Scanner,
            frame.ScanObservations,
            activeChronologyFrame.Status,
            activeChronologyFrame.Samples,
            CreateRuntimeAccessStatus(
                frame.ActiveLocatorId,
                frame.LocatorDetail,
                frame.LocatorReports,
                frame.MemoryMetrics,
                activeChronologyFrame.MemoryMetrics));
    }

    private Process? FindGameProcess()
    {
        if (_trackedGameProcess is not null)
        {
            try
            {
                if (!_trackedGameProcess.HasExited)
                {
                    return _trackedGameProcess;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // Process state can become unavailable during teardown.
            }

            DisposeTrackedGameProcess();
        }

        Process[] processes = Process.GetProcessesByName("DBXV2");
        if (processes.Length == 0)
        {
            return null;
        }

        Process selected = processes
            .OrderByDescending(TryGetStartTimeUtc)
            .ThenByDescending(process => process.Id)
            .First();
        foreach (Process process in processes)
        {
            if (process.Id != selected.Id)
            {
                process.Dispose();
            }
        }

        _trackedGameProcess = selected;
        _cachedAccessProcessId = null;
        _cachedAccessProbe = null;
        _nextAccessProbeUtc = DateTimeOffset.MinValue;
        return selected;
    }

    private ProcessAccessProbe GetReadAccessProbe(int processId, DateTimeOffset now)
    {
        if (_cachedAccessProbe is not null &&
            _cachedAccessProcessId == processId &&
            now < _nextAccessProbeUtc)
        {
            return _cachedAccessProbe;
        }

        _cachedAccessProbe = NativeMethods.ProbeReadAccess(processId);
        _cachedAccessProcessId = processId;
        _nextAccessProbeUtc = now + AccessProbeInterval;
        return _cachedAccessProbe;
    }

    private void DisposeTrackedGameProcess()
    {
        _trackedGameProcess?.Dispose();
        _trackedGameProcess = null;
        _cachedAccessProcessId = null;
        _cachedAccessProbe = null;
        _nextAccessProbeUtc = DateTimeOffset.MinValue;
    }

    private static DateTime TryGetStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static RuntimeStatusMessage CreateMessage(
        DateTimeOffset timestamp,
        long monotonicTicks,
        RuntimeState state,
        string detail,
        int? gameProcessId,
        bool canQuery,
        bool canRead,
        long sequence,
        string? gameVersion,
        bool patcherDetected,
        uint patcherImageSize,
        ulong? battleCoreAddress,
        int stableCoreSamples,
        IReadOnlyList<FighterSnapshot> fighters,
        IReadOnlyList<TelemetryEventMessage> events,
        ScannerConfiguration configuration,
        string scannerDetail,
        ChronologyFrame chronologyFrame) =>
        new(
            RuntimeProtocol.ProtocolVersion,
            timestamp,
            monotonicTicks,
            Stopwatch.Frequency,
            Environment.ProcessId,
            state,
            detail,
            gameProcessId,
            canQuery,
            canRead,
            sequence,
            true,
            gameVersion,
            patcherDetected,
            patcherImageSize,
            battleCoreAddress,
            stableCoreSamples,
            fighters,
            events,
            [],
            new ScannerStatusMessage(
                true,
                false,
                null,
                null,
                scannerDetail,
                configuration,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                null),
            [],
            chronologyFrame.Status,
            chronologyFrame.Samples,
            CreateRuntimeAccessStatus(
                null,
                scannerDetail,
                [],
                EmptyMetrics("observer"),
                chronologyFrame.MemoryMetrics));

    private static RuntimeAccessStatusMessage CreateRuntimeAccessStatus(
        string? activeLocatorId,
        string locatorDetail,
        IReadOnlyList<BattleCoreLocatorReport> locatorReports,
        MemoryAccessMetricsMessage observerMetrics,
        MemoryAccessMetricsMessage chronologyMetrics) =>
        new(
            "Runtime Access Architecture Gate 0",
            true,
            false,
            false,
            false,
            activeLocatorId,
            locatorDetail,
            locatorReports,
            AddressProvenanceCatalog.Entries,
            observerMetrics,
            chronologyMetrics,
            TelemetryComparisonPolicy.Describe());

    private static MemoryAccessMetricsMessage EmptyMetrics(string lane) => new(lane, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private void LogStateTransition(RuntimeStatusMessage status)
    {
        if (_lastLoggedState == status.State &&
            _lastLoggedGameProcessId == status.GameProcessId &&
            _lastLoggedBattleCore == status.BattleCoreAddress)
        {
            return;
        }

        _lastLoggedState = status.State;
        _lastLoggedGameProcessId = status.GameProcessId;
        _lastLoggedBattleCore = status.BattleCoreAddress;
        RuntimeLog.Write(
            $"State={status.State}; GamePid={status.GameProcessId?.ToString() ?? "none"}; " +
            $"BattleCore={(status.BattleCoreAddress.HasValue ? $"0x{status.BattleCoreAddress.Value:X16}" : "none")}; " +
            $"Fighters={status.Fighters.Count}; Baseline={status.Scanner.HasBaseline}; " +
            $"PendingScan={status.Scanner.PendingObservationCount}; ChronologyActive={status.Chronology.SamplingActive}; " +
            $"PendingChronology={status.Chronology.PendingSampleCount}; Locator={status.RuntimeAccess.ActiveLocatorId ?? "none"}; " +
            $"ObserverReads={status.RuntimeAccess.ObserverMetrics.ReadProcessMemoryCalls}; " +
            $"ChronologyReads={status.RuntimeAccess.ChronologyMetrics.ReadProcessMemoryCalls}; Detail={status.Detail}");
    }

    private async Task DelayBeforeReconnectAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
