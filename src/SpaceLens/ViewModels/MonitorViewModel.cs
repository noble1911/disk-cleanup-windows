using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceLens.Core;
using SpaceLens.Core.Diagnostics;
using SpaceLens.Core.Monitoring;
using SpaceLens.Services;

namespace SpaceLens.ViewModels;

/// <summary>A process (or a group of same-named processes) in the live table. Updated in place each second.</summary>
public sealed partial class ProcessRowViewModel : ObservableObject
{
    public ProcessRowViewModel(string key, string name)
    {
        Key = key;
        Name = name;
    }

    public string Key { get; }
    public string Name { get; }

    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _pidText = "";
    [ObservableProperty] private int _count = 1;
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private long _memory;
    [ObservableProperty] private long _io;
    [ObservableProperty] private long _net = -1;
    [ObservableProperty] private int _connections;
    [ObservableProperty] private int _threads;
    [ObservableProperty] private int _handles;
    [ObservableProperty] private string? _path;

    public List<int> Pids { get; } = [];

    public string CpuText => Cpu < 0.05 ? "0" : Cpu.ToString("0.0");
    public string MemoryText => ByteFormat.Format(Memory);
    public string IoText => Io < 1024 ? "0" : ByteFormat.Rate(Io);
    public string NetText => Net < 0 ? "—" : Net < 1024 ? "0" : ByteFormat.Rate(Net);
    public bool IsCritical => KnownProcesses.IsCritical(Name);

    partial void OnCpuChanged(double value) => OnPropertyChanged(nameof(CpuText));
    partial void OnMemoryChanged(long value) => OnPropertyChanged(nameof(MemoryText));
    partial void OnIoChanged(long value) => OnPropertyChanged(nameof(IoText));
    partial void OnNetChanged(long value) => OnPropertyChanged(nameof(NetText));
}

public sealed partial class MonitorViewModel : ObservableObject
{
    private const int ChartPoints = 60;

    private readonly Dictionary<string, ProcessRowViewModel> _rows = new();
    private readonly Queue<double> _cpuHistory = new(), _memHistory = new(), _diskHistory = new(), _netHistory = new();
    private bool _updating;

    public MonitorViewModel()
    {
        Processes = [];
        ProcessView = (ListCollectionView)CollectionViewSource.GetDefaultView(Processes);
        ProcessView.Filter = o => o is ProcessRowViewModel r && Matches(r);
        ProcessView.SortDescriptions.Add(new SortDescription(nameof(ProcessRowViewModel.Cpu), ListSortDirection.Descending));
        ProcessView.IsLiveSorting = true;
        foreach (var p in new[] { "Cpu", "Memory", "Io", "Net", "Connections", "Threads", "Handles", "Count", "Name", "Description" })
            ProcessView.LiveSortingProperties.Add(p);

        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        AppServices.Instance.Monitor.Sampled += (_, sample) =>
        {
            if (_updating || dispatcher.HasShutdownStarted) return; // UI is behind; skip this sample
            _updating = true;
            dispatcher.BeginInvoke(() =>
            {
                try { Update(sample); }
                finally { _updating = false; }
            });
        };
    }

    public ObservableCollection<ProcessRowViewModel> Processes { get; }
    public ListCollectionView ProcessView { get; }

    public bool IsElevated => AppServices.Instance.IsElevated;
    public string NetworkNote => AppServices.Instance.Monitor.PerProcessNetwork
        ? "Per-app network usage measured with Windows event tracing."
        : AppServices.Instance.Monitor.NetworkNote ?? "Per-app network usage needs administrator rights.";

    [ObservableProperty] private bool _groupByApp = true;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private ProcessRowViewModel? _selectedRow;

    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _cpuDetail = "";
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _memoryText = "";
    [ObservableProperty] private string _memoryDetail = "";
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _diskText = "";
    [ObservableProperty] private string _diskDetail = "";
    [ObservableProperty] private string _networkText = "";
    [ObservableProperty] private string _networkDetail = "";
    [ObservableProperty] private string _summary = "Collecting…";

