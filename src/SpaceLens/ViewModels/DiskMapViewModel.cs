using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpaceLens.Controls;
using SpaceLens.Core;
using SpaceLens.Core.Advisor;
using SpaceLens.Core.Diagnostics;
using SpaceLens.Core.Scanning;
using SpaceLens.Services;

namespace SpaceLens.ViewModels;

public sealed partial class DriveItemViewModel : ObservableObject
{
    public DriveItemViewModel(DriveSummary drive)
    {
        Drive = drive;
        IsSelected = drive.IsSystem;
    }

    public DriveSummary Drive { get; }
    public string Letter => Drive.Name.TrimEnd('\\');
    public string Title => string.IsNullOrWhiteSpace(Drive.Label) ? $"Local Disk ({Letter})" : $"{Drive.Label} ({Letter})";
    public string Usage => $"{ByteFormat.Format(Drive.Free)} free of {ByteFormat.Format(Drive.Total)}";
    public double UsedPercent => Drive.UsedPercent;

    [ObservableProperty] private bool _isSelected;
}

public sealed partial class BreadcrumbItem(FsNode node) : ObservableObject
{
    public FsNode Node { get; } = node;
    public string Name => Node.Parent is null || Node.Parent.Kind == NodeKind.Computer ? Node.Name.TrimEnd('\\') : Node.Name;
}

public sealed partial class DiskMapViewModel : ObservableObject
{
    private readonly DispatcherTimer _progressTimer;
    private DiskScanner? _scanner;
    private CancellationTokenSource? _cts;
    private int _ticks;

    public DiskMapViewModel()
    {
        _progressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        RefreshDrives();
    }

