using System.Collections.Concurrent;
using System.Diagnostics;
using SpaceLens.Core.Native;

namespace SpaceLens.Core.Scanning;

public sealed class ScanOptions
{
    /// <summary>Files at least this big are kept as individual nodes; smaller ones are merged per folder.</summary>
    public long KeepFileThreshold { get; init; } = 256 * 1024;

    /// <summary>Folders with at most this many small files keep them individually anyway.</summary>
    public int KeepAllFilesBelowCount { get; init; } = 8;

    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 16);
}

/// <summary>Live counters a UI can poll while a scan runs.</summary>
public sealed class ScanProgress
{
    internal long _files;
    internal long _dirs;
    internal long _bytes;
    internal long _errors;
    private string _currentPath = string.Empty;

    public long Files => Interlocked.Read(ref _files);
    public long Directories => Interlocked.Read(ref _dirs);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Errors => Interlocked.Read(ref _errors);

    public string CurrentPath
    {
        get => Volatile.Read(ref _currentPath);
        internal set => Volatile.Write(ref _currentPath, value);
    }

    public Stopwatch Elapsed { get; } = new();
}

public sealed class ScanResult
{
    public required FsNode Root { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }
    public required IReadOnlyDictionary<string, ExtensionStat> Extensions { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long Errors { get; init; }
    public bool Cancelled { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.Now;

    public FsNode? Find(string path) => Root.Find(path);

    /// <summary>True if <paramref name="path"/> lies inside one of the scanned roots.</summary>
    public bool Covers(string path)
    {
        foreach (var root in Roots)
        {
            var r = root.TrimEnd('\\');
            if (path.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public IReadOnlyList<FsNode> LargestFiles(int count)
    {
        var heap = new PriorityQueue<FsNode, long>();
        foreach (var n in Root.Descendants())
        {
            if (n.Kind != NodeKind.File) continue;
            if (heap.Count < count) heap.Enqueue(n, n.Size);
            else if (n.Size > heap.Peek().Size) heap.EnqueueDequeue(n, n.Size);
        }
        var list = new List<FsNode>(heap.Count);
        while (heap.Count > 0) list.Add(heap.Dequeue());
        list.Reverse();
        return list;
    }
}

/// <summary>
/// Fast multi-threaded scanner built on FindFirstFileEx (basic info, large fetch).
/// Junctions and symbolic links are not followed, so nothing is counted twice through
/// links such as "Documents and Settings". Cloud placeholders (OneDrive online-only files)
/// count as zero bytes because their data is not on the disk.
/// </summary>
public sealed class DiskScanner
{
    private readonly ScanOptions _options;

    public DiskScanner(ScanOptions? options = null) => _options = options ?? new ScanOptions();

    public ScanProgress Progress { get; private set; } = new();

    /// <summary>The tree being built. Available as soon as the scan starts, for live display.</summary>
    public FsNode? Root { get; private set; }

    public static string NormalizeRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length == 2 && full[1] == ':') full += "\\";
        if (full.Length > 3) full = full.TrimEnd('\\');
        return full;
    }

    /// <summary>Creates the root node so the caller can show it before <see cref="ScanAsync"/> fills it.</summary>
    public FsNode Prepare(IReadOnlyList<string> roots)
    {
        Progress = new ScanProgress();
        var normalized = roots.Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (normalized.Count == 0) throw new ArgumentException("No folders to scan.", nameof(roots));

        if (normalized.Count == 1)
        {
            Root = new FsNode(normalized[0], NodeKind.Directory, null);
        }
        else
        {
            var computer = new FsNode("This PC", NodeKind.Computer, null);
            computer.Children = normalized.Select(r => new FsNode(r, NodeKind.Directory, computer)).ToArray();
            computer.IsRead = true;
            Root = computer;
        }
        return Root;
    }

    public async Task<ScanResult> ScanAsync(IReadOnlyList<string> roots, CancellationToken ct = default)
    {
        var root = Root is not null && SameRoots(Root, roots) ? Root : Prepare(roots);
        var progress = Progress;
        progress.Elapsed.Restart();

        var stack = new ConcurrentStack<FsNode>();
        var signal = new SemaphoreSlim(0);
        long pending = 0;

        void Enqueue(FsNode dir)
        {
            Interlocked.Increment(ref pending);
            stack.Push(dir);
            signal.Release();
        }

        if (root.Kind == NodeKind.Computer)
            foreach (var drive in root.Children) Enqueue(drive);
        else
            Enqueue(root);

        var extTables = new ConcurrentBag<Dictionary<string, ExtensionStat>>();
        int workers = _options.MaxParallelism;
        bool cancelled = false;

        async Task Worker()
        {
            var ext = new Dictionary<string, ExtensionStat>(StringComparer.OrdinalIgnoreCase);
            extTables.Add(ext);
            var subdirs = new List<FsNode>();
            while (true)
            {
                try
                {
                    await signal.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    return;
                }

                if (!stack.TryPop(out var dir))
                {
                    // Woken by the "all done" release.
                    if (Interlocked.Read(ref pending) == 0) return;
                    continue;
                }

                subdirs.Clear();
                ReadDirectory(dir, progress, ext, subdirs);
                foreach (var sub in subdirs) Enqueue(sub);

                if (Interlocked.Decrement(ref pending) == 0)
                {
                    signal.Release(workers);
                    return;
                }
            }
        }

        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(Worker, CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        progress.Elapsed.Stop();

        await Task.Run(root.SortBySizeRecursive, CancellationToken.None).ConfigureAwait(false);

        var merged = new Dictionary<string, ExtensionStat>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in extTables)
        foreach (var (key, stat) in table)
        {
            if (!merged.TryGetValue(key, out var m)) merged[key] = m = new ExtensionStat(key);
            m.Bytes += stat.Bytes;
            m.Count += stat.Count;
        }

        return new ScanResult
        {
            Root = root,
            Roots = root.Kind == NodeKind.Computer ? root.Children.Select(c => c.Name).ToList() : [root.Name],
            Extensions = merged,
            Elapsed = progress.Elapsed.Elapsed,
            Errors = progress.Errors,
            Cancelled = cancelled || ct.IsCancellationRequested,
        };
    }

    private static bool SameRoots(FsNode root, IReadOnlyList<string> roots)
    {
        var names = root.Kind == NodeKind.Computer ? root.Children.Select(c => c.Name).ToList() : [root.Name];
        var wanted = roots.Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count == wanted.Count && names.All(n => wanted.Contains(n, StringComparer.OrdinalIgnoreCase));
    }

    private void ReadDirectory(FsNode dir, ScanProgress progress, Dictionary<string, ExtensionStat> ext, List<FsNode> subdirs)
    {
        string path = dir.FullPath;
        progress.CurrentPath = path;
        string search = Win32.ToLongPath(path.EndsWith('\\') ? path + "*" : path + "\\*");

        var handle = Win32.FindFirstFileExW(search, Win32.FINDEX_INFO_LEVELS.FindExInfoBasic, out var data,
            Win32.FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, Win32.FIND_FIRST_EX_LARGE_FETCH);

        if (handle == Win32.InvalidHandle)
        {
            dir.AccessDenied = true;
            dir.IsRead = true;
            Interlocked.Increment(ref progress._errors);
            return;
        }

        var files = new List<FsNode>();
        var small = new List<(string Name, long Size, DateTime Write)>();
        long totalBytes = 0;
        long fileCount = 0;

        try
        {
            do
            {
                string name = data.cFileName;
                if (name is "." or "..") continue;

                uint attrs = data.dwFileAttributes;
                var lastWrite = Win32.FromFileTime(data.ftLastWriteTime.Value);

                if ((attrs & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    // Never follow junctions / symlinks: they point at data counted elsewhere.
                    if ((attrs & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;
                    subdirs.Add(new FsNode(name, NodeKind.Directory, dir) { LastWriteUtc = lastWrite });
                    continue;
                }

                long size = (attrs & Win32.NotLocalMask) != 0
                    ? 0
                    : ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;

                totalBytes += size;
                fileCount++;

                int dot = name.LastIndexOf('.');
                string extension = dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : "(none)";
                if (!ext.TryGetValue(extension, out var stat)) ext[extension] = stat = new ExtensionStat(extension.ToLowerInvariant());
                stat.Bytes += size;
                stat.Count++;

                if (size >= _options.KeepFileThreshold)
                    files.Add(MakeFile(dir, name, size, lastWrite));
                else
                    small.Add((name, size, lastWrite));
            }
            while (Win32.FindNextFileW(handle, out data));
        }
        finally
        {
            Win32.FindClose(handle);
        }

        if (small.Count <= _options.KeepAllFilesBelowCount)
        {
            foreach (var (name, size, write) in small) files.Add(MakeFile(dir, name, size, write));
        }
        else
        {
            var group = new FsNode($"{small.Count:N0} smaller files", NodeKind.FileGroup, dir)
            {
                GroupedCount = small.Count,
                LastWriteUtc = small.Max(s => s.Write),
            };
            group.SetSize(small.Sum(s => s.Size));
            files.Add(group);
        }

        var children = new FsNode[subdirs.Count + files.Count];
        subdirs.CopyTo(children, 0);
        files.CopyTo(children, subdirs.Count);
        dir.Children = children;
        dir.IsRead = true;

        dir.AddUpwards(totalBytes, fileCount, subdirs.Count);
        Interlocked.Add(ref progress._files, fileCount);
        Interlocked.Add(ref progress._bytes, totalBytes);
        Interlocked.Increment(ref progress._dirs);
    }

    private static FsNode MakeFile(FsNode dir, string name, long size, DateTime lastWrite)
    {
        var node = new FsNode(name, NodeKind.File, dir) { LastWriteUtc = lastWrite, IsRead = true };
        node.SetSize(size);
        node._fileCount = 1;
        return node;
    }

    /// <summary>Single-threaded total size of a folder (used for quick checks outside a full scan).</summary>
    public static long MeasureDirectory(string path, CancellationToken ct = default, long stopAfterBytes = long.MaxValue)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path.TrimEnd('\\'));
        while (stack.Count > 0 && !ct.IsCancellationRequested && total < stopAfterBytes)
        {
            string dir = stack.Pop();
            var handle = Win32.FindFirstFileExW(Win32.ToLongPath(dir + "\\*"), Win32.FINDEX_INFO_LEVELS.FindExInfoBasic,
                out var data, Win32.FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, Win32.FIND_FIRST_EX_LARGE_FETCH);
            if (handle == Win32.InvalidHandle) continue;
            try
            {
                do
                {
                    if (data.cFileName is "." or "..") continue;
                    uint attrs = data.dwFileAttributes;
                    if ((attrs & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        if ((attrs & Win32.FILE_ATTRIBUTE_REPARSE_POINT) == 0) stack.Push(dir + "\\" + data.cFileName);
                    }
                    else if ((attrs & Win32.NotLocalMask) == 0)
                    {
                        total += ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    }
                }
                while (Win32.FindNextFileW(handle, out data));
            }
            finally
            {
                Win32.FindClose(handle);
            }
        }
        return total;
    }

    /// <summary>Size of a single file (works for hidden system files such as hiberfil.sys).</summary>
    public static long FileSize(string path)
    {
        if (!Win32.GetFileAttributesExW(Win32.ToLongPath(path), 0, out var info)) return -1;
        if ((info.dwFileAttributes & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0) return -1;
        return ((long)info.nFileSizeHigh << 32) | info.nFileSizeLow;
    }
}
