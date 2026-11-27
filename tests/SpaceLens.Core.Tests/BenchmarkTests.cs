using SpaceLens.Core.Advisor;
using SpaceLens.Core.Scanning;
using Xunit.Abstractions;

namespace SpaceLens.Core.Tests;

/// <summary>
/// Read-only scan + analysis of a real folder. Runs only when SPACELENS_BENCH_PATH is set, e.g.
/// <c>set SPACELENS_BENCH_PATH=C:\ &amp;&amp; dotnet test --filter Benchmark</c>.
/// </summary>
public class BenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Benchmark_scan_and_analyze()
    {
        var path = Environment.GetEnvironmentVariable("SPACELENS_BENCH_PATH");
        if (string.IsNullOrEmpty(path)) return;

        var result = await new DiskScanner().ScanAsync([path]);
        output.WriteLine($"Scanned {path}: {ByteFormat.Format(result.Root.Size)}, {result.Root.FileCount:N0} files, " +
                         $"{result.Root.DirCount:N0} folders in {result.Elapsed.TotalSeconds:0.00} s " +
                         $"({result.Root.FileCount / Math.Max(0.001, result.Elapsed.TotalSeconds):N0} files/s), {result.Errors:N0} unreadable folders");

        var measured = DiskScanner.MeasureDirectory(path);
        output.WriteLine($"Cross-check with single-threaded walk: {ByteFormat.Format(measured)}");
        Assert.Equal(measured, result.Root.Size);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var suggestions = new CleanupAdvisor().Analyze(result);
        output.WriteLine($"Advisor: {suggestions.Count} suggestions in {sw.Elapsed.TotalSeconds:0.00} s");
        foreach (var s in suggestions)
        {
            output.WriteLine($"  [{s.Risk}] {s.Title} — {ByteFormat.Format(s.Bytes)} ({s.Items.Count} items)");
            foreach (var item in s.Items.Take(4)) output.WriteLine($"      {ByteFormat.Format(item.Bytes),10}  {item.Path}  {item.Note}");
        }
    }
}
