using System.Diagnostics;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.ProbeHost;

internal sealed class ProbeHostService : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ProbePipeServer _pipeServer = new();
    private ProbeInjectionSession? _session;
    private ProbeState _state = ProbeState.Starting;
    private string _detail = "ProbeHost is starting.";
    private long _heartbeatSequence;
    private string _buildId = "PowerScaler Labs - Native Causal Probe Foundation Gate - Runtime Protocol 8 - Probe Protocol 1";

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
                    await _pipeServer.ServeAsync(CreateStatus, HandleCommandAsync, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException exception)
                {
                    ProbeLog.Write($"Pipe disconnected: {exception.Message}");
                }
                catch (Exception exception)
                {
                    ProbeLog.Write($"Pipe lifecycle error: {exception}");
                }

                if (!_shutdown.IsCancellationRequested)
                {
                    ProbeLog.Write("App disconnected; requesting safe probe detach and host shutdown.");
                    await DetachAsync(CancellationToken.None).ConfigureAwait(false);
                    _shutdown.Cancel();
                }
            }
        }
        finally
        {
            await DetachAsync(CancellationToken.None).ConfigureAwait(false);
        }
        ProbeLog.Write("ProbeHost stopped normally.");
        return 0;
    }

    private async Task HandleCommandAsync(ProbeCommand command, CancellationToken cancellationToken)
    {
        switch (command.Command.Trim().ToLowerInvariant())
        {
            case "attach":
                if (command.GameProcessId is not int processId)
                {
                    SetState(ProbeState.Faulted, "Attach rejected: no DBXV2 PID was supplied.");
                    return;
                }
                await AttachAsync(processId, cancellationToken).ConfigureAwait(false);
                break;
            case "detach":
                await DetachAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "ping":
                break;
            case "shutdown":
                await DetachAsync(CancellationToken.None).ConfigureAwait(false);
                _shutdown.Cancel();
                break;
            default:
                SetState(ProbeState.Faulted, $"Unknown probe command: {command.Command}");
                break;
        }
    }

    private async Task AttachAsync(int processId, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                SetState(ProbeState.Faulted, "Attach rejected: a probe session already exists.");
                return;
            }
            SetState(ProbeState.Injecting, $"Validating DBXV2 PID {processId} and loading NativeProbe.");
            ProbeLog.Write($"Probe attach requested for PID {processId}.");
            string probePath = Path.Combine(AppContext.BaseDirectory, "PowerScalerLabs.NativeProbe.dll");
            ProbeInjectionSession session = await ProbeInjector.AttachAsync(processId, probePath, cancellationToken)
                .ConfigureAwait(false);
            _session = session;
            SetState(ProbeState.WaitingForHandshake, "NativeProbe loaded; waiting for ABI handshake and heartbeat.");
            if (!await session.WaitForReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                SetState(ProbeState.Faulted, $"Native handshake failed; status {session.SharedMemory.InitializationStatus}.");
                ProbeLog.Write(_detail);
                return;
            }
            SetState(ProbeState.Ready, "Native ABI handshake established; instrumentation is inactive.");
            ProbeLog.Write("Native ABI handshake established; probe heartbeat healthy.");
        }
        catch (Exception exception)
        {
            SetState(ProbeState.Faulted, $"Attach failed: {exception.Message}");
            ProbeLog.Write($"Attach failed: {exception}");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task DetachAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProbeInjectionSession? session = _session;
            if (session is null)
            {
                if (!_shutdown.IsCancellationRequested)
                {
                    SetState(ProbeState.Idle, "Available; no probe is attached.");
                }
                return;
            }
            SetState(ProbeState.ShuttingDown, "Requesting native safe-to-unload state.");
            ProbeLog.Write("Probe detach requested.");
            bool unloaded;
            try
            {
                unloaded = await session.ShutdownAndUnloadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SetState(ProbeState.Faulted, $"Detach failed before unload confirmation: {exception.Message}");
                ProbeLog.Write($"Detach failure: {exception}");
                return;
            }
            if (!unloaded)
            {
                SetState(ProbeState.Faulted, "NativeProbe did not confirm safe unload; DLL was left loaded.");
                ProbeLog.Write(_detail);
                return;
            }
            session.Dispose();
            _session = null;
            SetState(ProbeState.Idle, "Probe detached cleanly; DBXV2 remains running.");
            ProbeLog.Write("FreeLibrary completed and remote probe module removal was confirmed.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private ProbeStatusMessage CreateStatus()
    {
        ProbeInjectionSession? session = _session;
        if (session is not null)
        {
            if (!session.IsGameAlive)
            {
                session.Dispose();
                _session = null;
                SetState(ProbeState.Idle, "DBXV2 exited; probe session resources were released.");
            }
            else
            {
                session.SharedMemory.WriteHostHeartbeat();
                if (_state == ProbeState.Ready)
                {
                    long age = Stopwatch.GetTimestamp() - session.SharedMemory.ProbeHeartbeatQpc;
                    if (age > Stopwatch.Frequency * 3)
                    {
                        SetState(ProbeState.Faulted, "Native probe heartbeat is stale; instrumentation remains inactive.");
                    }
                }
            }
        }

        session = _session;
        return new ProbeStatusMessage(
            ProbeProtocol.ProtocolVersion,
            ProbeProtocol.NativeAbiVersion,
            DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(),
            Stopwatch.Frequency,
            Environment.ProcessId,
            session?.GameProcess.Id,
            _state,
            _detail,
            session is not null,
            session is not null && session.SharedMemory.IsHandshakeValid() &&
                session.SharedMemory.State is ProbeSharedMemory.NativeState.Ready or ProbeSharedMemory.NativeState.Inert,
            Interlocked.Increment(ref _heartbeatSequence),
            session?.SharedMemory.ProbeHeartbeatQpc ?? 0,
            session?.SharedMemory.DroppedEventCount ?? 0,
            session?.SharedMemory.ActiveWatchpointCount ?? 0,
            session?.SharedMemory.SessionId,
            _buildId);
    }

    private void SetState(ProbeState state, string detail)
    {
        _state = state;
        _detail = detail;
    }

    private void LoadBuildId()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "BUILD_ID.txt");
        if (File.Exists(path))
        {
            _buildId = File.ReadAllText(path).Trim();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _session?.Dispose();
        _lifecycleGate.Dispose();
        _shutdown.Dispose();
    }
}
