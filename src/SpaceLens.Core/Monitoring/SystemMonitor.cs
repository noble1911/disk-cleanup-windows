using System.Diagnostics;
using System.Net.NetworkInformation;
using SpaceLens.Core.Native;

namespace SpaceLens.Core.Monitoring;

public sealed record ProcessTrend(
    ProcessKey Key,
    string Name,
    string? Description,
    string? Path,
    double[] Cpu,
    long[] PrivateBytes,
    long[] IoBytesPerSec,
    long[] Handles,
    bool Alive);

/// <summary>An immutable copy of everything the monitor has collected so far.</summary>
public sealed class MonitorSnapshot
{
    public required IReadOnlyList<SystemSample> History { get; init; }
    public required IReadOnlyList<ProcessTrend> Trends { get; init; }
    public int LogicalProcessors { get; init; } = Environment.ProcessorCount;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
    public bool PerProcessNetwork { get; init; }
    public SystemSample? Latest => History.Count > 0 ? History[^1] : null;
    public TimeSpan Covered => History.Count > 1 ? History[^1].Time - History[0].Time : TimeSpan.Zero;
}

/// <summary>
/// Samples CPU, memory, disk, network and every process once per interval on a background thread.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    public const int HistoryLength = 300;

    private readonly TimeSpan _interval;
    private readonly object _lock = new();
    private readonly RingBuffer<SystemSample> _history = new(HistoryLength);
    private readonly Dictionary<ProcessKey, ProcessHistory> _processHistory = new();
    private readonly Dictionary<ProcessKey, (string? Path, string? Description)> _details = new();
    private readonly ProcessEnumerator _enumerator = new();
    private readonly ProcessDetailsCache _detailsCache = new();
    private readonly NetworkTracer _networkTracer = new();
    private readonly int _cores = Environment.ProcessorCount;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private long _prevIdle, _prevKernel, _prevUser;
    private Dictionary<ProcessKey, RawProcess> _prevProcesses = new();
    private long _prevTimestamp;
    private long _prevNetRecv, _prevNetSend;
    private Dictionary<int, int> _tcpCounts = new();
    private int _tick;

    private PerformanceCounter? _diskIdle, _diskRead, _diskWrite, _diskQueue;
    private bool _diskCountersTried;
    private bool _diskReady;

    public SystemMonitor(TimeSpan? interval = null) => _interval = interval ?? TimeSpan.FromSeconds(1);

    /// <summary>Raised on a background thread after each sample.</summary>
    public event EventHandler<SystemSample>? Sampled;

    public SystemSample? Latest { get; private set; }
    public bool PerProcessNetwork => _networkTracer.IsRunning;
    public string? NetworkNote => _networkTracer.Error;
    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            Prime();
            _ = Task.Run(_networkTracer.TryStart);
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var sample = TakeSample();
                    Latest = sample;
                    Sampled?.Invoke(this, sample);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine("Monitor sample failed: " + ex);
                }
            }
        }, ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _loop = null;
    }

    public MonitorSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new MonitorSnapshot
            {
                History = _history.ToArray(),
                Trends = _processHistory.Values.Select(h =>
                {
                    _details.TryGetValue(h.Key, out var d);
                    return new ProcessTrend(h.Key, h.Name, d.Description, d.Path, h.Cpu.ToArray(), h.PrivateBytes.ToArray(),
                        h.IoBytesPerSec.ToArray(), h.Handles.ToArray(), _prevProcesses.ContainsKey(h.Key));
                }).ToList(),
                LogicalProcessors = _cores,
                Interval = _interval,
                PerProcessNetwork = PerProcessNetwork,
            };
        }
    }

    private void Prime()
    {
        Win32.GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);
        _prevTimestamp = Stopwatch.GetTimestamp();
        foreach (var p in _enumerator.Read()) _prevProcesses[p.Key] = p;
        (_prevNetRecv, _prevNetSend, _) = ReadNetworkTotals();
        _tcpCounts = SafeTcpCounts();
        // Creating performance counters can take several seconds on some PCs; don't hold up the first samples.
        _ = Task.Run(InitDiskCounters);
        _networkTracer.Drain();
    }

    private SystemSample TakeSample()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Max(0.001, (now - _prevTimestamp) / (double)Stopwatch.Frequency);
        _prevTimestamp = now;
        _tick++;

        // CPU (kernel time includes idle time).
        Win32.GetSystemTimes(out long idle, out long kernel, out long user);
        long dIdle = idle - _prevIdle, dKernel = kernel - _prevKernel, dUser = user - _prevUser;
        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);
        long dTotal = dKernel + dUser;
        double cpu = dTotal > 0 ? Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100) : 0;
        double kernelCpu = dTotal > 0 ? Math.Clamp(100.0 * (dKernel - dIdle) / dTotal, 0, 100) : 0;

        // Memory.
        var mem = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        Win32.GlobalMemoryStatusEx(ref mem);
        Win32.GetPerformanceInfo(out var perf, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.PERFORMANCE_INFORMATION>());
        long page = (long)perf.PageSize;

        // Disk.
        double diskActive = -1, diskRead = 0, diskWrite = 0, diskQueue = 0;
        if (Volatile.Read(ref _diskReady))
        {
            try
            {
                diskActive = Math.Clamp(100 - _diskIdle!.NextValue(), 0, 100);
                diskRead = _diskRead!.NextValue();
                diskWrite = _diskWrite!.NextValue();
                diskQueue = _diskQueue!.NextValue();
            }
            catch (Exception)
            {
                diskActive = -1;
            }
        }

        // Network (all interfaces).
        var (recv, send, linkSpeed) = ReadNetworkTotals();
        double netRecv = Math.Max(0, (recv - _prevNetRecv) / seconds);
        double netSend = Math.Max(0, (send - _prevNetSend) / seconds);
        (_prevNetRecv, _prevNetSend) = (recv, send);

        if (_tick % 3 == 0) _tcpCounts = SafeTcpCounts();
        var netByPid = _networkTracer.IsRunning ? _networkTracer.Drain() : null;

        // Processes.
        var raw = _enumerator.Read();
        var current = new Dictionary<ProcessKey, RawProcess>(raw.Count);
        var samples = new List<ProcessSample>(raw.Count);
        double cpuCapacity = seconds * 10_000_000.0 * _cores;

        foreach (var p in raw)
        {
            current[p.Key] = p;
            _prevProcesses.TryGetValue(p.Key, out var prev);
            bool hasPrev = prev.Name is not null;

            double procCpu = hasPrev ? Math.Clamp(100.0 * (p.CpuTime100ns - prev.CpuTime100ns) / cpuCapacity, 0, 100) : 0;
            long readRate = hasPrev ? (long)Math.Max(0, (p.ReadBytes - prev.ReadBytes) / seconds) : 0;
            long writeRate = hasPrev ? (long)Math.Max(0, (p.WriteBytes - prev.WriteBytes) / seconds) : 0;
            long otherRate = hasPrev ? (long)Math.Max(0, (p.OtherBytes - prev.OtherBytes) / seconds) : 0;
            uint faults = hasPrev && p.HardFaults >= prev.HardFaults ? (uint)((p.HardFaults - prev.HardFaults) / seconds) : 0;

            var (path, description) = _detailsCache.Get(p.Key, p.Name);
            long net = -1;
            if (netByPid is not null) net = netByPid.TryGetValue(p.Key.Pid, out var b) ? (long)(b / seconds) : 0;

            samples.Add(new ProcessSample
            {
                Key = p.Key,
                ParentPid = p.ParentPid,
                Name = p.Name,
                Path = path,
                Description = description,
                SessionId = p.SessionId,
                CpuPercent = p.Key.Pid == 0 ? 0 : procCpu,
                PrivateBytes = p.PrivateBytes,
                WorkingSet = p.WorkingSet,
                IoReadBytesPerSec = readRate,
                IoWriteBytesPerSec = writeRate,
                IoBytesPerSec = readRate + writeRate + otherRate,
                NetBytesPerSec = net,
                TcpConnections = _tcpCounts.TryGetValue(p.Key.Pid, out var c) ? c : 0,
                Threads = p.Threads,
                Handles = p.Handles,
                HardFaultsPerSec = faults,
                StartTime = p.Key.CreateTime > 0 ? DateTime.FromFileTimeUtc(p.Key.CreateTime) : DateTime.MinValue,
            });
        }

        var sample = new SystemSample
        {
            Time = DateTime.Now,
            CpuPercent = cpu,
            KernelCpuPercent = kernelCpu,
            MemoryTotal = (long)mem.ullTotalPhys,
            MemoryAvailable = (long)mem.ullAvailPhys,
            CommitTotal = (long)perf.CommitTotal * page,
            CommitLimit = (long)perf.CommitLimit * page,
            SystemCache = (long)perf.SystemCache * page,
            KernelNonPaged = (long)perf.KernelNonpaged * page,
            DiskActivePercent = diskActive,
            DiskReadBytesPerSec = diskRead,
            DiskWriteBytesPerSec = diskWrite,
            DiskQueueLength = diskQueue,
            NetReceiveBytesPerSec = netRecv,
            NetSendBytesPerSec = netSend,
            NetLinkSpeedBitsPerSec = linkSpeed,
            ProcessCount = (int)perf.ProcessCount,
            ThreadCount = (int)perf.ThreadCount,
            HandleCount = (int)perf.HandleCount,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            Processes = samples,
        };

        lock (_lock)
        {
            _history.Add(sample);
            var stamp = sample.Time;
            foreach (var s in samples)
            {
                if (!_processHistory.TryGetValue(s.Key, out var h))
                    _processHistory[s.Key] = h = new ProcessHistory(s.Key, s.Name);
                h.Cpu.Add(s.CpuPercent);
                h.PrivateBytes.Add(s.PrivateBytes);
                h.IoBytesPerSec.Add(s.IoBytesPerSec);
                h.Handles.Add(s.Handles);
                h.LastSeen = stamp;
                _details[s.Key] = (s.Path, s.Description);
            }

            // Forget processes that exited more than a minute ago.
            foreach (var key in _processHistory.Where(kv => (stamp - kv.Value.LastSeen).TotalSeconds > 60).Select(kv => kv.Key).ToList())
            {
                _processHistory.Remove(key);
                _details.Remove(key);
            }
            _prevProcesses = current;
        }

        if (_tick % 30 == 0) _detailsCache.Prune(current.Keys.ToHashSet());
        return sample;
    }

    private void InitDiskCounters()
    {
        if (_diskCountersTried) return;
        _diskCountersTried = true;
        try
        {
            _diskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
            _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
            _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
            _diskQueue = new PerformanceCounter("PhysicalDisk", "Avg. Disk Queue Length", "_Total", readOnly: true);
            _diskIdle.NextValue();
            _diskRead.NextValue();
            _diskWrite.NextValue();
            _diskQueue.NextValue();
            Volatile.Write(ref _diskReady, true);
        }
        catch (Exception)
        {
            _diskIdle = _diskRead = _diskWrite = _diskQueue = null;
        }
    }

    private static (long Recv, long Send, long LinkSpeed) ReadNetworkTotals()
    {
        long recv = 0, send = 0, speed = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                recv += stats.BytesReceived;
                send += stats.BytesSent;
                speed = Math.Max(speed, nic.Speed);
            }
        }
        catch (NetworkInformationException) { }
        return (recv, send, speed);
    }

    private static Dictionary<int, int> SafeTcpCounts()
    {
        try { return TcpConnections.CountByProcess(); }
        catch (Exception) { return new Dictionary<int, int>(); }
    }

    public void Dispose()
    {
        Stop();
        _networkTracer.Dispose();
        _diskIdle?.Dispose();
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
        _diskQueue?.Dispose();
    }
}
