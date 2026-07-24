namespace PowerScalerLabs.App.Overlay;

internal sealed record OverlayViewState(
    string RuntimeState,
    bool RuntimeConnected,
    bool GameDetected,
    int FighterCount,
    bool IsRecording,
    string RecordingSession,
    bool HasBaseline,
    string BaselineLabel,
    int PendingObservations,
    int ChangedObservations,
    int StableObservations,
    int DroppedObservations,
    string Detail);
