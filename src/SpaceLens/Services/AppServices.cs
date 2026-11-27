using System.Diagnostics;
using System.Windows;
using SpaceLens.Core.Diagnostics;
using SpaceLens.Core.Monitoring;
using SpaceLens.Core.Scanning;

namespace SpaceLens.Services;

/// <summary>Shared state for the whole app: the background monitor and the latest disk scan.</summary>
public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    private AppServices() { }

    public SystemMonitor Monitor { get; } = new();

    public bool IsElevated { get; } = SystemInfo.CurrentProcessIsElevated();

    private ScanResult? _lastScan;

    public ScanResult? LastScan
    {
        get => _lastScan;
        set
        {
            _lastScan = value;
            ScanCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ScanCompleted;

    /// <summary>Set by the main window; switches the visible page.</summary>
    public Action<string>? Navigate { get; set; }

    public static void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the UAC prompt.
        }
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "SpaceLens", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void ShellOpen(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "SpaceLens", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public static bool Confirm(string message, string title = "SpaceLens", MessageBoxImage icon = MessageBoxImage.Question) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No) == MessageBoxResult.Yes;
}
