using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceLens.Services;

namespace SpaceLens.ViewModels;

public sealed record NavItem(string Key, string Title, string Glyph, string Hint, object Page);

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        DiskMap = new DiskMapViewModel();
        Cleanup = new CleanupViewModel { AfterCleanup = DiskMap.Refresh };
        Monitor = new MonitorViewModel();
        Health = new HealthViewModel();

        Pages =
        [
            new("disk", "Disk Map", "", "See what's using space", DiskMap),
            new("cleanup", "Cleanup Advisor", "", "Free up space safely", Cleanup),
            new("monitor", "System Monitor", "", "Live CPU, memory, disk, network", Monitor),
            new("health", "Health Check", "", "Diagnose slowdowns", Health),
        ];
        _selectedPage = Pages[0];

        AppServices.Instance.Navigate = key =>
        {
            var page = Pages.FirstOrDefault(p => p.Key == key);
            if (page is not null) SelectedPage = page;
        };
    }

    public DiskMapViewModel DiskMap { get; }
    public CleanupViewModel Cleanup { get; }
    public MonitorViewModel Monitor { get; }
    public HealthViewModel Health { get; }
    public IReadOnlyList<NavItem> Pages { get; }

    public bool IsElevated => AppServices.Instance.IsElevated;
    public string ElevationText => IsElevated ? "Running as administrator" : "Standard user mode";

    [ObservableProperty] private NavItem _selectedPage;

    partial void OnSelectedPageChanged(NavItem value)
    {
        // Pages that summarise data refresh themselves when opened.
        if (value.Page == Health && !Health.IsChecking) _ = Health.Check();
        if (value.Page == Cleanup && Cleanup.Suggestions.Count == 0 && !Cleanup.IsAnalyzing) _ = Cleanup.AnalyzeCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private static void RestartAsAdmin() => AppServices.RestartElevated();
}
