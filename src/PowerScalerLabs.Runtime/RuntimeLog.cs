using System.IO;

namespace PowerScalerLabs.Runtime;

internal static class RuntimeLog
{
    private static readonly object Sync = new();
    internal static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(BuildLogPath(), $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never terminate the companion runtime.
        }
    }

    private static string BuildLogPath()
    {
        string persistentRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PowerScaler Labs");
        string logsDirectory = Path.Combine(persistentRoot, "Logs");
        Directory.CreateDirectory(logsDirectory);
        return Path.Combine(logsDirectory, "PowerScalerLabs.Runtime.log");
    }
}
