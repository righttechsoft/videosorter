using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoSorter.ViewModels;

namespace VideoSorter;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;


    public MainWindow()
    {
        InitializeComponent();
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/vo.ico", UriKind.Absolute));
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel = new MainViewModel(App.LibVLC);
        DataContext = _viewModel;

        // Set actual panel width before any folder load can happen
        // (InfoThumbnailStrip is laid out by the time Loaded fires on a Maximized window)
        if (InfoThumbnailStrip.ActualWidth > 0)
            _viewModel.SetInfoPanelWidth(InfoThumbnailStrip.ActualWidth);

        _viewModel.VideoList.ScrollToFirstVideo += OnScrollToFirstVideo;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsComparing))
                UpdateCompareColumnWidth();
            else if (args.PropertyName == nameof(MainViewModel.WindowTitle))
                Title = _viewModel.WindowTitle;
        };
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.VideoList.ScrollToFirstVideo -= OnScrollToFirstVideo;
        _viewModel?.Dispose();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _viewModel?.HandleKeyDownCommand.Execute(e);
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // After clicking any non-text control, reclaim keyboard focus to the window
        // so that hotkeys (Space, Arrow keys, etc.) keep working.
        // Delay with BeginInvoke so the click's own focus logic runs first.
        if (e.OriginalSource is not System.Windows.Controls.TextBox)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
                    Focus();
            });
        }
    }

    private void InfoThumbnailStrip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel?.SetInfoPanelWidth(e.NewSize.Width);
    }

    private void FolderItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is VideoFileEntry entry && entry.IsFolder)
        {
            _viewModel?.VideoList.NavigateToSubfolderCommand.Execute(entry);
            e.Handled = true;
        }
    }

    private void MultiSelectToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
        {
            FileListBox.SelectionMode = toggle.IsChecked == true
                ? SelectionMode.Extended
                : SelectionMode.Single;
        }
    }

    private void FileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null) return;

        _viewModel.VideoList.SelectedFiles.Clear();
        foreach (var item in FileListBox.SelectedItems)
        {
            if (item is VideoFileEntry entry)
                _viewModel.VideoList.SelectedFiles.Add(entry);
        }
    }

    private void UpdateCompareColumnWidth()
    {
        if (_viewModel == null) return;
        CompareColumn.Width = _viewModel.IsComparing
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
    }

    private void OnScrollToFirstVideo(VideoFileEntry? firstVideo)
    {
        if (firstVideo == null) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // Scroll to the last item first, then to the target — forces target to the top
            if (FileListBox.Items.Count > 0)
                FileListBox.ScrollIntoView(FileListBox.Items[^1]);
            FileListBox.ScrollIntoView(firstVideo);
        });
    }

    // --- Comparison shared controls ---

    private bool _compareSeekDragging;
    private bool _compareVolumeDragging;

    private static double GetRatio(MouseEventArgs e, FrameworkElement element)
    {
        var x = e.GetPosition(element).X;
        return Math.Clamp(x / element.ActualWidth, 0, 1);
    }

    private void CompareSeekBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _compareSeekDragging = true;
        CompareSeekBar.CaptureMouse();
        var pos = (float)GetRatio(e, CompareSeekBar);
        if (DataContext is MainViewModel vm)
            vm.CompareSeekTo(pos);
        e.Handled = true;
    }

    private void CompareSeekBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_compareSeekDragging) return;
        var pos = (float)GetRatio(e, CompareSeekBar);
        if (DataContext is MainViewModel vm)
            vm.CompareSeekTo(pos);
    }

    private void CompareSeekBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_compareSeekDragging) return;
        _compareSeekDragging = false;
        CompareSeekBar.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void CompareVolumeBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _compareVolumeDragging = true;
        CompareVolumeBar.CaptureMouse();
        if (DataContext is MainViewModel vm)
            vm.Player.Volume = (int)(GetRatio(e, CompareVolumeBar) * 100);
        e.Handled = true;
    }

    private void CompareVolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_compareVolumeDragging) return;
        if (DataContext is MainViewModel vm)
            vm.Player.Volume = (int)(GetRatio(e, CompareVolumeBar) * 100);
    }

    private void CompareVolumeBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_compareVolumeDragging) return;
        _compareVolumeDragging = false;
        CompareVolumeBar.ReleaseMouseCapture();
        e.Handled = true;
    }
}
