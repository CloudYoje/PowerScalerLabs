using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.App;

internal sealed class ProbeHostClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationToken _lifetime;
    private readonly Action<ProbeStatusMessage> _statusReceived;
    private readonly Action<ProbeEventMessage> _eventReceived;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ProbeCommandResult>> _pending = new();
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private Task? _connectionTask;
    private long _nextCommandId;
    private bool _closing;

    internal ProbeHostClient(CancellationToken lifetime, Action<ProbeStatusMessage> statusReceived,
        Action<string> log, Action<ProbeEventMessage>? eventReceived = null)
    {
        _lifetime = lifetime;
        _statusReceived = statusReceived;
        _eventReceived = eventReceived ?? (_ => { });
        _log = log;
    }

    internal void Start()
    {
        if (_connectionTask is { IsCompleted: false }) return;
        _connectionTask = ConnectWithBackoffAsync();
    }

    internal async Task<ProbeCommandResult> SendAsync(ProbeCommand command, TimeSpan? timeout = null)
    {
        if (_pending.Count >= ProbeProtocol.MaximumPendingCommands)
            return new(command.CommandId, command.Command, false, "App pending-command limit reached.", ProbeState.Faulted);

        long id = command.CommandId == 0 ? Interlocked.Increment(ref _nextCommandId) : command.CommandId;
        command = command with { CommandId = id };
        TaskCompletionSource<ProbeCommandResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            return new(id, command.Command, false, "Duplicate command ID rejected.", ProbeState.Faulted);

        try
        {
            StreamWriter? writer = _writer;
            if (writer is null) return new(id, command.Command, false, "ProbeHost is not connected.", ProbeState.Disconnected);
            await _writeGate.WaitAsync(_lifetime).ConfigureAwait(false);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions)).ConfigureAwait(false); }
            finally { _writeGate.Release(); }

            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            bounded.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
            return await completion.Task.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(id, command.Command, false, "Command result timed out or was canceled.", ProbeState.Disconnected);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return new(id, command.Command, false, exception.Message, ProbeState.Disconnected);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    internal ProbeCommand CreateCommand(string command, int? gameProcessId = null, ulong? traceSessionId = null,
        ulong? watchId = null, int? eventCount = null, int? intervalMilliseconds = null) =>
        new(Interlocked.Increment(ref _nextCommandId), command, gameProcessId, traceSessionId, watchId,
            EventCount: eventCount, EventIntervalMilliseconds: intervalMilliseconds);

    internal async Task<ProbeCommandResult> ShutdownAsync(TimeSpan timeout)
    {
        _closing = true;
        ProbeCommandResult result = await SendAsync(CreateCommand("shutdown"), timeout).ConfigureAwait(false);
        if (!result.Success) _log($"Probe cleanup unresolved: {result.Detail}");
        ClosePipe();
        return result;
    }

    private async Task ConnectWithBackoffAsync()
    {
        int[] delays = [0, 100, 250, 500, 1000];
        foreach (int delay in delays)
        {
            if (_lifetime.IsCancellationRequested || _closing) return;
            if (delay > 0) await Task.Delay(delay, _lifetime).ConfigureAwait(false);
            try
            {
                EnsureHostStarted();
                NamedPipeClientStream pipe = new(".", ProbeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000, _lifetime).ConfigureAwait(false);
                _pipe = pipe;
                _writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using StreamReader reader = new(pipe, leaveOpen: true);
                _log("Connected to the isolated causal ProbeHost.");
                await ReadMessagesAsync(reader).ConfigureAwait(false);
                ClosePipe();
                if (!_closing) _log("ProbeHost pipe ended; attempting bounded reconnection.");
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception exception) { _log($"ProbeHost connection attempt ended: {exception.Message}"); ClosePipe(); }
        }
    }

    private void EnsureHostStarted()
    {
        if (_process is { HasExited: false }) return;
        _process?.Dispose();
        string hostPath = Path.Combine(AppContext.BaseDirectory, "Probe", "PowerScalerLabs.ProbeHost.exe");
        if (!File.Exists(hostPath)) throw new FileNotFoundException("ProbeHost executable was not found.", hostPath);
        _process = Process.Start(new ProcessStartInfo(hostPath)
        {
            WorkingDirectory = Path.GetDirectoryName(hostPath)!, UseShellExecute = false, CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Windows did not return a ProbeHost process handle.");
        _log($"Started isolated ProbeHost PID {_process.Id}; no game attachment was requested.");
    }

    private async Task ReadMessagesAsync(StreamReader reader)
    {
        while (!_lifetime.IsCancellationRequested && !_closing)
        {
            string? line = await reader.ReadLineAsync(_lifetime).ConfigureAwait(false);
            if (line is null) return;
            ProbeHostMessage? message = JsonSerializer.Deserialize<ProbeHostMessage>(line, JsonOptions);
            if (message?.Status is not null && message.MessageType == ProbeMessageTypes.Status) _statusReceived(message.Status);
            else if (message?.Event is not null && message.MessageType == ProbeMessageTypes.Event) _eventReceived(message.Event);
            else if (message?.CommandResult is not null && message.MessageType == ProbeMessageTypes.CommandResult &&
                _pending.TryGetValue(message.CommandResult.CommandId, out TaskCompletionSource<ProbeCommandResult>? completion))
                completion.TrySetResult(message.CommandResult);
        }
    }

    private void ClosePipe()
    {
        _writer?.Dispose();
        _writer = null;
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose()
    {
        _closing = true;
        ClosePipe();
        foreach (TaskCompletionSource<ProbeCommandResult> pending in _pending.Values) pending.TrySetCanceled();
        _process?.Dispose();
        _writeGate.Dispose();
    }
}
