using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceLens.Core;
using SpaceLens.Core.Diagnostics;
using SpaceLens.Services;

namespace SpaceLens.ViewModels;

public sealed partial class FindingViewModel(Finding finding, HealthViewModel page) : ObservableObject
{
    public Finding Finding { get; } = finding;
    public Severity Severity => Finding.Severity;
    public string Area => Finding.Area switch
    {
        FindingArea.Cpu => "CPU",
        FindingArea.Memory => "Memory",
        FindingArea.Disk => "Disk",
        FindingArea.Network => "Network",
        FindingArea.Startup => "Startup",
        _ => "System",
    };
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string Recommendation => Finding.Recommendation;
    public bool HasRecommendation => Finding.Severity != Severity.Good;
    public IEnumerable<FindingActionViewModel> Actions => Finding.Actions.Select(a => new FindingActionViewModel(a, page));
}

public sealed partial class FindingActionViewModel(FindingAction action, HealthViewModel page) : ObservableObject
{
    public FindingAction Action { get; } = action;
    public string Label => Action.Label;

    [RelayCommand]
    private Task Run() => page.RunAction(Action);
}

public sealed record UsageRow(string Name, string Value, double Percent);

public sealed partial class HealthViewModel : ObservableObject
{
    public ObservableCollection<FindingViewModel> Findings { get; } = [];
    public ObservableCollection<StartupItem> StartupItems { get; } = [];
    public ObservableCollection<UsageRow> TopCpu { get; } = [];
    public ObservableCollection<UsageRow> TopMemory { get; } = [];
    public ObservableCollection<UsageRow> TopDisk { get; } = [];
    public ObservableCollection<UsageRow> TopNetwork { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckCommand))] private bool _isChecking;
    [ObservableProperty] private int _score = 100;
    [ObservableProperty] private string _verdict = "Not checked yet";
    [ObservableProperty] private string _verdictDetail = "";
    [ObservableProperty] private Severity _overall = Severity.Good;
    [ObservableProperty] private string _systemText = "";
    [ObservableProperty] private string _hardwareText = "";
    [ObservableProperty] private string _observedText = "";
    [ObservableProperty] private string _networkHeader = "Network";

    public bool IsElevated => AppServices.Instance.IsElevated;

    private bool CanCheck() => !IsChecking;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    public async Task Check()
    {
        IsChecking = true;
        try
        {
            var (info, report) = await Task.Run(() =>
            {
                var i = SystemInfo.Collect();
                return (i, PerformanceDiagnostics.Analyze(AppServices.Instance.Monitor.Snapshot(), i));
            });

            Findings.Clear();
            foreach (var f in report.Findings) Findings.Add(new FindingViewModel(f, this));

            Score = report.Score;
            int critical = report.Findings.Count(f => f.Severity == Severity.Critical);
            int warnings = report.Findings.Count(f => f.Severity == Severity.Warning);
            Overall = critical > 0 ? Severity.Critical : warnings > 0 ? Severity.Warning : Severity.Good;
            Verdict = critical > 0 ? "Needs attention" : warnings > 0 ? "Could be better" : "Looking good";
            VerdictDetail = critical + warnings == 0
                ? "No performance problems found right now."
                : $"{critical} serious and {warnings} minor issue{(critical + warnings == 1 ? "" : "s")} found. Start at the top of the list.";
            ObservedText = report.Observed.TotalSeconds >= 1
                ? $"Based on {report.Observed.TotalSeconds:0} seconds of measurements (SpaceLens keeps up to 5 minutes)."
                : "Measurements are still starting.";

            SystemText = $"{info.OsName} {info.OsBuild}";
            HardwareText = $"{info.CpuName} · {info.LogicalProcessors} logical processors · {ByteFormat.Format(info.TotalMemory)} RAM · power plan: {PowerPlanName(info.PowerPlan)}";

            StartupItems.Clear();
            foreach (var s in info.StartupItems) StartupItems.Add(s);

            Fill(TopCpu, report.TopCpu.Take(5), a => a.CpuPercent, a => $"{a.CpuPercent:0.#}%", 100);
            long memTotal = Math.Max(1, info.TotalMemory);
            Fill(TopMemory, report.TopMemory.Take(5), a => a.PrivateBytes, a => ByteFormat.Format(a.PrivateBytes), memTotal);
            Fill(TopDisk, report.TopDisk.Take(5).Where(a => a.IoBytesPerSec > 0), a => a.IoBytesPerSec, a => ByteFormat.Rate(a.IoBytesPerSec), 0);
            if (AppServices.Instance.Monitor.PerProcessNetwork)
            {
                NetworkHeader = "Network (per app)";
                Fill(TopNetwork, report.TopNetwork.Take(5).Where(a => a.NetBytesPerSec > 0), a => a.NetBytesPerSec, a => ByteFormat.Rate(a.NetBytesPerSec), 0);
            }
            else
            {
                NetworkHeader = "Open connections";
                Fill(TopNetwork, report.TopNetwork.Take(5).Where(a => a.Connections > 0), a => a.Connections, a => $"{a.Connections} conn.", 0);
            }
        }
        finally
        {
            IsChecking = false;
        }
    }

    private static void Fill(ObservableCollection<UsageRow> target, IEnumerable<AppUsage> apps, Func<AppUsage, double> value, Func<AppUsage, string> text, double scale)
    {
        target.Clear();
        var list = apps.ToList();
        double max = scale > 0 ? scale : Math.Max(1, list.Count > 0 ? list.Max(value) : 1);
        foreach (var a in list)
        {
            string name = a.ProcessCount > 1 ? $"{a.DisplayName} ×{a.ProcessCount}" : a.DisplayName;
            target.Add(new UsageRow(name, text(a), Math.Clamp(100 * value(a) / max, 0, 100)));
        }
    }

    private static string PowerPlanName(PowerPlan plan) => plan switch
    {
        PowerPlan.PowerSaver => "Power saver",
        PowerPlan.Balanced => "Balanced",
        PowerPlan.HighPerformance => "High performance",
        PowerPlan.UltimatePerformance => "Ultimate performance",
        PowerPlan.Custom => "Custom",
        _ => "Unknown",
    };

    internal async Task RunAction(FindingAction action)
    {
        switch (action.Kind)
        {
            case FindingActionKind.EndProcess:
                if (!int.TryParse(action.Argument, out int pid)) return;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!AppServices.Confirm($"End {p.ProcessName} (PID {pid})?\n\nUnsaved work in that app will be lost.", "End task", MessageBoxImage.Warning)) return;
                    p.Kill();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not end the process: " + ex.Message, "SpaceLens", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                break;

            case FindingActionKind.OpenUri:
                AppServices.ShellOpen(action.Argument);
                return;

            case FindingActionKind.RunCommand:
                if (action.Argument.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    AppServices.ShellOpen(action.Argument);
                    return;
                }
                if (!AppServices.Confirm($"Run this command?\n\n{action.Argument}")) return;
                await Task.Run(() =>
                {
                    using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + action.Argument)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = action.RequiresAdmin,
                        Verb = action.RequiresAdmin ? "runas" : "",
                    });
                    p?.WaitForExit(30_000);
                });
                break;

            case FindingActionKind.OpenCleanup:
                AppServices.Instance.Navigate?.Invoke("cleanup");
                return;
        }

        await Task.Delay(1500);
        await Check();
    }

    [RelayCommand]
    private static void OpenStartupSettings() => AppServices.ShellOpen("ms-settings:startupapps");

    [RelayCommand]
    private static void OpenTaskManagerStartup() => AppServices.ShellOpen("taskmgr.exe");

    [RelayCommand]
    private static void RestartAsAdmin() => AppServices.RestartElevated();
}
