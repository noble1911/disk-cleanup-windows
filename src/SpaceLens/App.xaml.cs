using System.Windows;
using System.Windows.Threading;
using SpaceLens.Services;

namespace SpaceLens;

public partial class App : Application
{
    /// <summary>Unexpected errors are appended here so problems can be diagnosed after the fact.</summary>
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
        // Start collecting resource history right away so the Health Check has data when opened.
        AppServices.Instance.Monitor.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.Instance.Monitor.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + $"\n\nDetails were saved to {LogPath}.",
            "SpaceLens", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    public static void Log(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch (Exception)
        {
            // Logging must never crash the app.
        }
    }
}
