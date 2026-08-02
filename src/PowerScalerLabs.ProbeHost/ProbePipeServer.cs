using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.ProbeHost;

internal sealed class ProbePipeServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal async Task ServeAsync(
        Func<ProbeStatusMessage> statusFactory,
        Func<ProbeCommand, CancellationToken, Task> commandHandler,
        CancellationToken cancellationToken)
    {
        await using NamedPipeServerStream pipe = new(
            ProbeProtocol.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        ProbeLog.Write("Waiting for App pipe connection.");
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        ProbeLog.Write("App connected to ProbeHost.");

        using StreamReader reader = new(pipe, leaveOpen: true);
        using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
        using CancellationTokenSource connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConcurrentQueue<ProbeCommand> commands = new();
        Task readerTask = ReadCommandsAsync(reader, commands, connection.Token);
        try
        {
            while (pipe.IsConnected && !connection.IsCancellationRequested)
            {
                while (commands.TryDequeue(out ProbeCommand? command))
                {
                    await commandHandler(command, connection.Token).ConfigureAwait(false);
                }
                string json = JsonSerializer.Serialize(statusFactory(), JsonOptions);
                await writer.WriteLineAsync(json).ConfigureAwait(false);
                await Task.Delay(100, connection.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            connection.Cancel();
            try
            {
                await readerTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
            }
        }
    }

    private static async Task ReadCommandsAsync(
        StreamReader reader,
        ConcurrentQueue<ProbeCommand> commands,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }
            try
            {
                ProbeCommand? command = JsonSerializer.Deserialize<ProbeCommand>(line, JsonOptions);
                if (command is not null && !string.IsNullOrWhiteSpace(command.Command))
                {
                    commands.Enqueue(command);
                }
            }
            catch (JsonException exception)
            {
                ProbeLog.Write($"Malformed App command ignored: {exception.Message}");
            }
        }
    }
}
