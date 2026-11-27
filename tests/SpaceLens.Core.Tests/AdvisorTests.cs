using SpaceLens.Core.Advisor;
using SpaceLens.Core.Scanning;

namespace SpaceLens.Core.Tests;

public class AdvisorTests
{
    private const long MB = 1024 * 1024;
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a synthetic tree (no real files) so rules can be tested with large sizes.</summary>
    private sealed class TreeBuilder
    {
        public FsNode Root { get; } = new(@"Z:\", NodeKind.Directory, null) { IsRead = true };

        public FsNode Dir(string path, DateTime? lastWrite = null)
        {
            var node = Root;
            foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = node.Children.FirstOrDefault(c => c.Name == part);
                if (next is null)
                {
                    next = new FsNode(part, NodeKind.Directory, node) { IsRead = true, LastWriteUtc = lastWrite ?? Now };
                    node.Children = [.. node.Children, next];
                    next.AddUpwards(0, 0, 1);
                }
                node = next;
            }
            return node;
        }

        public FsNode File(string path, long size, DateTime lastWrite)
        {
            var parent = Dir(Path.GetDirectoryName(path)!);
            var file = new FsNode(Path.GetFileName(path), NodeKind.File, parent) { LastWriteUtc = lastWrite, IsRead = true };
            file.SetSize(size);
            file._fileCount = 1;
            parent.Children = [.. parent.Children, file];
            parent.AddUpwards(size, 1, 0);
            return file;
        }

        public ScanResult Result() => new()
        {
            Root = Root,
            Roots = [Root.Name],
            Extensions = new Dictionary<string, ExtensionStat>(),
        };
    }

    [Fact]
    public void Finds_node_modules_and_preselects_stale_projects()
    {
        var tree = new TreeBuilder();
        var old = Now.AddDays(-200);
        tree.Dir(@"code\old-app", old);
        tree.File(@"code\old-app\node_modules\react\index.js", 150 * MB, old);
        tree.File(@"code\old-app\node_modules\nested\node_modules\x.js", 10 * MB, old);
        tree.Dir(@"code\active-app", Now.AddDays(-1));
        tree.File(@"code\active-app\node_modules\lib.js", 80 * MB, Now.AddDays(-1));

        var suggestions = new CleanupAdvisor(() => Now).Analyze(tree.Result());
        var nm = Assert.Single(suggestions, s => s.Id == "node-modules");

        Assert.Equal(2, nm.Items.Count); // nested node_modules is not listed separately
        Assert.Equal(240 * MB, nm.Bytes);
        Assert.True(nm.Items.Single(i => i.Path.Contains("old-app")).Recommended);
        Assert.False(nm.Items.Single(i => i.Path.Contains("active-app")).Recommended);
    }

    [Fact]
    public void Finds_large_old_files_but_not_recent_ones()
    {
        var tree = new TreeBuilder();
        tree.File(@"Videos\holiday-2019.mp4", 2000 * MB, Now.AddDays(-900));
        tree.File(@"Videos\this-week.mp4", 2000 * MB, Now.AddDays(-3));

        var suggestions = new CleanupAdvisor(() => Now).Analyze(tree.Result());
        var s = Assert.Single(suggestions, x => x.Id == "large-old-files");

        var item = Assert.Single(s.Items);
        Assert.EndsWith("holiday-2019.mp4", item.Path);
        Assert.False(item.Recommended); // user files are never pre-selected
    }

    [Fact]
    public void Expand_resolves_wildcard_profile_folders()
    {
        using var dir = new TempDir();
        dir.Dir(@"User Data\Default\Cache");
        dir.Dir(@"User Data\Profile 1\Cache");
        dir.Dir(@"User Data\Profile 2");

        var found = KnownLocations.Expand(Path.Combine(dir.Path, @"User Data\*\Cache")).ToList();

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task Clean_folder_contents_keeps_the_folder_and_reports_bytes()
    {
        using var dir = new TempDir();
        dir.File(@"cache\a.bin", 1000);
        dir.File(@"cache\sub\b.bin", 2000);
        var cache = Path.Combine(dir.Path, "cache");

        var suggestion = new CleanupSuggestion
        {
            Id = "t", Title = "t", Category = "t", Description = "t",
            Kind = SuggestionKind.CleanFolderContents,
            Items = [new SuggestionItem { Path = cache, Bytes = 3000 }],
        };
        var outcome = await CleanupExecutor.ExecuteAsync(suggestion, null);

        Assert.Equal(3000, outcome.BytesFreed);
        Assert.Equal(0, outcome.Failures);
        Assert.True(Directory.Exists(cache));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
    }

    [Fact]
    public void Sampled_hash_detects_identical_and_different_content()
    {
        using var dir = new TempDir();
        var a = dir.File("a.bin", 2_000_000);
        var b = dir.File("b.bin", 2_000_000);
        var c = dir.File("c.bin", 2_000_000);
        using (var fs = new FileStream(c, FileMode.Open)) { fs.Position = 1_000_000; fs.WriteByte(1); }

        var ha = CleanupAdvisor.SampledHash(a, 2_000_000);
        Assert.Equal(ha, CleanupAdvisor.SampledHash(b, 2_000_000));
        Assert.NotEqual(ha, CleanupAdvisor.SampledHash(c, 2_000_000));
    }
}
