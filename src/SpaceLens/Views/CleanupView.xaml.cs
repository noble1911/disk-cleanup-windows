using System.Windows.Controls;
using System.Windows.Input;
using SpaceLens.ViewModels;

namespace SpaceLens.Views;

public partial class CleanupView : UserControl
{
    public CleanupView() => InitializeComponent();

    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is CleanupViewModel vm && FilesGrid.SelectedItem is FileRow row) vm.OpenFileCommand.Execute(row);
    }
}
