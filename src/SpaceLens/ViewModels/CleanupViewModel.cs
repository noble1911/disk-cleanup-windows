using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceLens.Core;
using SpaceLens.Core.Advisor;
using SpaceLens.Core.Scanning;
using SpaceLens.Services;

namespace SpaceLens.ViewModels;

public sealed partial class SuggestionItemViewModel : ObservableObject
{
    private readonly SuggestionViewModel _owner;

    public SuggestionItemViewModel(SuggestionItem item, SuggestionViewModel owner)
    {
        Item = item;
        _owner = owner;
        _isSelected = item.Recommended;
    }

    public SuggestionItem Item { get; }
    public string Path => Item.Path;
    public string SizeText => ByteFormat.Format(Item.Bytes);
    public string DateText => Item.LastWriteUtc > DateTime.MinValue.AddYears(1) ? Item.LastWriteUtc.ToLocalTime().ToString("d") : "";
    public string? Note => Item.Note;

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _owner.SelectionChanged();

    [RelayCommand]
    private void Open() => AppServices.OpenInExplorer(Item.Path);
}

public sealed partial class SuggestionViewModel : ObservableObject
{
    private readonly CleanupViewModel _page;

    public SuggestionViewModel(CleanupSuggestion suggestion, CleanupViewModel page)
    {
        Suggestion = suggestion;
        _page = page;
        Items = new ObservableCollection<SuggestionItemViewModel>(suggestion.Items.Select(i => new SuggestionItemViewModel(i, this)));
    }

    public CleanupSuggestion Suggestion { get; }
    public ObservableCollection<SuggestionItemViewModel> Items { get; }

