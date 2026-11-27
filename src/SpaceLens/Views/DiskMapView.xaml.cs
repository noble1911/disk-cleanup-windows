using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SpaceLens.Core.Scanning;
using SpaceLens.Services;
using SpaceLens.ViewModels;

namespace SpaceLens.Views;

public partial class DiskMapView : UserControl
{
    public DiskMapView() => InitializeComponent();

    private DiskMapViewModel? Vm => DataContext as DiskMapViewModel;

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Vm is { } vm && e.NewValue is TreeItemViewModel item) vm.SelectedNode = item.Node;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || Tree.SelectedItem is not TreeItemViewModel item) return;
        if (item.Node.IsContainer && !ReferenceEquals(item.Node, Vm.CurrentNode))
        {
            Vm.ZoomToCommand.Execute(item.Node);
            e.Handled = true;
        }
    }

    private void Map_HoverChanged(object? sender, FsNode? node)
    {
        if (Vm is not null) Vm.HoveredNode = node;
    }

    private void Map_NodeActivated(object? sender, FsNode node) => Vm?.ZoomToCommand.Execute(node);

    private void Map_NodeContextRequested(object? sender, FsNode node)
    {
        if (Vm is null) return;
        var vm = Vm;
        var menu = new ContextMenu();

        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
            return mi;
        }

        var target = node.IsContainer ? node : node.Parent;
        Item(node.IsContainer ? "Zoom into this folder" : "Zoom into containing folder", () => vm.ZoomToCommand.Execute(target), target is not null);
        Item("Open in Explorer", () => AppServices.OpenInExplorer(node.FullPath));
        Item("Copy path", () => vm.CopyPathCommand.Execute(node));
        menu.Items.Add(new Separator());
        Item("Move to Recycle Bin…", () => vm.RecycleSelectedCommand.Execute(null), vm.RecycleSelectedCommand.CanExecute(null));

        menu.PlacementTarget = Map;
        menu.IsOpen = true;
    }
}
