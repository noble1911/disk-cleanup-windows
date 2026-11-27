namespace SpaceLens.Core.Scanning;

public enum NodeKind : byte
{
    /// <summary>A virtual root that holds several drives ("This PC").</summary>
    Computer,
    Directory,
    File,
    /// <summary>Many small files in one folder, merged into a single node to save memory.</summary>
    FileGroup,
}

/// <summary>
/// One entry in the scanned file system tree. Directory sizes grow while a scan runs
/// (sizes are pushed up to every ancestor as each folder is read), so the UI can draw
/// the tree live.
/// </summary>
public sealed class FsNode
{
    private static readonly FsNode[] NoChildren = [];

    internal long _size;
    internal long _fileCount;
    internal long _dirCount;
    private FsNode[] _children = NoChildren;

    public FsNode(string name, NodeKind kind, FsNode? parent)
    {
        Name = name;
        Kind = kind;
        Parent = parent;
    }

    public string Name { get; }
    public NodeKind Kind { get; }
    public FsNode? Parent { get; internal set; }

    /// <summary>Total bytes in this node, including every descendant.</summary>
    public long Size => Volatile.Read(ref _size);

    /// <summary>Number of files in this node and below.</summary>
    public long FileCount => Volatile.Read(ref _fileCount);

    /// <summary>Number of folders below this node.</summary>
    public long DirCount => Volatile.Read(ref _dirCount);

    public DateTime LastWriteUtc { get; internal set; }

    /// <summary>For <see cref="NodeKind.FileGroup"/>: how many files were merged.</summary>
    public int GroupedCount { get; internal set; }

    /// <summary>True when the folder could not be read (usually access denied).</summary>
    public bool AccessDenied { get; internal set; }

    /// <summary>True once this folder's own entries have been read.</summary>
    public bool IsRead { get; internal set; }

    public FsNode[] Children
    {
        get => Volatile.Read(ref _children);
        internal set => Volatile.Write(ref _children, value);
    }

    public bool IsContainer => Kind is NodeKind.Directory or NodeKind.Computer;

    public string FullPath
    {
        get
        {
            if (Kind == NodeKind.Computer) return Name;
            if (Kind == NodeKind.FileGroup) return Parent?.FullPath ?? Name;
            if (Parent is null || Parent.Kind == NodeKind.Computer) return Name;
            return Path.Join(Parent.FullPath, Name);
        }
    }

    public string Extension
    {
        get
        {
            if (Kind != NodeKind.File) return string.Empty;
            int dot = Name.LastIndexOf('.');
            return dot <= 0 || dot == Name.Length - 1 ? string.Empty : Name[(dot + 1)..].ToLowerInvariant();
        }
    }

    public int Depth
    {
        get
        {
            int depth = 0;
            for (var n = Parent; n is not null; n = n.Parent) depth++;
            return depth;
        }
    }

    public bool IsAncestorOf(FsNode other)
    {
        for (var n = other.Parent; n is not null; n = n.Parent)
            if (ReferenceEquals(n, this)) return true;
        return false;
    }

    internal void SetSize(long bytes) => Volatile.Write(ref _size, bytes);

    /// <summary>A display-only node (not attached to the tree) that stands for several items.</summary>
    public static FsNode Summary(string name, FsNode parent, long size, int count)
    {
        var node = new FsNode(name, NodeKind.FileGroup, parent) { GroupedCount = count, IsRead = true };
        node.SetSize(size);
        return node;
    }

    /// <summary>Adds bytes/files to this node and every ancestor (thread-safe).</summary>
    internal void AddUpwards(long bytes, long files, long dirs)
    {
        for (var n = this; n is not null; n = n.Parent)
        {
            if (bytes != 0) Interlocked.Add(ref n._size, bytes);
            if (files != 0) Interlocked.Add(ref n._fileCount, files);
            if (dirs != 0) Interlocked.Add(ref n._dirCount, dirs);
        }
    }

    /// <summary>Removes a child (after it was deleted from disk) and subtracts its totals from all ancestors.</summary>
    public void RemoveChild(FsNode child)
    {
        var current = Children;
        int index = Array.IndexOf(current, child);
        if (index < 0) return;

        var next = new FsNode[current.Length - 1];
        Array.Copy(current, 0, next, 0, index);
        Array.Copy(current, index + 1, next, index, current.Length - index - 1);
        Children = next;

        long dirs = child.DirCount + (child.Kind == NodeKind.Directory ? 1 : 0);
        long files = child.Kind == NodeKind.FileGroup ? child.GroupedCount : child.FileCount;
        AddUpwards(-child.Size, -files, -dirs);
        child.Parent = null;
    }

    /// <summary>Sorts children by size, largest first, for this node and all descendants.</summary>
    public void SortBySizeRecursive()
    {
        var stack = new Stack<FsNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var children = node.Children;
            if (children.Length == 0) continue;
            Array.Sort(children, static (a, b) => b.Size.CompareTo(a.Size));
            foreach (var c in children)
                if (c.IsContainer) stack.Push(c);
        }
    }

    /// <summary>Finds a descendant (or this node) by full path, or null.</summary>
    public FsNode? Find(string path)
    {
        path = path.TrimEnd('\\');
        if (Kind == NodeKind.Computer)
        {
            foreach (var drive in Children)
            {
                var hit = drive.Find(path);
                if (hit is not null) return hit;
            }
            return null;
        }

        string mine = FullPath.TrimEnd('\\');
        if (path.Equals(mine, StringComparison.OrdinalIgnoreCase)) return this;
        if (!path.StartsWith(mine + "\\", StringComparison.OrdinalIgnoreCase)) return null;

        var node = this;
        foreach (var segment in path[(mine.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            FsNode? next = null;
            foreach (var c in node.Children)
            {
                if (c.Kind != NodeKind.FileGroup && c.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = c;
                    break;
                }
            }
            if (next is null) return null;
            node = next;
        }
        return node;
    }

    /// <summary>Depth-first walk of this node and every descendant.</summary>
    public IEnumerable<FsNode> Descendants(Func<FsNode, bool>? descendInto = null)
    {
        var stack = new Stack<FsNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            if (!node.IsContainer) continue;
            if (descendInto is not null && !ReferenceEquals(node, this) && !descendInto(node)) continue;
            var children = node.Children;
            for (int i = children.Length - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    public override string ToString() => $"{FullPath} ({ByteFormat.Format(Size)})";
}
