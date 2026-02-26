using System.Windows.Controls;
using System.Windows.Input;
using VideoSorter.ViewModels;

namespace VideoSorter.Views;

public partial class FolderBarView : UserControl
{
    public FolderBarView()
    {
        InitializeComponent();
    }

    private void Path_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FolderBarViewModel vm)
            vm.BrowseCommand.Execute(null);
    }
}
