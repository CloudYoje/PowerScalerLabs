using System.Text.Json.Serialization;

namespace PowerScalerLabs.Protocol;

public static class ProbeProtocol
{
    public const string PipeName = "PowerScalerLabs.ProbeHost.CausalResearchGate";
    public const int ProtocolVersion = 2;
    public const int NativeAbiVersion = 2;
    public const int MaximumPendingCommands = 64;
    public const int MaximumEventBatch = 10_000;
}

public static class ProbeMessageTypes
{
    public const string Status = "status";
    public const string Event = "event";
    public const string CommandResult = "command_result";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProbeState
{
    Unavailable,
    Starting,
    Idle,
    Injecting,
    WaitingForHandshake,
    Ready,
    Faulted,
    ShuttingDown,
    Disconnected
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProbeEventType
{
    Synthetic = 1
}

public sealed record ProbeStatusMessage(
    int ProtocolVersion,
    int NativeAbiVersion,
    DateTimeOffset TimestampUtc,
    long MonotonicTicks,
    long MonotonicFrequency,
    int HostProcessId,
    int? GameProcessId,
    ProbeState State,
    string Detail,
    bool ProbeDllLoaded,
    bool NativeHandshakeEstablished,
    long HeartbeatSequence,
    long NativeHeartbeatMonotonicTicks,
    long DroppedNativeEventCount,
    int ActiveWatchpointCount,
    string? SessionId,
    string BuildId);

public sealed record ProbeCommand(
    long CommandId,
    string Command,
    int? GameProcessId = null,
    ulong? TraceSessionId = null,
    ulong? WatchId = null,
    ulong? Address = null,
    int? Width = null,
    int? AccessType = null,
    int? EventCount = null,
    int? EventIntervalMilliseconds = null);

public sealed record ProbeCommandResult(
    long CommandId,
    string Command,
    bool Success,
    string Detail,
    ProbeState NativeState,
    int GeneratedEventCount = 0,
    long DroppedNativeEventCount = 0);

public sealed record ProbeEventMessage(
    ulong Sequence,
    long MonotonicTicks,
    ProbeEventType EventType,
    int ThreadId,
    ulong TraceSessionId,
    ulong WatchId,
    ulong TrapRip,
    ulong StackPointer,
    ulong Flags,
    IReadOnlyList<ulong> Registers,
    ulong Dr6,
    ulong Dr7,
    ulong WatchedAddress,
    ulong AccessAddress,
    int AccessWidth,
    int AccessType,
    string Origin);

public sealed record ProbeHostMessage(
    string MessageType,
    ProbeStatusMessage? Status = null,
    ProbeEventMessage? Event = null,
    ProbeCommandResult? CommandResult = null)
{
    public static ProbeHostMessage ForStatus(ProbeStatusMessage status) =>
        new(ProbeMessageTypes.Status, Status: status);

    public static ProbeHostMessage ForEvent(ProbeEventMessage traceEvent) =>
        new(ProbeMessageTypes.Event, Event: traceEvent);

    public static ProbeHostMessage ForCommandResult(ProbeCommandResult result) =>
        new(ProbeMessageTypes.CommandResult, CommandResult: result);
}