    [ObservableProperty] private double[] _cpuSeries = [];
    [ObservableProperty] private double[] _memorySeries = [];
    [ObservableProperty] private double[] _diskSeries = [];
    [ObservableProperty] private double[] _networkSeries = [];

    partial void OnFilterChanged(string value) => ProcessView.Refresh();

    partial void OnGroupByAppChanged(bool value)
    {
        Processes.Clear();
        _rows.Clear();
        if (AppServices.Instance.Monitor.Latest is { } latest) Update(latest);
    }

    partial void OnSelectedRowChanged(ProcessRowViewModel? value)
    {
        EndTaskCommand.NotifyCanExecuteChanged();
        OpenLocationCommand.NotifyCanExecuteChanged();
    }

    private bool Matches(ProcessRowViewModel r) =>
        string.IsNullOrWhiteSpace(Filter) ||
        r.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) ||
        r.Description.Contains(Filter, StringComparison.OrdinalIgnoreCase) ||
        r.PidText.Contains(Filter, StringComparison.OrdinalIgnoreCase);

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > ChartPoints) q.Dequeue();
    }

    private void Update(SystemSample s)
    {
        // Tiles
        CpuPercent = s.CpuPercent;
        CpuDetail = $"{s.ProcessCount:N0} processes · {s.ThreadCount:N0} threads · up {FormatUptime(s.Uptime)}";

        MemoryPercent = s.MemoryLoadPercent;
        MemoryText = $"{ByteFormat.Format(s.MemoryUsed)} / {ByteFormat.Format(s.MemoryTotal)}";
        MemoryDetail = $"Committed {ByteFormat.Format(s.CommitTotal)} of {ByteFormat.Format(s.CommitLimit)} · cached {ByteFormat.Format(s.SystemCache)}";

        if (s.DiskActivePercent >= 0)
        {
            DiskPercent = s.DiskActivePercent;
            DiskText = $"{s.DiskActivePercent:0}% active";
            DiskDetail = $"Read {ByteFormat.Rate(s.DiskReadBytesPerSec)} · Write {ByteFormat.Rate(s.DiskWriteBytesPerSec)}";
        }
        else
        {
            DiskText = "n/a";
            DiskDetail = "Disk performance counters are unavailable";
        }

        NetworkText = $"↓ {ByteFormat.Rate(s.NetReceiveBytesPerSec)}   ↑ {ByteFormat.Rate(s.NetSendBytesPerSec)}";
        NetworkDetail = s.NetLinkSpeedBitsPerSec > 0 ? $"Link {s.NetLinkSpeedBitsPerSec / 1_000_000:N0} Mbit/s" : "No active network adapter";

        Push(_cpuHistory, s.CpuPercent);
        Push(_memHistory, s.MemoryLoadPercent);
        Push(_diskHistory, Math.Max(0, s.DiskActivePercent));
        Push(_netHistory, s.NetTotalBytesPerSec);
        CpuSeries = _cpuHistory.ToArray();
        MemorySeries = _memHistory.ToArray();
        DiskSeries = _diskHistory.ToArray();
        NetworkSeries = _netHistory.ToArray();

        // Table
        var seen = new HashSet<string>();
        if (GroupByApp)
        {
            foreach (var g in s.Processes.Where(p => p.Pid != 0).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var first = g.First();
                var row = GetRow(g.Key, first.Name);
                row.Description = KnownProcesses.Get(first.Name)?.FriendlyName ?? first.Description ?? "";
                row.Count = g.Count();
                row.PidText = row.Count == 1 ? first.Pid.ToString() : $"{row.Count} processes";
                row.Cpu = g.Sum(p => p.CpuPercent);
                row.Memory = g.Sum(p => p.PrivateBytes);
                row.Io = g.Sum(p => p.IoBytesPerSec);
                row.Net = g.Any(p => p.NetBytesPerSec >= 0) ? g.Sum(p => Math.Max(0, p.NetBytesPerSec)) : -1;
                row.Connections = g.Sum(p => p.TcpConnections);
                row.Threads = g.Sum(p => p.Threads);
                row.Handles = g.Sum(p => p.Handles);
                row.Path = g.Select(p => p.Path).FirstOrDefault(p => p is not null);
                row.Pids.Clear();
                row.Pids.AddRange(g.Select(p => p.Pid));
                seen.Add(row.Key);
            }
        }
        else
        {
            foreach (var p in s.Processes.Where(p => p.Pid != 0))
            {
                var row = GetRow($"{p.Pid}:{p.Key.CreateTime}", p.Name);
                row.Description = p.Description ?? "";
                row.PidText = p.Pid.ToString();
                row.Cpu = p.CpuPercent;
                row.Memory = p.PrivateBytes;
                row.Io = p.IoBytesPerSec;
                row.Net = p.NetBytesPerSec;
                row.Connections = p.TcpConnections;
                row.Threads = p.Threads;
                row.Handles = p.Handles;
                row.Path = p.Path;
                row.Pids.Clear();
                row.Pids.Add(p.Pid);
                seen.Add(row.Key);
            }
        }

        foreach (var key in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Processes.Remove(_rows[key]);
            _rows.Remove(key);
        }

        var topCpu = Processes.Where(r => r.Name != "System Idle Process").OrderByDescending(r => r.Cpu).FirstOrDefault();
        var topMem = Processes.OrderByDescending(r => r.Memory).FirstOrDefault();
        Summary = topCpu is null || topMem is null
            ? ""
            : $"Top CPU: {Display(topCpu)} ({topCpu.Cpu:0.#}%)   ·   Top memory: {Display(topMem)} ({topMem.MemoryText})";
    }

    private static string Display(ProcessRowViewModel r) => string.IsNullOrWhiteSpace(r.Description) ? r.Name : r.Description;

    private ProcessRowViewModel GetRow(string key, string name)
    {
        if (_rows.TryGetValue(key, out var row)) return row;
        row = new ProcessRowViewModel(key, name);
        _rows[key] = row;
        Processes.Add(row);
        return row;
    }

    private static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m";

    private bool CanEnd() => SelectedRow is { IsCritical: false };

    [RelayCommand(CanExecute = nameof(CanEnd))]
    private void EndTask()
    {
        var row = SelectedRow;
        if (row is null) return;
        string what = row.Pids.Count > 1 ? $"all {row.Pids.Count} {row.Name} processes" : $"{row.Name} (PID {row.Pids.FirstOrDefault()})";
        if (!AppServices.Confirm($"End {what}?\n\nUnsaved work in that app will be lost.", "End task", MessageBoxImage.Warning)) return;

        int failed = 0;
        foreach (var pid in row.Pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: false);
            }
            catch (Exception)
            {
                failed++;
            }
        }
        if (failed > 0)
            MessageBox.Show($"{failed} process(es) could not be ended (access denied or already gone)." +
                            (IsElevated ? "" : " Running SpaceLens as administrator may help."), "End task", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool CanOpenLocation() => SelectedRow?.Path is not null;

    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private void OpenLocation()
    {
        if (SelectedRow?.Path is { } path) AppServices.OpenInExplorer(path);
    }

    [RelayCommand]
    private void SearchOnline()
    {
        if (SelectedRow is { } row) AppServices.ShellOpen("https://www.bing.com/search?q=" + Uri.EscapeDataString(row.Name + " process"));
    }

    [RelayCommand]
    private static void RestartAsAdmin() => AppServices.RestartElevated();

    [RelayCommand]
    private static void OpenHealthCheck() => AppServices.Instance.Navigate?.Invoke("health");
}
