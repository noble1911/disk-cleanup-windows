using SpaceLens.Core.Monitoring;

namespace SpaceLens.Core.Diagnostics;

public enum Severity { Good, Info, Warning, Critical }

public enum FindingArea { Cpu, Memory, Disk, Network, Startup, System }

public enum FindingActionKind { EndProcess, OpenUri, RunCommand, OpenCleanup }

public sealed record FindingAction(string Label, FindingActionKind Kind, string Argument = "", bool RequiresAdmin = false);

public sealed record Finding(
    Severity Severity,
    FindingArea Area,
    string Title,
    string Detail,
    string Recommendation,
    IReadOnlyList<FindingAction> Actions)
{
    public Finding(Severity severity, FindingArea area, string title, string detail, string recommendation)
        : this(severity, area, title, detail, recommendation, []) { }
}

/// <summary>Resource use of all processes sharing one executable name (e.g. 30 × chrome.exe).</summary>
public sealed record AppUsage(string Name, string DisplayName, int ProcessCount, double CpuPercent, long PrivateBytes, long IoBytesPerSec, long NetBytesPerSec, int Connections, IReadOnlyList<int> Pids);

public sealed class DiagnosisReport
{
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required TimeSpan Observed { get; init; }
    public int Score { get; init; }
    public IReadOnlyList<AppUsage> TopCpu { get; init; } = [];
    public IReadOnlyList<AppUsage> TopMemory { get; init; } = [];
    public IReadOnlyList<AppUsage> TopDisk { get; init; } = [];
    public IReadOnlyList<AppUsage> TopNetwork { get; init; } = [];
}

/// <summary>
/// Turns monitor history into plain-language findings: what is slow, which app causes it,
/// and what to do about it.
/// </summary>
public static class PerformanceDiagnostics
{
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;

    /// <summary>Samples used for "sustained" averages (≈30 s at the default 1 s interval).</summary>
    public const int Window = 30;

