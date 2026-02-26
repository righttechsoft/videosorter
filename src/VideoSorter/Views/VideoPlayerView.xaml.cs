using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using VideoSorter.ViewModels;

namespace VideoSorter.Views;

public partial class VideoPlayerView : UserControl
{
    public VideoPlayerView()
    {
        InitializeComponent();
        VideoView.Loaded += VideoView_Loaded;
    }

    private void VideoView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // The VideoView hosts a native HWND with a default white background brush.
        // Replace it with a black brush to prevent the white flash between videos.
        var hwndSource = System.Windows.PresentationSource.FromVisual(VideoView) as HwndSource;
        if (hwndSource != null)
        {
            var hwnd = hwndSource.Handle;
            // Find the child window (the actual video surface)
            var child = FindWindowEx(hwnd, IntPtr.Zero, null, null);
            if (child != IntPtr.Zero)
                SetClassLongPtr(child, GCL_HBRBACKGROUND, GetStockObject(BLACK_BRUSH));
            SetClassLongPtr(hwnd, GCL_HBRBACKGROUND, GetStockObject(BLACK_BRUSH));
        }
    }

    private const int GCL_HBRBACKGROUND = -10;
    private const int BLACK_BRUSH = 4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtr")]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetClassLong")]
    private static extern int SetClassLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetClassLongPtr64(hWnd, nIndex, dwNewLong);
        return new IntPtr(SetClassLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Ensure WPF has keyboard focus when clicking the video area
        // (VLC's native HWND can steal focus, breaking app-wide hotkeys)
        Overlay.Focus();
    }

    private bool _seekDragging;
    private bool _volumeDragging;

    private static double GetRatio(MouseEventArgs e, FrameworkElement element)
    {
        var x = e.GetPosition(element).X;
        return Math.Clamp(x / element.ActualWidth, 0, 1);
    }

    private void SeekBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _seekDragging = true;
        SeekBar.CaptureMouse();
        var pos = GetRatio(e, SeekBar);
        if (DataContext is VideoPlayerViewModel vm)
            vm.MediaPlayer.Position = (float)pos;
        e.Handled = true;
    }

    private void SeekBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_seekDragging) return;
        var pos = GetRatio(e, SeekBar);
        if (DataContext is VideoPlayerViewModel vm)
            vm.MediaPlayer.Position = (float)pos;
    }

    private void SeekBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seekDragging) return;
        _seekDragging = false;
        SeekBar.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void VolumeBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _volumeDragging = true;
        VolumeBar.CaptureMouse();
        if (DataContext is VideoPlayerViewModel vm)
            vm.Volume = (int)(GetRatio(e, VolumeBar) * 100);
        e.Handled = true;
    }

    private void VolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_volumeDragging) return;
        if (DataContext is VideoPlayerViewModel vm)
            vm.Volume = (int)(GetRatio(e, VolumeBar) * 100);
    }

    private void VolumeBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_volumeDragging) return;
        _volumeDragging = false;
        VolumeBar.ReleaseMouseCapture();
        e.Handled = true;
    }
}
