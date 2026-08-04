using System.Collections.Concurrent;
using System.Diagnostics;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.ProbeHost;

internal sealed class ProbeHostService : IDisposable
{
    private const int MaximumBufferedEvents = 20_000;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ProbePipeServer _pipeServer = new();
    private readonly ConcurrentQueue<ProbeEventMessage> _events = new();
    private readonly HashSet<int> _blockedProcessIds = [];
    private ProbeInjectionSession? _session;
    private CancellationTokenSource? _consumerStop;
    private Task? _consumerTask;
    private int _deadProcessCleanupQueued;
    private ProbeState _state = ProbeState.Starting;
    private string _detail = "ProbeHost is starting.";
    private long _heartbeatSequence;
    private string _buildId = "PowerScaler Labs - Generic Two-Lane SIMD Writer Evidence Gate - Runtime Protocol 8 - Probe Protocol 3 - Native ABI 3";

    internal async Task<int> RunAsync()
    {
        LoadBuildId();
        SetState(ProbeState.Idle, "Available; no probe is attached.");
        ProbeLog.Write($"ProbeHost started. PID {Environment.ProcessId}.");
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    await _pipeServer.ServeAsync(CreateStatus, HandleCommandAsync, DrainEvents, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (IOException exception) { ProbeLog.Write($"Pipe disconnected: {exception.Message}"); }
                catch (Exception exception) { ProbeLog.Write($"Pipe lifecycle error: {exception}"); }

                if (!_shutdown.IsCancellationRequested)
                {
                    ProbeLog.Write("App disconnected; immediately shutting down and unloading NativeProbe.");
                    await DetachCoreAsync(CancellationToken.None).ConfigureAwait(false);
                    _shutdown.Cancel();
                }
            }
        }
        finally
        {
            await DetachCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        ProbeLog.Write("ProbeHost stopped normally.");
        return 0;
    }

    private async Task<ProbeCommandResult> HandleCommandAsync(ProbeCommand command, CancellationToken cancellationToken)
    {
        string name = command.Command.Trim().ToLowerInvariant();
        try
        {
            return name switch
            {
                "attach" when command.GameProcessId is int pid => await AttachAsync(command, pid, cancellationToken).ConfigureAwait(false),
                "attach" => Result(command, false, "Attach rejected: no DBXV2 PID was supplied."),
                "detach" => await DetachAsync(command, cancellationToken).ConfigureAwait(false),
                "emit_synthetic_event" => await EmitAsync(command, cancellationToken).ConfigureAwait(false),
                "arm_write_watch" => await ArmWriteWatchAsync(command, cancellationToken).ConfigureAwait(false),
                "disarm_watch" or "disarm_all" => await DisarmWatchAsync(command, cancellationToken).ConfigureAwait(false),
                "ping" => Result(command, true, "ProbeHost is responsive."),
                "shutdown" => await ShutdownAsync(command).ConfigureAwait(false),
                _ => Result(command, false, $"Unknown probe command: {command.Command}")
            };
        }
        catch (OperationCanceledException) { return Result(command, false, "Command canceled before completion."); }
        catch (Exception exception)
        {
            ProbeLog.Write($"Command {name} failed: {exception}");
            return Result(command, false, exception.Message);
        }
    }

    private async Task<ProbeCommandResult> AttachAsync(ProbeCommand command, int processId, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ProbeInjectionSession? candidate = null;
        try
        {
            if (_session is not null) return Result(command, false, "Attach rejected: a probe session already exists.");
            if (IsBlocked(processId)) return Result(command, false, "Attach blocked: a previous unload could not be proven for this live PID.");

            SetState(ProbeState.Injecting, $"Validating DBXV2 PID {processId} and loading NativeProbe.");
            string probePath = Path.Combine(AppContext.BaseDirectory, "PowerScalerLabs.NativeProbe.dll");
            candidate = await ProbeInjector.AttachAsync(processId, probePath, cancellationToken).ConfigureAwait(false);
            SetState(ProbeState.WaitingForHandshake, "NativeProbe loaded; waiting for ABI handshake and heartbeat.");
            if (!await candidate.WaitForReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                bool removed = await RollbackCandidateAsync(candidate).ConfigureAwait(false);
                candidate = null;
                if (!removed) _blockedProcessIds.Add(processId);
                SetState(ProbeState.Faulted, removed ? "Native handshake failed; injection was rolled back." : "Native handshake failed and module removal could not be proven; PID quarantined.");
                return Result(command, false, _detail);
            }

            _session = candidate;
            candidate = null;
            Volatile.Write(ref _deadProcessCleanupQueued, 0);
            StartConsumer(_session);
            SetState(ProbeState.Ready, "Native ABI handshake established; synthetic transport is ready.");
            return Result(command, true, _detail);
        }
        finally
        {
            if (candidate is not null)
            {
                bool removed = await RollbackCandidateAsync(candidate).ConfigureAwait(false);
                if (!removed) _blockedProcessIds.Add(processId);
            }
            _lifecycleGate.Release();
        }
    }

