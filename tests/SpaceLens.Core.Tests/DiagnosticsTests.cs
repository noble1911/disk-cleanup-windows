using SpaceLens.Core.Diagnostics;
using SpaceLens.Core.Monitoring;

namespace SpaceLens.Core.Tests;

public class DiagnosticsTests
{
    private const long GB = 1024L * 1024 * 1024;

    private static ProcessSample Proc(int pid, string name, double cpu = 0, long priv = 100 * 1024 * 1024) => new()
    {
        Key = new ProcessKey(pid, 1),
        Name = name,
        CpuPercent = cpu,
        PrivateBytes = priv,
    };

    private static MonitorSnapshot Snapshot(int seconds, Func<int, SystemSample> make)
    {
        var history = Enumerable.Range(0, seconds).Select(make).ToList();
        return new MonitorSnapshot { History = history, Trends = [], LogicalProcessors = 8 };
    }

    private static SystemInfo Info(params DriveSummary[] drives) => new()
    {
        TotalMemory = 16 * GB,
        PowerPlan = PowerPlan.Balanced,
        Drives = drives,
    };

    [Fact]
    public void Healthy_system_reports_good_findings_only()
    {
        var snap = Snapshot(30, i => new SystemSample
        {
            Time = DateTime.Now.AddSeconds(i),
            CpuPercent = 10,
            MemoryTotal = 16 * GB,
            MemoryAvailable = 10 * GB,
            DiskActivePercent = 5,
            Uptime = TimeSpan.FromHours(5),
            Processes = [Proc(100, "notepad.exe", 1)],
        });

        var report = PerformanceDiagnostics.Analyze(snap, Info());

        Assert.All(report.Findings, f => Assert.Equal(Severity.Good, f.Severity));
        Assert.Equal(100, report.Score);
    }

    [Fact]
    public void Cpu_hog_and_memory_pressure_are_reported_with_the_culprit()
    {
        var snap = Snapshot(30, i => new SystemSample
        {
            Time = DateTime.Now.AddSeconds(i),
            CpuPercent = 95,
            MemoryTotal = 16 * GB,
            MemoryAvailable = GB,
            CommitTotal = 10 * GB,
            CommitLimit = 20 * GB,
            Uptime = TimeSpan.FromHours(5),
            Processes =
            [
                Proc(10, "miner.exe", 70, 2 * GB),
                Proc(20, "chrome.exe", 10, 4 * GB),
                Proc(21, "chrome.exe", 5, 4 * GB),
            ],
        });

        var report = PerformanceDiagnostics.Analyze(snap, Info());

        Assert.Contains(report.Findings, f => f.Severity == Severity.Critical && f.Title == "CPU is maxed out");
        var hog = Assert.Single(report.Findings, f => f.Title.StartsWith("miner.exe"));
        Assert.Equal(Severity.Critical, hog.Severity);
        Assert.Contains(hog.Actions, a => a.Kind == FindingActionKind.EndProcess && a.Argument == "10");

        Assert.Contains(report.Findings, f => f.Severity == Severity.Critical && f.Title == "Memory is almost full");
        var chrome = Assert.Single(report.Findings, f => f.Title.StartsWith("Google Chrome is using") && f.Title.EndsWith("of memory"));
        Assert.Empty(chrome.Actions); // never offer to kill one of many browser processes
        Assert.Contains("tab", chrome.Recommendation, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("Google Chrome", report.TopMemory[0].DisplayName);
        Assert.Equal(2, report.TopMemory[0].ProcessCount);
        Assert.True(report.Score < 50);
    }

    [Fact]
    public void Nearly_full_system_drive_links_to_cleanup()
    {
        var snap = Snapshot(1, _ => new SystemSample { Time = DateTime.Now, MemoryTotal = 16 * GB, MemoryAvailable = 8 * GB });
        var drive = new DriveSummary { Name = @"C:\", Type = DriveType.Fixed, Total = 256 * GB, Free = 3 * GB, IsSystem = true };

        var report = PerformanceDiagnostics.Analyze(snap, Info(drive));

        var f = Assert.Single(report.Findings, x => x.Title.Contains("nearly full"));
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Contains(f.Actions, a => a.Kind == FindingActionKind.OpenCleanup);
    }

    [Fact]
    public void Ring_buffer_keeps_newest_items_in_order()
    {
        var ring = new RingBuffer<int>(3);
        for (int i = 1; i <= 5; i++) ring.Add(i);
        Assert.Equal([3, 4, 5], ring.ToArray());
        Assert.Equal([4, 5], ring.TakeLast(2).ToArray());
        Assert.Equal(5, ring.Last);
    }
}
