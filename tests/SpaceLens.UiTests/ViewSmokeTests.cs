using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SpaceLens.Core.Scanning;
using SpaceLens.Services;
using SpaceLens.ViewModels;
using SpaceLens.Views;
using Xunit.Abstractions;

namespace SpaceLens.UiTests;

/// <summary>
/// Loads every real view with its real view model (without showing a window) and drives the
/// main flows: scan → map → cleanup analysis, live monitor updates, health check.
/// XAML errors, missing resources and binding failures make these tests fail.
/// </summary>
public class ViewSmokeTests(ITestOutputHelper output)
{
    private static readonly object Gate = new();
    private static App? _app;

    private static void RunSta(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            lock (Gate)
            {
                if (Application.Current is null)
                {
                    _app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    _app.InitializeComponent(); // loads App.xaml resources (theme, converters)
                }
            }

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try { await body(); }
                catch (Exception ex) { failure = ex; }
                finally { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("UI test failed: " + failure.Message, failure);
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message is not null) Errors.Add(message);
        }
    }

    private static BindingErrorListener ListenForBindingErrors()
    {
        var listener = new BindingErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        return listener;
    }

    private static Window Host(FrameworkElement view, object vm)
    {
        view.DataContext = vm;
        var window = new Window
        {
            Content = view,
            Width = 1360,
            Height = 860,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -20000, // off-screen: the test never shows anything to the user
            Top = -20000,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static async Task Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) await Task.Delay(50);
    }

    [Fact]
    public void Main_window_loads_all_pages()
    {
        RunSta(async () =>
        {
            var errors = ListenForBindingErrors();
            var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            var vm = (MainViewModel)window.DataContext;
            foreach (var page in vm.Pages)
            {
                vm.SelectedPage = page;
                window.UpdateLayout();
                await Pump(300);
            }
            window.Close();
            Assert.True(errors.Errors.Count == 0, "Binding errors:\n" + string.Join("\n", errors.Errors));
        });
    }

    [Fact]
    public void Disk_map_scans_and_cleanup_analyzes_a_real_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SpaceLensUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, @"app\node_modules\lib"));
        File.WriteAllBytes(Path.Combine(folder, @"app\package.json"), new byte[10]);
        File.WriteAllBytes(Path.Combine(folder, @"app\node_modules\lib\big.bin"), new byte[600_000]);
        for (int i = 0; i < 30; i++) File.WriteAllBytes(Path.Combine(folder, $"f{i}.txt"), new byte[1000]);

        try
        {
            RunSta(async () =>
            {
                var errors = ListenForBindingErrors();
                var diskVm = new DiskMapViewModel();
                var diskWindow = Host(new DiskMapView(), diskVm);

                await diskVm.ScanPath(folder);
                diskWindow.UpdateLayout();

                Assert.NotNull(AppServices.Instance.LastScan);
                Assert.Equal(600_010 + 30_000, diskVm.RootNode!.Size);
                Assert.StartsWith("Done.", diskVm.Status);
                Assert.Single(diskVm.TreeRoots);
                Assert.True(diskVm.TreeRoots[0].Children.Count >= 2);

                // Zoom into a folder and back up.
                var app = diskVm.RootNode.Find(Path.Combine(folder, "app"))!;
                diskVm.ZoomToCommand.Execute(app);
                Assert.Same(app, diskVm.CurrentNode);
                Assert.True(diskVm.Breadcrumbs.Count >= 2);
                diskVm.GoUpCommand.Execute(null);
                Assert.Same(diskVm.RootNode, diskVm.CurrentNode);

                diskVm.ToggleColorModeCommand.Execute(null);
                diskWindow.UpdateLayout();
                diskWindow.Close();

                var cleanupVm = new CleanupViewModel();
                var cleanupWindow = Host(new CleanupView(), cleanupVm);
                await cleanupVm.AnalyzeCommand.ExecuteAsync(null);
                await Pump(500);
                cleanupWindow.UpdateLayout();
                output.WriteLine(cleanupVm.Status);
                foreach (var s in cleanupVm.Suggestions) output.WriteLine($"  [{s.RiskText}] {s.Title} — {s.SizeText}");
                Assert.Contains(cleanupVm.LargestFiles, f => f.Name == "big.bin");
                Assert.Contains(cleanupVm.FileTypes, t => t.Extension == ".txt");
                cleanupWindow.Close();

                Assert.True(errors.Errors.Count == 0, "Binding errors:\n" + string.Join("\n", errors.Errors));
            });
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Monitor_and_health_pages_show_live_data()
    {
        RunSta(async () =>
        {
            var errors = ListenForBindingErrors();
            AppServices.Instance.Monitor.Start();

            var monitorVm = new MonitorViewModel();
            var monitorWindow = Host(new MonitorView(), monitorVm);
            var started = Stopwatch.StartNew();
            while (monitorVm.CpuSeries.Length < 3 && started.Elapsed < TimeSpan.FromSeconds(15)) await Pump(100);
            output.WriteLine($"First 3 samples after {started.Elapsed.TotalSeconds:0.0} s");
            monitorWindow.UpdateLayout();

            Assert.NotEmpty(monitorVm.Processes);
            Assert.Contains(monitorVm.Processes, p => p.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || p.Name == "System");
            Assert.True(monitorVm.MemoryPercent > 0);
            Assert.NotEmpty(monitorVm.CpuSeries);
            output.WriteLine("Monitor: " + monitorVm.Summary);
            output.WriteLine($"  CPU {monitorVm.CpuPercent:0}% · {monitorVm.MemoryText} · disk {monitorVm.DiskText} · net {monitorVm.NetworkText}");

            monitorVm.GroupByApp = false;
            await Pump(1200);
            Assert.Contains(monitorVm.Processes, p => p.Count == 1);
            monitorVm.Filter = "zzzz-no-such-process";
            Assert.True(monitorVm.ProcessView.IsEmpty);
            monitorWindow.Close();

            var healthVm = new HealthViewModel();
            var healthWindow = Host(new HealthView(), healthVm);
            await healthVm.Check();
            healthWindow.UpdateLayout();
            Assert.NotEmpty(healthVm.Findings);
            Assert.NotEmpty(healthVm.SystemText);
            output.WriteLine($"Health: {healthVm.Verdict} (score {healthVm.Score}) — {healthVm.HardwareText}");
            foreach (var f in healthVm.Findings) output.WriteLine($"  [{f.Severity}] {f.Title}");
            healthWindow.Close();

            Assert.True(errors.Errors.Count == 0, "Binding errors:\n" + string.Join("\n", errors.Errors));
        });
    }
}
