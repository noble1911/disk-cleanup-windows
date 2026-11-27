using System.Diagnostics;
using SpaceLens.Core.Scanning;

namespace SpaceLens.Core.Tests;

public class ScanningTests
{
    [Fact]
    public async Task Scan_totals_sizes_and_counts()
    {
        using var dir = new TempDir();
        dir.File(@"a\big.bin", 400_000);
        dir.File(@"a\b\c\deep.bin", 300_000);
        for (int i = 0; i < 20; i++) dir.File($@"small\f{i}.txt", 1_000);
        dir.Dir("empty");

        var scanner = new DiskScanner(new ScanOptions { KeepFileThreshold = 256 * 1024 });
        var result = await scanner.ScanAsync([dir.Path]);

        Assert.False(result.Cancelled);
        Assert.Equal(400_000 + 300_000 + 20 * 1_000, result.Root.Size);
        Assert.Equal(22, result.Root.FileCount);

        var small = result.Find(Path.Combine(dir.Path, "small"))!;
        Assert.Single(small.Children);
        Assert.Equal(NodeKind.FileGroup, small.Children[0].Kind);
        Assert.Equal(20, small.Children[0].GroupedCount);
        Assert.Equal(20_000, small.Size);

        var deep = result.Find(Path.Combine(dir.Path, @"a\b\c\deep.bin"))!;
        Assert.Equal(NodeKind.File, deep.Kind);
        Assert.Equal(300_000, deep.Size);

        // Children are sorted largest first.
        var a = result.Find(Path.Combine(dir.Path, "a"))!;
        Assert.Equal("big.bin", a.Children[0].Name);

        Assert.Equal(20, result.Extensions["txt"].Count);
    }

    [Fact]
    public async Task Scan_does_not_follow_junctions()
    {
        using var dir = new TempDir();
        dir.File(@"real\data.bin", 500_000);
        var link = Path.Combine(dir.Path, "link");
        var target = Path.Combine(dir.Path, "real");
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link));

        var result = await new DiskScanner().ScanAsync([dir.Path]);

        Assert.Equal(500_000, result.Root.Size);
        Assert.Null(result.Find(link));
    }

    [Fact]
    public async Task Multiple_roots_get_a_virtual_computer_node()
    {
        using var one = new TempDir();
        using var two = new TempDir();
        one.File("x.bin", 1000);
        two.File("y.bin", 2000);

        var result = await new DiskScanner().ScanAsync([one.Path, two.Path]);

        Assert.Equal(NodeKind.Computer, result.Root.Kind);
        Assert.Equal(2, result.Root.Children.Length);
        Assert.Equal(3000, result.Root.Size);
        Assert.NotNull(result.Find(Path.Combine(two.Path, "y.bin")));
    }

    [Fact]
    public async Task RemoveChild_subtracts_from_ancestors()
    {
        using var dir = new TempDir();
        dir.File(@"a\b\file.bin", 300_000);
        dir.File(@"a\keep.bin", 300_000);

        var result = await new DiskScanner().ScanAsync([dir.Path]);
        var b = result.Find(Path.Combine(dir.Path, @"a\b"))!;
        b.Parent!.RemoveChild(b);

        Assert.Equal(300_000, result.Root.Size);
        Assert.Equal(1, result.Root.FileCount);
        Assert.Null(result.Find(Path.Combine(dir.Path, @"a\b")));
    }

    [Fact]
    public void Measure_directory_matches_file_sizes()
    {
        using var dir = new TempDir();
        dir.File(@"x\1.bin", 1234);
        dir.File(@"x\y\2.bin", 4321);
        Assert.Equal(5555, DiskScanner.MeasureDirectory(dir.Path));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(10L * 1024 * 1024 * 1024, "10.0 GB")]
    public void Byte_format(long bytes, string expected)
    {
        Assert.Equal(expected, ByteFormat.Format(bytes).Replace(',', '.'));
    }
}
