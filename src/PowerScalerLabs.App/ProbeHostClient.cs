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
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private Task? _connectionTask;

    internal ProbeHostClient(
        CancellationToken lifetime,
        Action<ProbeStatusMessage> statusReceived,
        Action<string> log)
    {
        _lifetime = lifetime;
        _statusReceived = statusReceived;
        _log = log;
    }

    internal void Start()
    {
        if (_process is { HasExited: false })
        {
            return;
        }
        string hostPath = Path.Combine(AppContext.BaseDirectory, "Probe", "PowerScalerLabs.ProbeHost.exe");
        if (!File.Exists(hostPath))
        {
            _log($"ERROR: ProbeHost executable was not found at {hostPath}");
            return;
        }
        try
        {
            _process = Process.Start(new ProcessStartInfo(hostPath)
            {
                WorkingDirectory = Path.GetDirectoryName(hostPath)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (_process is null)
            {
                throw new InvalidOperationException("Windows did not return a ProbeHost process handle.");
            }
            _log($"Started isolated ProbeHost PID {_process.Id}; no game attachment was requested.");
            _connectionTask = ConnectAndReadAsync();
        }
        catch (Exception exception)
        {
            _log($"ERROR: Unable to start ProbeHost: {exception.Message}");
        }
    }

    internal async Task SendAsync(ProbeCommand command)
    {
        StreamWriter? writer = _writer;
        if (writer is null)
        {
            _log("ProbeHost is not connected; the command was not sent.");
            return;
        }
        await _writeGate.WaitAsync(_lifetime).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _log($"ProbeHost command failed: {exception.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ConnectAndReadAsync()
    {
        try
        {
            NamedPipeClientStream pipe = new(".", ProbeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, _lifetime).ConfigureAwait(false);
            _pipe = pipe;
            _writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using StreamReader reader = new(pipe, leaveOpen: true);
            _log("Connected to the isolated causal ProbeHost.");
            while (pipe.IsConnected && !_lifetime.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(_lifetime).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                ProbeStatusMessage? status = JsonSerializer.Deserialize<ProbeStatusMessage>(line, JsonOptions);
                if (status is not null)
                {
                    _statusReceived(status);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log($"ProbeHost connection ended: {exception.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (_writer is not null)
            {
                _writer.WriteLine(JsonSerializer.Serialize(new ProbeCommand("detach"), JsonOptions));
                _writer.WriteLine(JsonSerializer.Serialize(new ProbeCommand("shutdown"), JsonOptions));
                _writer.Flush();
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
        _writer?.Dispose();
        _pipe?.Dispose();
        _process?.Dispose();
        _writeGate.Dispose();
    }
}