    private async Task<ProbeCommandResult> EmitAsync(ProbeCommand command, CancellationToken cancellationToken)
    {
        ProbeInjectionSession? session = _session;
        if (session is null || _state != ProbeState.Ready) return Result(command, false, "Synthetic event rejected: probe is not ready.");
        int count = command.EventCount ?? 1;
        int interval = command.EventIntervalMilliseconds ?? 0;
        NativeCommandOutcome outcome = await session.SharedMemory.EmitSyntheticEventsAsync(
            command.TraceSessionId ?? 0, command.WatchId ?? 0, count, interval, cancellationToken).ConfigureAwait(false);
        bool success = outcome.ResultCode == 0;
        return new ProbeCommandResult(command.CommandId, command.Command, success,
            success ? $"Native generation completed: {outcome.GeneratedEventCount} event(s)." : $"Native generation failed with result {outcome.ResultCode}.",
            _state, outcome.GeneratedEventCount, session.SharedMemory.DroppedEventCount);
    }

    private async Task<ProbeCommandResult> ArmWriteWatchAsync(ProbeCommand command, CancellationToken cancellationToken)
    {
        ProbeInjectionSession? session = _session;
        if (session is null || _state != ProbeState.Ready) return Result(command, false, "HP write watch rejected: probe is not ready.");
        if (command.TraceSessionId is not ulong traceSessionId || command.WatchId is not ulong watchId ||
            command.Address is not ulong address || address == 0 || command.Width != 4 || command.AccessType != ProbeAccessTypes.Write ||
            command.SimdRegister0 is not int simdRegister0 || command.SimdRegister1 is not int simdRegister1 ||
            simdRegister0 is < 0 or > 15 || simdRegister1 is < 0 or > 15 || simdRegister0 == simdRegister1)
            return Result(command, false, "Write watch rejected: trace/watch IDs, address, write/4, and two distinct XMM selectors are required.");
        if (!session.IsValidWatchAddress(address, 4))
            return Result(command, false, "HP write watch rejected: target address is not a committed readable game page containing four bytes.");
        NativeCommandOutcome outcome = await session.SharedMemory.ArmWriteWatchAsync(
            traceSessionId, watchId, address, simdRegister0, simdRegister1, cancellationToken).ConfigureAwait(false);
        bool success = outcome.ResultCode == 0;
        string detail = success
            ? $"HP write watch armed transactionally across {outcome.GeneratedEventCount} game thread(s)."
            : DescribeInstrumentationFailure(outcome);
        return new ProbeCommandResult(command.CommandId, command.Command, success, detail, _state,
            outcome.GeneratedEventCount, session.SharedMemory.DroppedEventCount);
    }

    private async Task<ProbeCommandResult> DisarmWatchAsync(ProbeCommand command, CancellationToken cancellationToken)
    {
        ProbeInjectionSession? session = _session;
        if (session is null) return Result(command, true, "No probe session is attached; no watch is active.");
        NativeCommandOutcome outcome = await session.SharedMemory.DisarmWatchAsync(cancellationToken).ConfigureAwait(false);
        bool success = outcome.ResultCode == 0;
        if (!success) SetState(ProbeState.Faulted, DescribeInstrumentationFailure(outcome));
        string successDetail = outcome.NonOwnedChangeFlags == 0
            ? "HP write watch disarmed; PowerScaler-owned DR0 state was restored."
            : $"HP write watch disarmed; PowerScaler-owned DR0 state was restored. Non-owned debug state changed on thread " +
              $"{outcome.NonOwnedChangeThreadId} (flags 0x{outcome.NonOwnedChangeFlags:X}); it was preserved without failing the watch.";
        return new ProbeCommandResult(command.CommandId, command.Command, success,
            success ? successDetail : _detail,
            _state, outcome.GeneratedEventCount, session.SharedMemory.DroppedEventCount);
    }

