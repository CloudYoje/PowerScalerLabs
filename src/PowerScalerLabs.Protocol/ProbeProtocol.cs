using System.Text.Json.Serialization;

namespace PowerScalerLabs.Protocol;

public static class ProbeProtocol
{
    public const string PipeName = "PowerScalerLabs.ProbeHost.CausalResearchGate";
    public const int ProtocolVersion = 1;
    public const int NativeAbiVersion = 1;
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

public sealed record ProbeCommand(string Command, int? GameProcessId = null);

public sealed record ProbeEventMessage(
    long Sequence,
    long MonotonicTicks,
    int EventType,
    int ThreadId,
    ulong InstructionPointer,
    ulong TraceSessionId,
    ulong WatchId);
