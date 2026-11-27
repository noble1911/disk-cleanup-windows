namespace SpaceLens.Core.Monitoring;

/// <summary>Identifies a process instance; PIDs are reused, so the creation time is part of the key.</summary>
public readonly record struct ProcessKey(int Pid, long CreateTime);

public sealed class ProcessSample
{
    public required ProcessKey Key { get; init; }
    public int Pid => Key.Pid;
    public int ParentPid { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public string? Description { get; init; }
    public uint SessionId { get; init; }

    /// <summary>Share of total CPU capacity (0–100 across all cores).</summary>
    public double CpuPercent { get; init; }
    public long PrivateBytes { get; init; }
    public long WorkingSet { get; init; }
    public long IoBytesPerSec { get; init; }
    public long IoReadBytesPerSec { get; init; }
    public long IoWriteBytesPerSec { get; init; }
    /// <summary>Network throughput, available only when running elevated (ETW); otherwise -1.</summary>
    public long NetBytesPerSec { get; init; } = -1;
    public int TcpConnections { get; init; }
    public int Threads { get; init; }
    public int Handles { get; init; }
    public uint HardFaultsPerSec { get; init; }
    public DateTime StartTime { get; init; }

    /// <summary>Friendly name for display: file description if known, else the image name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : Description!;
}

public sealed class SystemSample
{
    public DateTime Time { get; init; }

    public double CpuPercent { get; init; }
    public double KernelCpuPercent { get; init; }

    public long MemoryTotal { get; init; }
    public long MemoryAvailable { get; init; }
    public long MemoryUsed => MemoryTotal - MemoryAvailable;
    public double MemoryLoadPercent => MemoryTotal > 0 ? 100.0 * MemoryUsed / MemoryTotal : 0;
    public long CommitTotal { get; init; }
    public long CommitLimit { get; init; }
    public double CommitPercent => CommitLimit > 0 ? 100.0 * CommitTotal / CommitLimit : 0;
    public long SystemCache { get; init; }
    public long KernelNonPaged { get; init; }

    /// <summary>% of time the disks were busy (100 − idle), or -1 when counters are unavailable.</summary>
    public double DiskActivePercent { get; init; } = -1;
    public double DiskReadBytesPerSec { get; init; }
    public double DiskWriteBytesPerSec { get; init; }
    public double DiskQueueLength { get; init; }

    public double NetReceiveBytesPerSec { get; init; }
    public double NetSendBytesPerSec { get; init; }
    public double NetTotalBytesPerSec => NetReceiveBytesPerSec + NetSendBytesPerSec;
    public long NetLinkSpeedBitsPerSec { get; init; }

    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public TimeSpan Uptime { get; init; }

    public IReadOnlyList<ProcessSample> Processes { get; init; } = [];
}

/// <summary>Fixed-size circular buffer; oldest values drop off.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;

    public RingBuffer(int capacity) => _items = new T[capacity];

    public int Count { get; private set; }
    public int Capacity => _items.Length;

    public void Add(T item)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = item;
            Count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    public T this[int index] => _items[(_start + index) % _items.Length];

    public T Last => this[Count - 1];

    public T[] ToArray()
    {
        var result = new T[Count];
        for (int i = 0; i < Count; i++) result[i] = this[i];
        return result;
    }

    /// <summary>The newest <paramref name="n"/> items, oldest first.</summary>
    public IEnumerable<T> TakeLast(int n)
    {
        int skip = Math.Max(0, Count - n);
        for (int i = skip; i < Count; i++) yield return this[i];
    }
}

/// <summary>Recent values for one process, used to spot sustained hogs and memory leaks.</summary>
public sealed class ProcessHistory
{
    public const int Length = 300;

    public ProcessHistory(ProcessKey key, string name)
    {
        Key = key;
        Name = name;
    }

    public ProcessKey Key { get; }
    public string Name { get; }
    public RingBuffer<double> Cpu { get; } = new(Length);
    public RingBuffer<long> PrivateBytes { get; } = new(Length);
    public RingBuffer<long> IoBytesPerSec { get; } = new(Length);
    public RingBuffer<long> Handles { get; } = new(Length);
    public DateTime LastSeen { get; set; }
}