    private static string DescribeInstrumentationFailure(NativeCommandOutcome outcome) => outcome.ResultCode switch
    {
        10 => "Invalid or already-active write-watch request.",
        11 => "Cannot arm PowerScaler HP watch: VEH registration failed.",
        12 => "Cannot arm PowerScaler HP watch: game-thread enumeration failed.",
        13 => "Cannot arm PowerScaler HP watch: thread capacity was exceeded.",
        14 => $"Cannot arm PowerScaler HP watch: thread {outcome.ResultDetail} could not be opened or suspended.",
        15 => $"Cannot arm PowerScaler HP watch: GetThreadContext failed on thread {outcome.ResultDetail}.",
        16 => $"Cannot arm PowerScaler HP watch: DR0 is already active on thread {outcome.ResultDetail}.",
        17 => $"Cannot arm PowerScaler HP watch: SetThreadContext failed on thread {outcome.ResultDetail}.",
        18 => "Cannot complete HP watch cleanup: VEH removal failed.",
        19 => "Cannot arm PowerScaler HP watch: transactional rollback failed.",
        20 => $"Cannot disarm HP watch: thread {outcome.ResultDetail} changed PowerScaler-owned DR0 " +
              $"(expected 0x{outcome.ExpectedOwnedValue:X16}, observed 0x{outcome.ObservedOwnedValue:X16}).",
        21 => $"Cannot disarm HP watch: thread {outcome.ResultDetail} changed PowerScaler-owned DR7 bits " +
              $"(expected 0x{outcome.ExpectedOwnedValue:X16}, observed 0x{outcome.ObservedOwnedValue:X16}).",
        _ => $"Native instrumentation failed with result {outcome.ResultCode}."
    };

    private async Task<ProbeCommandResult> DetachAsync(ProbeCommand command, CancellationToken cancellationToken)
    {
        bool removed = await DetachCoreAsync(cancellationToken).ConfigureAwait(false);
        return Result(command, removed, removed ? "NativeProbe shutdown and module removal confirmed." : _detail);
    }

    private async Task<ProbeCommandResult> ShutdownAsync(ProbeCommand command)
    {
        bool removed = await DetachCoreAsync(CancellationToken.None).ConfigureAwait(false);
        return Result(command, removed, removed ? "ProbeHost shutdown authorized after confirmed module removal." : _detail);
    }

