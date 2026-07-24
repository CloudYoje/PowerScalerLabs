using System;
using System.Threading.Tasks;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PowerScalerLabs.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            $"PowerScaler Labs encountered an unexpected error.\n\n{e.Exception.Message}\n\nA crash log was written to the Logs folder.",
            "PowerScaler Labs",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Current.Shutdown(-1);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog(exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        e.SetObserved();
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PowerScaler Labs",
                "Logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "App-Crash.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}]\n{exception}\n\n");
        }
        catch
        {
            // Crash reporting must never mask the original failure.
        }
    }
}
