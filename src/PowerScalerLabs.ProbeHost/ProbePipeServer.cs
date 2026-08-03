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
        Func<ProbeCommand, CancellationToken, Task<ProbeCommandResult>> commandHandler,
        Func<IReadOnlyList<ProbeEventMessage>> eventDrain,
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
        ConcurrentQueue<ProbeHostMessage> immediateMessages = new();
        Task readerTask = ReadCommandsAsync(reader, commands, immediateMessages, connection.Token);
        try
        {
            while (pipe.IsConnected && !connection.IsCancellationRequested)
            {
                if (commands.TryDequeue(out ProbeCommand? command))
                {
                    ProbeCommandResult result = await commandHandler(command, connection.Token).ConfigureAwait(false);
                    immediateMessages.Enqueue(ProbeHostMessage.ForCommandResult(result));
                }
                while (immediateMessages.TryDequeue(out ProbeHostMessage? message))
                {
                    await WriteMessageAsync(writer, message).ConfigureAwait(false);
                }
                foreach (ProbeEventMessage traceEvent in eventDrain())
                {
                    await WriteMessageAsync(writer, ProbeHostMessage.ForEvent(traceEvent)).ConfigureAwait(false);
                }
                await WriteMessageAsync(writer, ProbeHostMessage.ForStatus(statusFactory())).ConfigureAwait(false);
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
        ConcurrentQueue<ProbeHostMessage> immediateMessages,
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
                if (command is null || string.IsNullOrWhiteSpace(command.Command))
                {
                    continue;
                }
                if (commands.Count >= ProbeProtocol.MaximumPendingCommands)
                {
                    immediateMessages.Enqueue(ProbeHostMessage.ForCommandResult(new ProbeCommandResult(
                        command.CommandId,
                        command.Command,
                        false,
                        "ProbeHost command queue is full.",
                        ProbeState.Faulted)));
                    continue;
                }
                commands.Enqueue(command);
            }
            catch (JsonException exception)
            {
                ProbeLog.Write($"Malformed App command ignored: {exception.Message}");
            }
        }
    }

    private static Task WriteMessageAsync(StreamWriter writer, ProbeHostMessage message) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions));
}
