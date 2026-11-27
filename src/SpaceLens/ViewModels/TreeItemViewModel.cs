using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceLens.Core;
using SpaceLens.Core.Scanning;

namespace SpaceLens.ViewModels;

/// <summary>A row in the folder tree. Children are created lazily when the row is expanded.</summary>
public sealed partial class TreeItemViewModel : ObservableObject
{
    private const int MaxChildren = 300;
    private static readonly TreeItemViewModel Placeholder = new();
    private bool _loaded;

    private TreeItemViewModel() => Node = new FsNode("", NodeKind.File, null);

    public TreeItemViewModel(FsNode node, TreeItemViewModel? parent)
    {
        Node = node;
        Parent = parent;
        if (node.IsContainer && node.Children.Length > 0) Children.Add(Placeholder);
    }

    public FsNode Node { get; }
    public TreeItemViewModel? Parent { get; }
    public ObservableCollection<TreeItemViewModel> Children { get; } = [];

    public string Name => Node.Parent is null || Node.Parent.Kind == NodeKind.Computer ? Node.FullPath : Node.Name;
    public string SizeText => ByteFormat.Format(Node.Size);

    /// <summary>Share of the parent's size, 0–100.</summary>
    public double Percent => Parent is null || Parent.Node.Size == 0 ? 100 : 100.0 * Node.Size / Parent.Node.Size;
    public string PercentText => $"{Percent:0.#}%";

    public string Glyph => Node.Kind switch
    {
        NodeKind.Computer => "",   // Devices / PC
        NodeKind.Directory when Node.Parent is null || Node.Parent.Kind == NodeKind.Computer => "", // HardDrive
        NodeKind.Directory => "",  // Folder
        NodeKind.FileGroup => "",  // ViewAll
        _ => "",                   // Document
    };

    public bool IsWarning => Node.AccessDenied;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _loaded) return;
        _loaded = true;
        Children.Clear();
        var kids = Node.Children.OrderByDescending(c => c.Size).ToList();
        foreach (var child in kids.Take(MaxChildren)) Children.Add(new TreeItemViewModel(child, this));
        if (kids.Count > MaxChildren)
        {
            var restNode = FsNode.Summary($"… {kids.Count - MaxChildren:N0} more items", Node, kids.Skip(MaxChildren).Sum(k => k.Size), kids.Count - MaxChildren);
            Children.Add(new TreeItemViewModel(restNode, this));
        }
    }
}