    private async Task<bool> DetachCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProbeInjectionSession? session = _session;
            if (session is null) return true;
            if (!session.IsGameAlive)
            {
                AnnounceDeadSessionCleanup();
                await DisposeDeadSessionLockedAsync(session).ConfigureAwait(false);
                return true;
            }
            SetState(ProbeState.ShuttingDown, "Requesting native safe-to-unload state.");
            bool unloaded;
            try { unloaded = await session.ShutdownAndUnloadAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception)
            {
                SetState(ProbeState.Faulted, $"Detach failed before unload confirmation: {exception.Message}");
                return false;
            }
            if (!unloaded)
            {
                _blockedProcessIds.Add(session.GameProcess.Id);
                SetState(ProbeState.Faulted, "NativeProbe module removal was not proven; PID quarantined.");
                return false;
            }
            if (!session.IsGameAlive)
            {
                AnnounceDeadSessionCleanup();
                await DisposeDeadSessionLockedAsync(session).ConfigureAwait(false);
                return true;
            }
            await StopConsumerAsync().ConfigureAwait(false);
            session.Dispose();
            _session = null;
            SetState(ProbeState.Idle, "Probe detached cleanly; module removal confirmed.");
            return true;
        }
        finally { _lifecycleGate.Release(); }
    }

    private void QueueDeadSessionCleanup(ProbeInjectionSession session)
    {
        if (Interlocked.CompareExchange(ref _deadProcessCleanupQueued, 1, 0) != 0)
        {
            return;
        }
        SetState(ProbeState.Faulted, "DBXV2 exited while a probe session was active; disposing the dead-process session.");
        _ = Task.Run(async () =>
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_session, session) && !session.IsGameAlive)
                {
                    await DisposeDeadSessionLockedAsync(session).ConfigureAwait(false);
                }
            }
            finally { _lifecycleGate.Release(); }
        });
    }

    private void AnnounceDeadSessionCleanup()
    {
        if (Interlocked.CompareExchange(ref _deadProcessCleanupQueued, 1, 0) == 0)
        {
            SetState(ProbeState.Faulted, "DBXV2 exited while a probe session was active; disposing the dead-process session.");
        }
    }

    private async Task DisposeDeadSessionLockedAsync(ProbeInjectionSession session)
    {
        if (!ReferenceEquals(_session, session)) return;
        await StopConsumerAsync().ConfigureAwait(false);
        _session = null;
        session.Dispose();
        SetState(ProbeState.Idle, "Dead DBXV2 probe session disposed.");
    }

    private static async Task<bool> RollbackCandidateAsync(ProbeInjectionSession session)
    {
        try { return await session.ShutdownAndUnloadAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { ProbeLog.Write($"Candidate rollback failed: {exception}"); return false; }
        finally { session.Dispose(); }
    }

    private bool IsBlocked(int processId)
    {
        if (!_blockedProcessIds.Contains(processId)) return false;
        try { if (!Process.GetProcessById(processId).HasExited) return true; }
        catch (ArgumentException) { }
        _blockedProcessIds.Remove(processId);
        return false;
    }

    private void StartConsumer(ProbeInjectionSession session)
    {
        _consumerStop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _consumerTask = Task.Run(() => ConsumeEvents(session, _consumerStop.Token));
    }

    private void ConsumeEvents(ProbeInjectionSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && session.IsGameAlive)
        {
            session.SharedMemory.EventReady.WaitOne(100);
            foreach (ProbeEventMessage traceEvent in session.SharedMemory.DrainCommittedEvents())
            {
                if (_events.Count >= MaximumBufferedEvents) _events.TryDequeue(out _);
                _events.Enqueue(traceEvent.EventType == ProbeEventType.HardwareWriteTrap
                    ? traceEvent with { Origin = session.DescribeTrapContext(traceEvent.TrapRip) }
                    : traceEvent);
            }
        }
    }

    private async Task StopConsumerAsync()
    {
        _consumerStop?.Cancel();
        if (_consumerTask is not null)
        {
            try { await _consumerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _consumerStop?.Dispose();
        _consumerStop = null;
        _consumerTask = null;
    }

    private IReadOnlyList<ProbeEventMessage> DrainEvents()
    {
        List<ProbeEventMessage> result = [];
        while (result.Count < ProbeSharedMemory.EventCapacity && _events.TryDequeue(out ProbeEventMessage? traceEvent)) result.Add(traceEvent);
        return result;
    }

    private ProbeCommandResult Result(ProbeCommand command, bool success, string detail) =>
        new(command.CommandId, command.Command, success, detail, _state, 0, _session?.SharedMemory.DroppedEventCount ?? 0);

    private ProbeStatusMessage CreateStatus()
    {
        ProbeInjectionSession? session = _session;
        if (session is not null)
        {
            if (!session.IsGameAlive)
            {
                QueueDeadSessionCleanup(session);
                session = null;
            }
            else
            {
                session.SharedMemory.WriteHostHeartbeat();
            }
            if (session is not null && session.SharedMemory.State == ProbeSharedMemory.NativeState.Faulted && _state != ProbeState.Faulted)
                SetState(ProbeState.Faulted, "NativeProbe reported an instrumentation fault; complete thread coverage was not retained.");
            if (session is not null && _state == ProbeState.Ready && Stopwatch.GetTimestamp() - session.SharedMemory.ProbeHeartbeatQpc > Stopwatch.Frequency * 3)
                SetState(ProbeState.Faulted, "Native probe heartbeat is stale; only synthetic transport was enabled.");
        }
        return new ProbeStatusMessage(ProbeProtocol.ProtocolVersion, ProbeProtocol.NativeAbiVersion, DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(), Stopwatch.Frequency, Environment.ProcessId, session?.GameProcess.Id, _state, _detail,
            session is not null, session is not null && session.SharedMemory.IsHandshakeValid(),
            Interlocked.Increment(ref _heartbeatSequence), session?.SharedMemory.ProbeHeartbeatQpc ?? 0,
            session?.SharedMemory.DroppedEventCount ?? 0, session?.SharedMemory.ActiveWatchpointCount ?? 0,
            session?.SharedMemory.SessionId, _buildId,
            session?.SharedMemory.EligibleThreadCount ?? 0, session?.SharedMemory.InstrumentedThreadCount ?? 0,
            session?.SharedMemory.ExitedThreadCount ?? 0, session?.SharedMemory.NewlyArmedThreadCount ?? 0,
            session?.SharedMemory.ConflictThreadCount ?? 0, session?.SharedMemory.NonOwnedChangeFlags ?? 0,
            session?.SharedMemory.NonOwnedChangeThreadId ?? 0);
    }

    private void SetState(ProbeState state, string detail) { _state = state; _detail = detail; ProbeLog.Write(detail); }
    private void LoadBuildId() { string path = Path.Combine(AppContext.BaseDirectory, "BUILD_ID.txt"); if (File.Exists(path)) _buildId = File.ReadAllText(path).Trim(); }

    public void Dispose()
    {
        _shutdown.Cancel();
        _consumerStop?.Cancel();
        _session?.Dispose();
        _consumerStop?.Dispose();
        _lifecycleGate.Dispose();
        _shutdown.Dispose();
    }
}
