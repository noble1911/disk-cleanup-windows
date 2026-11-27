using System.Diagnostics;
using SpaceLens.Core.Monitoring;

namespace SpaceLens.Core.Tests;

public class MonitorTests
{
    [Fact]
    public async Task Monitor_reads_live_system_and_own_process_correctly()
    {
        using var monitor = new SystemMonitor(TimeSpan.FromMilliseconds(250));
        var got = new TaskCompletionSource<SystemSample>();
        int count = 0;
        monitor.Sampled += (_, s) => { if (++count == 3) got.TrySetResult(s); };
        monitor.Start();

        var sample = await got.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.InRange(sample.CpuPercent, 0, 100);
        Assert.True(sample.MemoryTotal > 1L << 30);
        Assert.True(sample.Processes.Count > 20);

        // Validates the SYSTEM_PROCESS_INFORMATION layout against what .NET reports for this process.
        using var me = Process.GetCurrentProcess();
        var mine = Assert.Single(sample.Processes, p => p.Pid == me.Id);
        Assert.Equal(me.ProcessName + ".exe", mine.Name, ignoreCase: true);
        Assert.Equal(me.Threads.Count, mine.Threads, (double)me.Threads.Count * 0.5);
        Assert.InRange(mine.PrivateBytes, me.PrivateMemorySize64 / 2, me.PrivateMemorySize64 * 2);
        Assert.InRange(mine.WorkingSet, me.WorkingSet64 / 2, me.WorkingSet64 * 2);
        Assert.NotNull(mine.Path);
        Assert.Contains(sample.Processes, p => p.Name == "System" && p.Pid == 4);
    }
}
