namespace PowerScalerLabs.ProbeHost;

internal static class ProbeLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerScaler Labs", "Logs", "ProbeHost.log");

    internal static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] [ProbeHost] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // External diagnostics must not destabilize the lifecycle controller.
        }
    }
}