    public string Title => Suggestion.Title;
    public string Category => Suggestion.Category;
    public string Description => Suggestion.Description;
    public RiskLevel Risk => Suggestion.Risk;
    public string RiskText => Suggestion.Risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Review => "Review first",
        _ => "Advanced",
    };
    public string SizeText => Suggestion.Bytes > 0 ? (Suggestion.IsEstimate ? "up to " : "") + ByteFormat.Format(Suggestion.Bytes) : "varies";
    public string ActionLabel => Suggestion.ActionLabel;
    public bool HasItems => Items.Count > 0;
    public bool Selectable => Suggestion.Kind is SuggestionKind.RecycleItems or SuggestionKind.CleanFolderContents && Items.Count > 1;
    public string? CommandText => Suggestion.Kind == SuggestionKind.RunCommand ? Suggestion.Command : null;
    public bool NeedsAdminWarning => Suggestion.RequiresAdmin && !AppServices.Instance.IsElevated && Suggestion.Kind == SuggestionKind.CleanFolderContents;

    public long SelectedBytes => Selectable ? Items.Where(i => i.IsSelected).Sum(i => i.Item.Bytes) : Suggestion.Bytes;
    public string SelectionText => Selectable ? $"{Items.Count(i => i.IsSelected)} of {Items.Count} selected · {ByteFormat.Format(SelectedBytes)}" : "";

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ExecuteCommand))] private bool _isBusy;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ExecuteCommand))] private bool _isDone;

    internal void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectionText));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll(bool? select)
    {
        foreach (var i in Items) i.IsSelected = select ?? true;
    }

    private bool CanExecute() => !IsBusy && !IsDone && (!Selectable || Items.Any(i => i.IsSelected));

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task Execute()
    {
        var s = Suggestion;
        var selected = Selectable ? Items.Where(i => i.IsSelected).Select(i => i.Path).ToList() : null;

        if (s.Kind != SuggestionKind.OpenLocation)
        {
            string what = s.Kind switch
            {
                SuggestionKind.CleanFolderContents => $"Permanently delete the contents of {(selected?.Count ?? s.Items.Count)} folder(s) ({ByteFormat.Format(SelectedBytes)})?\n\nFiles that are in use are skipped.",
                SuggestionKind.RecycleItems => $"Move {(selected?.Count ?? s.Items.Count)} item(s) ({ByteFormat.Format(SelectedBytes)}) to the Recycle Bin?\n\nYou can restore them from the Recycle Bin.",
                SuggestionKind.EmptyRecycleBin => $"Permanently delete everything in the Recycle Bin ({ByteFormat.Format(s.Bytes)})?",
                SuggestionKind.RunCommand => $"Run this command{(s.RequiresAdmin ? " as administrator" : "")}?\n\n{s.Command}\n\n{s.Description}",
                _ => "",
            };
            var icon = s.Risk == RiskLevel.Safe ? MessageBoxImage.Question : MessageBoxImage.Warning;
            if (!AppServices.Confirm(what, s.Title, icon)) return;
        }

        IsBusy = true;
        ResultText = s.Kind == SuggestionKind.OpenLocation ? "" : "Working…";
        try
        {
            var outcome = await CleanupExecutor.ExecuteAsync(s, selected);
            ResultText = outcome.Message;
            if (outcome.BytesFreed > 0 || (s.Kind == SuggestionKind.RecycleItems && outcome.ItemsRemoved > 0))
            {
                IsDone = true;
                _page.OnCleaned(s, selected ?? s.Items.Select(i => i.Path).ToList(), outcome);
            }
        }
        catch (Exception ex)
        {
            ResultText = "Failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task<CleanupOutcome?> ExecuteSilently()
    {
        if (IsDone || IsBusy) return null;
        IsBusy = true;
        try
        {
            var paths = Items.Select(i => i.Path).ToList();
            var outcome = await CleanupExecutor.ExecuteAsync(Suggestion, paths);
            ResultText = outcome.Message;
            IsDone = true;
            _page.OnCleaned(Suggestion, paths, outcome);
            return outcome;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed record FileRow(string Name, string Folder, long Size, string SizeText, string Modified, string Category, string FullPath);

public sealed record ExtensionRow(string Extension, string Category, long Bytes, string SizeText, long Count, double Percent);

public sealed partial class CleanupViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public CleanupViewModel()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        AppServices.Instance.ScanCompleted += (_, _) =>
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.InvokeAsync(OnScanChanged);
        };
    }

    public ObservableCollection<SuggestionViewModel> Suggestions { get; } = [];
    public ObservableCollection<FileRow> LargestFiles { get; } = [];
    public ObservableCollection<ExtensionRow> FileTypes { get; } = [];

    /// <summary>Lets the Disk Map redraw after something was cleaned.</summary>
    public Action? AfterCleanup { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(CleanAllSafeCommand))] private bool _isAnalyzing;
    [ObservableProperty] private string _status = "Press Analyze to look for space you can free up.";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _safeText = "";
    [ObservableProperty] private string _freedText = "";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _scanNote = "";

    private long _freedTotal;

    public bool HasScan => AppServices.Instance.LastScan is not null;

    private void OnScanChanged()
    {
        OnPropertyChanged(nameof(HasScan));
        UpdateScanNote();
        FillScanTables();
    }

    private void UpdateScanNote()
    {
        var scan = AppServices.Instance.LastScan;
        ScanNote = scan is null
            ? "Tip: scan a drive in Disk Map first. Then the advisor can also find node_modules, build output, duplicates and large old files."
            : $"Using the scan of {string.Join(", ", scan.Roots)} from {scan.CompletedAt:t}.";
    }

    private bool CanAnalyze() => !IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task Analyze()
    {
        _cts = new CancellationTokenSource();
        IsAnalyzing = true;
        UpdateScanNote();
        Suggestions.Clear();
        HasResults = false;
        var progress = new Progress<string>(s => Status = s);

        try
        {
            var advisor = new CleanupAdvisor();
            var results = await advisor.AnalyzeAsync(AppServices.Instance.LastScan, progress, _cts.Token);
            foreach (var r in results) Suggestions.Add(new SuggestionViewModel(r, this));
            HasResults = Suggestions.Count > 0;
            UpdateTotals();
            Status = Suggestions.Count == 0 ? "Nothing worth cleaning was found. Nice and tidy!" : $"Found {Suggestions.Count} ways to free up space.";
            FillScanTables();
        }
        catch (OperationCanceledException)
        {
            Status = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Analysis failed: " + ex.Message;
        }
        finally
        {
            IsAnalyzing = false;
            CleanAllSafeCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateTotals()
    {
        var open = Suggestions.Where(s => !s.IsDone).ToList();
        long total = open.Sum(s => s.Suggestion.Bytes);
        long safe = open.Where(IsQuickWin).Sum(s => s.Suggestion.Bytes);
        TotalText = ByteFormat.Format(total);
        SafeText = ByteFormat.Format(safe);
        FreedText = _freedTotal > 0 ? ByteFormat.Format(_freedTotal) : "";
    }

    private static bool IsQuickWin(SuggestionViewModel s) =>
        s.Suggestion.Risk == RiskLevel.Safe && s.Suggestion.Kind is SuggestionKind.CleanFolderContents or SuggestionKind.EmptyRecycleBin;

    private bool CanCleanAllSafe() => !IsAnalyzing && Suggestions.Any(s => IsQuickWin(s) && !s.IsDone);

    [RelayCommand(CanExecute = nameof(CanCleanAllSafe))]
    private async Task CleanAllSafe()
    {
        var targets = Suggestions.Where(s => IsQuickWin(s) && !s.IsDone).ToList();
        string list = string.Join("\n", targets.Select(t => $"• {t.Title} — {t.SizeText}"));
        if (!AppServices.Confirm($"Clean these safe items?\n\n{list}\n\nTemporary files and caches are deleted permanently; files in use are skipped.", "Clean all safe items"))
            return;

        long freed = 0;
        foreach (var t in targets)
        {
            var outcome = await t.ExecuteSilently();
            freed += outcome?.BytesFreed ?? 0;
        }
        Status = $"Freed {ByteFormat.Format(freed)} from {targets.Count} safe items.";
        CleanAllSafeCommand.NotifyCanExecuteChanged();
    }

    internal void OnCleaned(CleanupSuggestion suggestion, IReadOnlyList<string> paths, CleanupOutcome outcome)
    {
        _freedTotal += outcome.BytesFreed;
        UpdateTotals();

        // Keep the scan tree in sync so the map and other suggestions don't show deleted data.
        var scan = AppServices.Instance.LastScan;
        if (scan is not null)
        {
            foreach (var path in paths)
            {
                var node = scan.Find(path);
                if (node is null) continue;
                if (suggestion.Kind == SuggestionKind.RecycleItems)
                {
                    if (!File.Exists(path) && !Directory.Exists(path)) node.Parent?.RemoveChild(node);
                }
                else if (suggestion.Kind == SuggestionKind.CleanFolderContents)
                {
                    foreach (var child in node.Children.ToList())
                    {
                        string childPath = child.Kind == NodeKind.FileGroup ? "" : child.FullPath;
                        bool gone = child.Kind == NodeKind.FileGroup || (!File.Exists(childPath) && !Directory.Exists(childPath));
                        if (gone) node.RemoveChild(child);
                    }
                }
            }
            AfterCleanup?.Invoke();
            FillScanTables();
        }
        CleanAllSafeCommand.NotifyCanExecuteChanged();
    }

    private async void FillScanTables()
    {
        var scan = AppServices.Instance.LastScan;
        if (scan is null)
        {
            LargestFiles.Clear();
            FileTypes.Clear();
            return;
        }

        // Walking millions of nodes takes a moment; keep it off the UI thread.
        var (files, types) = await Task.Run(() =>
        {
            var fileRows = scan.LargestFiles(200).Select(f => new FileRow(f.Name, f.Parent?.FullPath ?? "", f.Size, ByteFormat.Format(f.Size),
                f.LastWriteUtc > DateTime.MinValue ? f.LastWriteUtc.ToLocalTime().ToString("d") : "",
                FileCategories.Of(f.Extension).ToString(), f.FullPath)).ToList();

            long total = Math.Max(1, scan.Extensions.Values.Sum(e => e.Bytes));
            var typeRows = scan.Extensions.Values.OrderByDescending(e => e.Bytes).Take(150)
                .Select(e => new ExtensionRow(e.Extension == "(none)" ? "(no extension)" : "." + e.Extension, e.Category.ToString(),
                    e.Bytes, ByteFormat.Format(e.Bytes), e.Count, 100.0 * e.Bytes / total)).ToList();
            return (fileRows, typeRows);
        });

        if (!ReferenceEquals(scan, AppServices.Instance.LastScan)) return;
        LargestFiles.Clear();
        foreach (var f in files) LargestFiles.Add(f);
        FileTypes.Clear();
        foreach (var t in types) FileTypes.Add(t);
    }

    [RelayCommand]
    private static void OpenFile(FileRow? row)
    {
        if (row is not null) AppServices.OpenInExplorer(row.FullPath);
    }

    [RelayCommand]
    private void GoToDiskMap() => AppServices.Instance.Navigate?.Invoke("disk");
}
