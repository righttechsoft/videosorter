using System.Windows.Controls;
using System.Windows.Input;
using VideoSorter.ViewModels;

namespace VideoSorter.Views;

public partial class VideoItemView : UserControl
{
    public VideoItemView()
    {
        InitializeComponent();
    }

    private void FileName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && DataContext is VideoItemViewModel vm)
        {
            vm.StartRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RenameBox_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }
}