    public ObservableCollection<DriveItemViewModel> Drives { get; } = [];
    public ObservableCollection<TreeItemViewModel> TreeRoots { get; } = [];
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    [ObservableProperty] private FsNode? _rootNode;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGoUp))] private FsNode? _currentNode;
    [ObservableProperty] private FsNode? _selectedNode;
    [ObservableProperty] private FsNode? _hoveredNode;
    [ObservableProperty] private int _mapVersion;
    [ObservableProperty] private TreemapColorMode _colorMode = TreemapColorMode.Folder;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ScanSelectedCommand), nameof(ScanAllCommand), nameof(ScanFolderCommand), nameof(CancelCommand))]
    private bool _isScanning;

    [ObservableProperty] private string _status = "Choose drives and press Scan. Tip: scanning as administrator also reads protected system folders.";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _currentPathText = "";

    public bool CanGoUp => CurrentNode?.Parent is not null;
    public bool IsElevated => AppServices.Instance.IsElevated;

    /// <summary>The node whose details show in the info bar: hovered first, then selected.</summary>
    public FsNode? InfoNode => HoveredNode ?? SelectedNode;
    public string InfoTitle => InfoNode is null ? "Hover over the map to see details" : InfoNode.FullPath;
    public string InfoDetail => InfoNode is null ? "Double-click a folder to zoom in · right-click for actions" : Describe(InfoNode);

    partial void OnHoveredNodeChanged(FsNode? value) => NotifyInfo();

    partial void OnSelectedNodeChanged(FsNode? value)
    {
        NotifyInfo();
        OpenSelectedCommand.NotifyCanExecuteChanged();
        RecycleSelectedCommand.NotifyCanExecuteChanged();
        ZoomSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentNodeChanged(FsNode? value)
    {
        Breadcrumbs.Clear();
        var chain = new List<FsNode>();
        for (var n = value; n is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        foreach (var n in chain) Breadcrumbs.Add(new BreadcrumbItem(n));
        GoUpCommand.NotifyCanExecuteChanged();
    }

    private void NotifyInfo()
    {
        OnPropertyChanged(nameof(InfoNode));
        OnPropertyChanged(nameof(InfoTitle));
        OnPropertyChanged(nameof(InfoDetail));
    }

    private string Describe(FsNode node)
    {
        var parts = new List<string> { ByteFormat.Format(node.Size) };
        var reference = CurrentNode ?? RootNode;
        if (reference is not null && reference.Size > 0 && !ReferenceEquals(reference, node))
            parts.Add($"{100.0 * node.Size / reference.Size:0.#}% of {(reference.Parent is null ? "scan" : reference.Name)}");
        if (node.IsContainer)
        {
            parts.Add($"{node.FileCount:N0} files");
            parts.Add($"{node.DirCount:N0} folders");
        }
        else if (node.Kind == NodeKind.FileGroup)
        {
            parts.Add($"{node.GroupedCount:N0} files smaller than 256 KB, grouped together");
        }
        if (node.LastWriteUtc > DateTime.MinValue && node.Kind != NodeKind.Computer)
            parts.Add("modified " + node.LastWriteUtc.ToLocalTime().ToString("d"));
        if (node.AccessDenied) parts.Add("access denied — run as administrator to include it");
        return string.Join("  ·  ", parts);
    }

    [RelayCommand]
    private void RefreshDrives()
    {
        var selected = Drives.Where(d => d.IsSelected).Select(d => d.Drive.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Drives.Clear();
        foreach (var d in SystemInfo.ReadDrives().Where(d => d.Type == DriveType.Fixed || d.Type == DriveType.Removable))
        {
            var vm = new DriveItemViewModel(d);
            if (selected.Count > 0) vm.IsSelected = selected.Contains(d.Name);
            Drives.Add(vm);
        }
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanSelected()
    {
        var roots = Drives.Where(d => d.IsSelected).Select(d => d.Drive.Name).ToList();
        if (roots.Count == 0)
        {
            Status = "Tick at least one drive first.";
            return Task.CompletedTask;
        }
        return Scan(roots);
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAll()
    {
        foreach (var d in Drives) d.IsSelected = true;
        return Scan(Drives.Select(d => d.Drive.Name).ToList());
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan" };
        return dialog.ShowDialog() == true ? Scan([dialog.FolderName]) : Task.CompletedTask;
    }

    public Task ScanPath(string path) => IsScanning ? Task.CompletedTask : Scan([path]);

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void Cancel() => _cts?.Cancel();

    private async Task Scan(IReadOnlyList<string> roots)
    {
        _cts = new CancellationTokenSource();
        _scanner = new DiskScanner();
        var root = _scanner.Prepare(roots);

        RootNode = root;
        CurrentNode = root;
        SelectedNode = null;
        TreeRoots.Clear();
        IsScanning = true;
        Status = "Scanning " + string.Join(", ", roots) + "…";
        _ticks = 0;
        _progressTimer.Start();

        try
        {
            var result = await _scanner.ScanAsync(roots, _cts.Token);
            AppServices.Instance.LastScan = result;

            string summary = $"{ByteFormat.Format(result.Root.Size)} in {result.Root.FileCount:N0} files and {result.Root.DirCount:N0} folders, scanned in {result.Elapsed.TotalSeconds:0.0} s.";
            if (result.Errors > 0)
                summary += $" {result.Errors:N0} folders couldn't be read" + (IsElevated ? "." : " (run as administrator to include them).");
            Status = (result.Cancelled ? "Scan stopped. Partial result: " : "Done. ") + summary;
        }
        catch (Exception ex)
        {
            Status = "Scan failed: " + ex.Message;
        }
        finally
        {
            _progressTimer.Stop();
            IsScanning = false;
            ProgressText = "";
            CurrentPathText = "";
            MapVersion++;
            BuildTree();
        }
    }

    private void UpdateProgress()
    {
        if (_scanner is null) return;
        var p = _scanner.Progress;
        ProgressText = $"{ByteFormat.Format(p.Bytes)} · {p.Files:N0} files · {p.Directories:N0} folders · {p.Elapsed.Elapsed.TotalSeconds:0} s";
        CurrentPathText = p.CurrentPath;
        if (++_ticks % 3 == 0) MapVersion++; // redraw the growing map every ~0.75 s
    }

    private void BuildTree()
    {
        TreeRoots.Clear();
        if (CurrentNode is null) return;
        var item = new TreeItemViewModel(CurrentNode, null) { IsExpanded = true };
        TreeRoots.Add(item);
    }

    // ------------------------------------------------------------------ navigation

    [RelayCommand]
    private void ZoomTo(FsNode? node)
    {
        if (node is null || !node.IsContainer) return;
        CurrentNode = node;
        SelectedNode = null;
        if (!IsScanning) BuildTree();
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp()
    {
        if (CurrentNode?.Parent is { } parent) ZoomTo(parent);
    }

    [RelayCommand]
    private void ToggleColorMode() =>
        ColorMode = ColorMode == TreemapColorMode.Folder ? TreemapColorMode.FileType : TreemapColorMode.Folder;

    // ------------------------------------------------------------------ actions on the selection

    private bool HasRealSelection() => SelectedNode is { Kind: NodeKind.File or NodeKind.Directory } or { Kind: NodeKind.FileGroup };

    [RelayCommand(CanExecute = nameof(HasRealSelection))]
    private void OpenSelected()
    {
        if (SelectedNode is { } n) AppServices.OpenInExplorer(n.FullPath);
    }

    [RelayCommand(CanExecute = nameof(HasRealSelection))]
    private void ZoomSelected()
    {
        var n = SelectedNode;
        if (n is null) return;
        ZoomTo(n.IsContainer ? n : n.Parent);
    }

    [RelayCommand]
    private void CopyPath(FsNode? node)
    {
        node ??= SelectedNode;
        if (node is null) return;
        try { Clipboard.SetText(node.FullPath); } catch (Exception) { }
    }

    private bool CanRecycleSelected() => !IsScanning && SelectedNode is { Kind: NodeKind.File or NodeKind.Directory, Parent: not null } n && n.Parent.Kind != NodeKind.Computer;

    [RelayCommand(CanExecute = nameof(CanRecycleSelected))]
    private async Task RecycleSelected()
    {
        var node = SelectedNode;
        if (node?.Parent is null) return;

        string path = node.FullPath;
        string warning = CleanupAdvisor.IsSystemPath(path)
            ? "\n\n⚠ This is inside a Windows or program folder. Deleting it may break Windows or an installed app."
            : "";
        if (!AppServices.Confirm($"Move this {(node.IsContainer ? "folder" : "file")} to the Recycle Bin?\n\n{path}\n{ByteFormat.Format(node.Size)}{warning}",
                icon: warning.Length > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question))
            return;

        var outcome = await Task.Run(() => CleanupExecutor.RecycleItems([path]));
        if (outcome.ItemsRemoved > 0)
        {
            var parent = node.Parent;
            parent.RemoveChild(node);
            SelectedNode = null;
            MapVersion++;
            BuildTree();
        }
        Status = outcome.Message;
    }

    /// <summary>Called after the Cleanup Advisor removed things so the map reflects it.</summary>
    public void Refresh()
    {
        MapVersion++;
        if (!IsScanning) BuildTree();
    }
}