    public static DiagnosisReport Analyze(MonitorSnapshot snapshot, SystemInfo info)
    {
        var findings = new List<Finding>();
        var window = snapshot.History.Skip(Math.Max(0, snapshot.History.Count - Window)).ToList();
        var latest = snapshot.Latest;

        var appsWindow = AggregateApps(window);
        var topCpu = appsWindow.Where(a => a.Name != "System Idle Process").OrderByDescending(a => a.CpuPercent).Take(8).ToList();
        List<AppUsage> topMemory = latest is null ? [] : AggregateApps([latest]).OrderByDescending(a => a.PrivateBytes).Take(8).ToList();
        var topDisk = appsWindow.OrderByDescending(a => a.IoBytesPerSec).Take(8).ToList();
        var topNet = snapshot.PerProcessNetwork
            ? appsWindow.OrderByDescending(a => a.NetBytesPerSec).Take(8).ToList()
            : appsWindow.OrderByDescending(a => a.Connections).Take(8).ToList();

        if (window.Count < 5 || latest is null)
        {
            findings.Add(new Finding(Severity.Info, FindingArea.System, "Collecting data…",
                "SpaceLens needs a few seconds of measurements before it can judge CPU, disk and network load.",
                "Leave the app open for 30 seconds while you use your PC normally (or while the slowdown happens), then run the check again."));
        }
        else
        {
            CpuRules(findings, window, appsWindow, snapshot.LogicalProcessors);
            MemoryRules(findings, latest, topMemory, snapshot, info);
            DiskRules(findings, window, topDisk);
            NetworkRules(findings, window, topNet, snapshot.PerProcessNetwork, info);
        }

        StorageRules(findings, info);
        SystemRules(findings, latest, info);

        int score = 100;
        foreach (var f in findings)
            score -= f.Severity switch { Severity.Critical => 25, Severity.Warning => 10, Severity.Info => 2, _ => 0 };

        return new DiagnosisReport
        {
            Findings = findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Area).ToList(),
            Observed = snapshot.Covered,
            Score = Math.Clamp(score, 0, 100),
            TopCpu = topCpu,
            TopMemory = topMemory,
            TopDisk = topDisk,
            TopNetwork = topNet,
        };
    }

    /// <summary>Averages per-application usage over the given samples.</summary>
    public static List<AppUsage> AggregateApps(IReadOnlyList<SystemSample> samples)
    {
        if (samples.Count == 0) return [];
        var acc = new Dictionary<string, (string Display, double Cpu, double Priv, double Io, double Net, double Conn, HashSet<int> Pids, int MaxCount)>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in samples)
        {
            var perSample = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in sample.Processes)
            {
                acc.TryGetValue(p.Name, out var a);
                a.Pids ??= [];
                a.Display ??= KnownProcesses.Get(p.Name)?.FriendlyName ?? p.DisplayName;
                a.Cpu += p.CpuPercent;
                a.Priv += p.PrivateBytes;
                a.Io += p.IoBytesPerSec;
                a.Net += Math.Max(0, p.NetBytesPerSec);
                a.Conn += p.TcpConnections;
                a.Pids.Add(p.Pid);
                perSample[p.Name] = perSample.TryGetValue(p.Name, out var seen) ? seen + 1 : 1;
                acc[p.Name] = a;
            }
            foreach (var (name, count) in perSample)
            {
                var a = acc[name];
                a.MaxCount = Math.Max(a.MaxCount, count);
                acc[name] = a;
            }
        }

        double n = samples.Count;
        return acc.Select(kv => new AppUsage(
                kv.Key, kv.Value.Display, kv.Value.MaxCount,
                kv.Value.Cpu / n, (long)(kv.Value.Priv / n), (long)(kv.Value.Io / n), (long)(kv.Value.Net / n),
                (int)Math.Round(kv.Value.Conn / n), kv.Value.Pids.ToList()))
            .ToList();
    }

    private static string List(IEnumerable<AppUsage> apps, Func<AppUsage, string> value) =>
        string.Join(", ", apps.Select(a => $"{Label(a)} ({value(a)})"));

    private static string Label(AppUsage a) => a.ProcessCount > 1 ? $"{a.DisplayName} ×{a.ProcessCount}" : a.DisplayName;

    private static IReadOnlyList<FindingAction> EndAction(AppUsage app)
    {
        if (app.ProcessCount != 1 || KnownProcesses.IsCritical(app.Name) || KnownProcesses.Get(app.Name)?.IsSystem == true) return [];
        return [new FindingAction($"End {app.Name}", FindingActionKind.EndProcess, app.Pids[^1].ToString())];
    }

    // ------------------------------------------------------------------ CPU

    private static void CpuRules(List<Finding> findings, List<SystemSample> window, List<AppUsage> apps, int cores)
    {
        double avg = window.Average(s => s.CpuPercent);
        double kernel = window.Average(s => s.KernelCpuPercent);
        var busy = apps.Where(a => a.Name != "System Idle Process" && a.CpuPercent >= 1).OrderByDescending(a => a.CpuPercent).ToList();
        string secs = $"{window.Count} s";

        if (avg >= 85)
        {
            findings.Add(new Finding(Severity.Critical, FindingArea.Cpu, "CPU is maxed out",
                $"Average CPU use was {avg:0}% over the last {secs}. Biggest users: {List(busy.Take(4), a => $"{a.CpuPercent:0}%")}.",
                "Everything will feel slow until this load drops. Close or pause the apps at the top of the list."));
        }
        else if (avg >= 60)
        {
            findings.Add(new Finding(Severity.Warning, FindingArea.Cpu, "CPU is busy",
                $"Average CPU use was {avg:0}% over the last {secs}. Biggest users: {List(busy.Take(4), a => $"{a.CpuPercent:0}%")}.",
                "If the PC feels sluggish, close the busiest apps you don't need right now."));
        }

        double oneCore = 100.0 / cores;
        foreach (var app in busy.Take(5))
        {
            bool heavy = app.CpuPercent >= 20;
            bool pegsCore = cores > 2 && app.ProcessCount == 1 && app.CpuPercent >= oneCore * 0.9;
            if (!heavy && !pegsCore) continue;

            var advice = KnownProcesses.Get(app.Name);
            string what = advice is null ? "" : $" ({advice.What})";
            string detail = pegsCore && !heavy
                ? $"{Label(app)}{what} is keeping one CPU core fully busy ({app.CpuPercent:0.#}% of total CPU over {secs}). Single-threaded work like this makes that app slow and wastes battery."
                : $"{Label(app)}{what} used {app.CpuPercent:0}% of total CPU on average over {secs}.";
            string recommendation = advice?.CpuAdvice
                ?? "If you don't need it right now, close it. If it stays busy while idle, update or reinstall the app — a stuck background task is the usual cause.";

            findings.Add(new Finding(app.CpuPercent >= 50 ? Severity.Critical : Severity.Warning, FindingArea.Cpu,
                $"{app.DisplayName} is using a lot of CPU", detail, recommendation, EndAction(app)));
        }

        if (kernel >= 30)
        {
            findings.Add(new Finding(Severity.Warning, FindingArea.Cpu, "High CPU time in Windows/drivers",
                $"{kernel:0}% of CPU time is spent in the kernel (drivers, interrupts, file system), not in apps.",
                "This often comes from a misbehaving driver or very heavy disk/network I/O. Update chipset, storage, network and graphics drivers; " +
                "if it persists, check Windows Update for driver updates."));
        }

        if (avg < 60 && !findings.Any(f => f.Area == FindingArea.Cpu))
            findings.Add(new Finding(Severity.Good, FindingArea.Cpu, "CPU load is normal", $"Average CPU use was {avg:0}% over the last {secs}.", "Nothing to do."));
    }

    // ------------------------------------------------------------------ Memory

    private static void MemoryRules(List<Finding> findings, SystemSample latest, List<AppUsage> topMemory, MonitorSnapshot snapshot, SystemInfo info)
    {
        double load = latest.MemoryLoadPercent;
        string used = $"{ByteFormat.Format(latest.MemoryUsed)} of {ByteFormat.Format(latest.MemoryTotal)} ({load:0}%)";
        string top = List(topMemory.Take(5), a => ByteFormat.Format(a.PrivateBytes));

        if (load >= 90)
        {
            findings.Add(new Finding(Severity.Critical, FindingArea.Memory, "Memory is almost full",
                $"{used} is in use. Windows is compressing memory and paging to disk, which makes everything slow. Biggest users: {top}.",
                "Close apps you aren't using — start with the ones at the top of the list. Browsers: close or sleep tabs."));
        }
        else if (load >= 80)
        {
            findings.Add(new Finding(Severity.Warning, FindingArea.Memory, "Memory is running low",
                $"{used} is in use. Biggest users: {top}.",
                "Close apps you aren't using to keep the PC responsive."));
        }
        else
        {
            findings.Add(new Finding(Severity.Good, FindingArea.Memory, "Memory use is fine", $"{used} is in use.", "Nothing to do."));
        }

        if (latest.CommitPercent >= 90)
        {
            findings.Add(new Finding(Severity.Critical, FindingArea.Memory, "Running out of virtual memory",
                $"Committed memory is {ByteFormat.Format(latest.CommitTotal)} of a {ByteFormat.Format(latest.CommitLimit)} limit ({latest.CommitPercent:0}%). " +
                "When this hits 100%, apps crash or refuse to open.",
                "Close memory-hungry apps. If this happens often, let Windows manage the page file size (System → About → Advanced system settings → Performance → Advanced → Virtual memory).",
                [new FindingAction("Open performance options", FindingActionKind.RunCommand, "SystemPropertiesPerformance.exe")]));
        }

        foreach (var app in topMemory.Take(5))
        {
            if (app.PrivateBytes < 1536 * MB || app.PrivateBytes < latest.MemoryTotal / 5) continue;
            var advice = KnownProcesses.Get(app.Name);
            findings.Add(new Finding(load >= 80 ? Severity.Warning : Severity.Info, FindingArea.Memory,
                $"{app.DisplayName} is using {ByteFormat.Format(app.PrivateBytes)} of memory",
                $"{Label(app)} holds {100.0 * app.PrivateBytes / latest.MemoryTotal:0}% of your RAM.",
                advice?.MemoryAdvice ?? "If you aren't actively using it, closing it will free that memory immediately.",
                EndAction(app)));
        }

        // Leaks: memory that keeps growing over the observed period.
        foreach (var trend in snapshot.Trends.Where(t => t.Alive && t.PrivateBytes.Length >= 120))
        {
            var values = trend.PrivateBytes;
            int q = values.Length / 4;
            double first = values.Take(q).Average();
            double last = values.Skip(values.Length - q).Average();
            double growth = last - first;
            if (growth < 300 * MB || growth < first * 0.3) continue;

            int rising = 0;
            for (int i = 1; i < values.Length; i++) if (values[i] >= values[i - 1]) rising++;
            if (rising < (values.Length - 1) * 0.7) continue;

            double minutes = values.Length * snapshot.Interval.TotalMinutes;
            var name = trend.Description ?? trend.Name;
            findings.Add(new Finding(Severity.Warning, FindingArea.Memory, $"{name} may be leaking memory",
                $"{trend.Name} (PID {trend.Key.Pid}) grew by {ByteFormat.Format((long)growth)} in about {minutes:0} minutes and keeps rising.",
                "Restart the app to get the memory back. If it keeps happening, check for an update — memory leaks are bugs.",
                KnownProcesses.IsCritical(trend.Name) ? [] : [new FindingAction($"End {trend.Name}", FindingActionKind.EndProcess, trend.Key.Pid.ToString())]));
        }

        foreach (var trend in snapshot.Trends.Where(t => t.Alive && t.Handles.Length >= 60 && t.Handles[^1] >= 20_000))
        {
            long growth = trend.Handles[^1] - trend.Handles[0];
            if (growth < 1_000) continue;
            findings.Add(new Finding(Severity.Warning, FindingArea.Memory, $"{trend.Description ?? trend.Name} may be leaking handles",
                $"{trend.Name} holds {trend.Handles[^1]:N0} handles and gained {growth:N0} while we watched.",
                "Handle leaks slowly degrade the whole system. Restart the app and look for an update."));
        }

        double faults = snapshot.History.Skip(Math.Max(0, snapshot.History.Count - Window)).Average(s => s.Processes.Sum(p => (double)p.HardFaultsPerSec));
        if (faults >= 1000 && load >= 80)
        {
            findings.Add(new Finding(Severity.Warning, FindingArea.Memory, "Windows is paging to disk",
                $"About {faults:N0} hard page faults per second — data is being read back from the page file because RAM is full.",
                "Free memory by closing apps. If you regularly see this, more RAM is the real fix."));
        }

        if (info.TotalMemory > 0 && info.TotalMemory < 7.5 * GB && load >= 70)
        {
            findings.Add(new Finding(Severity.Info, FindingArea.Memory, "This PC has little RAM for modern apps",
                $"{ByteFormat.Format(info.TotalMemory)} of RAM installed. Browsers, Teams and similar apps can easily use more than that.",
                "If your PC supports it, upgrading to 16 GB is the most effective speed-up for everyday multitasking."));
        }
    }

    // ------------------------------------------------------------------ Disk

    private static void DiskRules(List<Finding> findings, List<SystemSample> window, List<AppUsage> topDisk)
    {
        var withCounters = window.Where(s => s.DiskActivePercent >= 0).ToList();
        if (withCounters.Count == 0) return;

        double active = withCounters.Average(s => s.DiskActivePercent);
        double queue = withCounters.Average(s => s.DiskQueueLength);
        double read = withCounters.Average(s => s.DiskReadBytesPerSec);
        double write = withCounters.Average(s => s.DiskWriteBytesPerSec);
        var busy = topDisk.Where(a => a.IoBytesPerSec >= 256 * 1024).Take(4).ToList();
        string who = busy.Count > 0 ? $" Biggest I/O: {List(busy, a => ByteFormat.Rate(a.IoBytesPerSec))}." : "";

        if (active >= 90)
        {
            var advice = busy.Select(a => KnownProcesses.Get(a.Name)?.DiskAdvice).FirstOrDefault(a => a is not null);
            findings.Add(new Finding(Severity.Critical, FindingArea.Disk, "Disk is at 100%",
                $"The disk was busy {active:0}% of the time (queue {queue:0.0}; reading {ByteFormat.Rate(read)}, writing {ByteFormat.Rate(write)}).{who}",
                advice ?? "Apps that need the disk will stall. Pause the heavy I/O (updates, sync, antivirus scans, downloads) or wait for it to finish. " +
                "On a hard drive (HDD), moving Windows to an SSD is the biggest upgrade you can make."));
        }
        else if (active >= 60 || queue >= 2)
        {
            findings.Add(new Finding(Severity.Warning, FindingArea.Disk, "Disk is busy",
                $"The disk was busy {active:0}% of the time (queue {queue:0.0}).{who}",
                "Expect slower app launches and file operations until this settles."));
        }
        else
        {
            findings.Add(new Finding(Severity.Good, FindingArea.Disk, "Disk activity is normal",
                $"The disk was busy {active:0}% of the time.", "Nothing to do."));
        }
    }

    private static void StorageRules(List<Finding> findings, SystemInfo info)
    {
        foreach (var drive in info.Drives.Where(d => d.Type == DriveType.Fixed && d.Total > 0))
        {
            double freePct = 100.0 * drive.Free / drive.Total;
            bool critical = freePct < 5 || (drive.IsSystem && drive.Free < 5 * GB);
            bool warning = freePct < 12 || (drive.IsSystem && drive.Free < 20 * GB);
            if (!critical && !warning) continue;

            string why = drive.IsSystem
                ? " Windows needs free space on this drive for updates, the page file and temporary files; when it runs out, the whole PC slows down and updates fail."
                : "";
            findings.Add(new Finding(critical ? Severity.Critical : Severity.Warning, FindingArea.Disk,
                $"Drive {drive.Name.TrimEnd('\\')} is nearly full",
                $"Only {ByteFormat.Format(drive.Free)} free of {ByteFormat.Format(drive.Total)} ({freePct:0.#}%).{why}",
                "Scan the drive in Disk Map to see what's using the space, then use the Cleanup Advisor to free it.",
                [new FindingAction("Find what to clean", FindingActionKind.OpenCleanup, drive.Name)]));
        }
    }

    // ------------------------------------------------------------------ Network

    private static void NetworkRules(List<Finding> findings, List<SystemSample> window, List<AppUsage> topNet, bool perProcess, SystemInfo info)
    {
        double recv = window.Average(s => s.NetReceiveBytesPerSec);
        double send = window.Average(s => s.NetSendBytesPerSec);
        double total = recv + send;
        long link = window[^1].NetLinkSpeedBitsPerSec;
        double linkBytes = link > 0 ? link / 8.0 : 0;

        var busy = perProcess
            ? topNet.Where(a => a.NetBytesPerSec >= 64 * 1024).Take(4).ToList()
            : topNet.Where(a => a.Connections > 0).Take(4).ToList();
        string who = busy.Count == 0
            ? ""
            : perProcess
                ? $" Biggest users: {List(busy, a => ByteFormat.Rate(a.NetBytesPerSec))}."
                : $" Apps with the most open connections: {List(busy, a => $"{a.Connections} connections")}. (Run as administrator to see exact bandwidth per app.)";

        bool saturated = linkBytes > 0 && total >= linkBytes * 0.7;
        if (saturated || total >= 5 * MB)
        {
            var advice = busy.Select(a => KnownProcesses.Get(a.Name)?.NetworkAdvice).FirstOrDefault(a => a is not null);
            findings.Add(new Finding(saturated ? Severity.Warning : Severity.Info, FindingArea.Network,
                saturated ? "Network connection is saturated" : "Heavy network activity",
                $"Downloading {ByteFormat.Rate(recv)}, uploading {ByteFormat.Rate(send)} on average over {window.Count} s." +
                (linkBytes > 0 ? $" Link speed: {link / 1_000_000:N0} Mbit/s." : "") + who,
                advice ?? "Large downloads, cloud sync, game updates and Windows Update are the usual causes. Pause the one you don't need right now. " +
                "Windows Update's peer-to-peer sharing can be limited in Settings → Windows Update → Advanced options → Delivery Optimization.",
                [new FindingAction("Delivery Optimization settings", FindingActionKind.OpenUri, "ms-settings:delivery-optimization")]));
        }
        else
        {
            findings.Add(new Finding(Severity.Good, FindingArea.Network, "Network activity is normal",
                $"Downloading {ByteFormat.Rate(recv)}, uploading {ByteFormat.Rate(send)} on average." + (info.IsElevated ? "" : ""),
                "Nothing to do."));
        }
    }

    // ------------------------------------------------------------------ System

    private static void SystemRules(List<Finding> findings, SystemSample? latest, SystemInfo info)
    {
        var uptime = latest?.Uptime ?? TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (uptime.TotalDays >= 7)
        {
            findings.Add(new Finding(uptime.TotalDays >= 14 ? Severity.Warning : Severity.Info, FindingArea.System,
                $"PC hasn't restarted in {uptime.TotalDays:0} days",
                "Long uptimes let leaked memory, stuck background tasks and pending updates pile up. " +
                "Note: 'Shut down' with Fast Startup enabled doesn't reset this — only Restart does.",
                "Restart the PC (Start → Power → Restart) to clear it out."));
        }

        if (info.PowerPlan == PowerPlan.PowerSaver)
        {
            findings.Add(new Finding(info.OnBattery ? Severity.Info : Severity.Warning, FindingArea.System, "Power saver plan is active",
                "The Power saver plan caps CPU speed to save energy, which makes everything slower.",
                "Switch to the Balanced plan unless you need to stretch battery life.",
                [new FindingAction("Switch to Balanced", FindingActionKind.RunCommand, "powercfg /setactive SCHEME_BALANCED")]));
        }
        if (info.BatterySaver)
        {
            findings.Add(new Finding(Severity.Info, FindingArea.System, "Battery saver is on",
                "Battery saver throttles background activity and can lower performance.",
                "Turn it off in Quick Settings when you're plugged in or need full speed.",
                [new FindingAction("Power settings", FindingActionKind.OpenUri, "ms-settings:powersleep")]));
        }

        int enabledStartup = info.StartupItems.Count(s => s.Enabled);
        if (enabledStartup >= 6)
        {
            string names = string.Join(", ", info.StartupItems.Where(s => s.Enabled).Take(8).Select(s => s.Name));
            findings.Add(new Finding(enabledStartup >= 10 ? Severity.Warning : Severity.Info, FindingArea.Startup,
                $"{enabledStartup} apps start with Windows",
                $"Every startup app slows down sign-in and keeps using memory in the background: {names}{(enabledStartup > 8 ? ", …" : "")}.",
                "Disable the ones you don't need straight after signing in (updaters, launchers, chat apps). They still work when you open them yourself.",
                [new FindingAction("Manage startup apps", FindingActionKind.OpenUri, "ms-settings:startupapps")]));
        }

        if (latest is not null && latest.ProcessCount >= 350)
        {
            findings.Add(new Finding(Severity.Info, FindingArea.System, $"{latest.ProcessCount} processes are running",
                "That's a lot of background activity, which costs memory and CPU time.",
                "Check the System Monitor's 'group by app' view for apps with many processes, and uninstall utilities you don't use."));
        }
    }
}
