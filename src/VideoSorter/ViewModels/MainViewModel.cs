using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using VideoSorter.Helpers;
using VideoSorter.Models;
using VideoSorter.Services;

namespace VideoSorter.ViewModels;

public class InfoThumbnail
{
    public BitmapImage Image { get; init; } = null!;
    public float Position { get; init; } // 0..1
}

public enum SyncUnit
{
    Milliseconds,
    Frames,
    Seconds
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly FfmpegService _ffmpegService;
    private readonly CacheService _cacheService;
    private readonly FileOperationService _fileService;
    private double _infoPanelWidth;
    private CancellationTokenSource? _infoThumbCts;
    private bool _isRestoringMode3D;
    private System.Timers.Timer? _syncTimer;

    [ObservableProperty]
    private VideoFileInfo? _selectedInfo;

    [ObservableProperty]
    private string _editFileName = string.Empty;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _isLoadingInfo;

    [ObservableProperty]
    private bool _isComparing;

    [ObservableProperty]
    private string _windowTitle = "Video Sorter";

    [ObservableProperty]
    private long _syncOffsetMs;

    [ObservableProperty]
    private SyncUnit _selectedSyncUnit = SyncUnit.Milliseconds;

    public string SyncOffsetDisplay => SelectedSyncUnit switch
    {
        SyncUnit.Frames => $"{(SyncOffsetMs >= 0 ? "+" : "")}{SyncOffsetMs / 33} frames",
        SyncUnit.Seconds => $"{(SyncOffsetMs >= 0 ? "+" : "")}{SyncOffsetMs / 1000.0:F1}s",
        _ => $"{(SyncOffsetMs >= 0 ? "+" : "")}{SyncOffsetMs}ms"
    };

    public static IReadOnlyList<SyncUnit> SyncUnits { get; } = Enum.GetValues<SyncUnit>();

    public FolderBarViewModel FolderBar { get; }
    public VideoListViewModel VideoList { get; }
    public VideoPlayerViewModel Player { get; }
    public VideoPlayerViewModel ComparePlayer { get; }
    public ObservableCollection<InfoThumbnail> InfoThumbnails { get; } = [];

    public MainViewModel(LibVLC libVlc)
    {
        _fileService = new FileOperationService();
        _ffmpegService = new FfmpegService();
        _cacheService = new CacheService();

        FolderBar = new FolderBarViewModel(OnFolderChanged);
        VideoList = new VideoListViewModel(_fileService, _ffmpegService, _cacheService, OnFileSelected);
        VideoList.SetFolderNavigator(path => FolderBar.NavigateTo(path));
        VideoList.SetSpreadThumbCountFunc(CalculateSpreadThumbCount);
        VideoList.StatusChanged += OnStatusChanged;
        Player = new VideoPlayerViewModel(libVlc);
        ComparePlayer = new VideoPlayerViewModel(libVlc) { ShowControls = false };
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Player.Mode3D))
                OnPlayer3DModeChanged();
        };

        _syncTimer = new System.Timers.Timer(500);
        _syncTimer.Elapsed += (_, _) => CheckDrift();
    }

    partial void OnSyncOffsetMsChanged(long value) => OnPropertyChanged(nameof(SyncOffsetDisplay));
    partial void OnSelectedSyncUnitChanged(SyncUnit value) => OnPropertyChanged(nameof(SyncOffsetDisplay));

    private void OnStatusChanged(string status)
    {
        WindowTitle = string.IsNullOrEmpty(status)
            ? "Video Sorter"
            : $"Video Sorter — {status}";
    }

    private void OnFolderChanged(string path)
    {
        ThreadPool.QueueUserWorkItem(_ => Player.MediaPlayer.Stop());
        SelectedInfo = null;
        HasSelection = false;
        VideoList.LoadFolder(path);
    }

    private async void OnFileSelected(VideoFileEntry? entry)
    {
        if (IsComparing) return;

        if (entry == null)
        {
            SelectedInfo = null;
            HasSelection = false;
            InfoThumbnails.Clear();
            return;
        }

        HasSelection = true;
        IsLoadingInfo = true;
        EditFileName = entry.FileName;
        InfoThumbnails.Clear();

        // Load metadata (from cache or ffprobe) to get saved 3D mode
        var fi = new FileInfo(entry.FilePath);
        var cacheKey = HashHelper.ComputeCacheKey(entry.FilePath, fi.LastWriteTimeUtc, fi.Length);
        var cached = _cacheService.Get(cacheKey);

        if (cached != null)
        {
            SelectedInfo = cached;
            // Restore saved 3D mode before playing (suppress save-back and auto-replay)
            _isRestoringMode3D = true;
            Player.SetMode3DSilently((Video3DMode)cached.Mode3D);
            _isRestoringMode3D = false;
        }
        else
        {
            _isRestoringMode3D = true;
            Player.SetMode3DSilently(Video3DMode.Off);
            _isRestoringMode3D = false;
            var info = await _ffmpegService.ExtractMetadataAsync(entry.FilePath, cacheKey);
            if (info != null)
            {
                _cacheService.Save(info);
                SelectedInfo = info;
            }
        }

        // Play after 3D mode is set
        Player.PlayFile(entry.FilePath);

        IsLoadingInfo = false;
        _ = RefreshInfoThumbnailsAsync();
    }

    public void SetInfoPanelWidth(double width)
    {
        if (Math.Abs(width - _infoPanelWidth) < 1) return;
        _infoPanelWidth = width;
        _ = RefreshInfoThumbnailsAsync();
    }

    private int CalculateSpreadThumbCount(int videoWidth = 0, int videoHeight = 0) => 10;

    private static void Log(string msg)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoSorter", "debug.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private async Task RefreshInfoThumbnailsAsync()
    {
        _infoThumbCts?.Cancel();
        _infoThumbCts = new CancellationTokenSource();
        var ct = _infoThumbCts.Token;

        InfoThumbnails.Clear();
        var count = CalculateSpreadThumbCount();
        Log($"RefreshInfo: file={SelectedInfo?.FileName} count={count} panelW={_infoPanelWidth}");
        if (count <= 0) return;

        var thumbDir = _cacheService.GetThumbnailDirectory(SelectedInfo!.CacheKey);
        try
        {
            Log($"RefreshInfo: calling GenerateAllThumbnailsAsync spreadCount={count} thumbDir={thumbDir}");
            // Generate all thumbnails (skips files already on disk)
            var (_, paths) = await _ffmpegService.GenerateAllThumbnailsAsync(
                SelectedInfo.FilePath, SelectedInfo.Duration, thumbDir, count,
                (int)Player.Mode3D, ct);

            Log($"RefreshInfo: got {paths.Count} spread paths");
            if (ct.IsCancellationRequested) return;

            var duration = SelectedInfo.Duration.TotalSeconds;
            var interval = duration / count;
            for (int i = 0; i < paths.Count; i++)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(paths[i], UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();

                var thumbTime = interval * i + interval / 2;
                InfoThumbnails.Add(new InfoThumbnail
                {
                    Image = bmp,
                    Position = duration > 0 ? (float)(thumbTime / duration) : 0f
                });
            }
            Log($"RefreshInfo: loaded {InfoThumbnails.Count} thumbnails into UI");
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void SeekToThumbnail(InfoThumbnail thumb)
    {
        Player.MediaPlayer.Position = thumb.Position;
    }

    private void OnPlayer3DModeChanged()
    {
        if (_isRestoringMode3D) return;
        var entry = VideoList.SelectedFile;
        if (entry == null || entry.IsFolder) return;

        var mode3dInt = (int)Player.Mode3D;
        entry.Mode3D = mode3dInt;

        // Save to cache
        _cacheService.UpdateMode3D(entry.CacheKey, mode3dInt);

        // Regenerate thumbnails with new 3D crop
        _ = VideoList.RegenerateThumbnailsAsync(entry);
    }

    [RelayCommand]
    private void Rename()
    {
        if (SelectedInfo == null || VideoList.SelectedFile == null) return;

        var newName = EditFileName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == SelectedInfo.FileName) return;

        if (_fileService.RenameFile(SelectedInfo.FilePath, newName))
        {
            var dir = Path.GetDirectoryName(SelectedInfo.FilePath)!;
            var newPath = Path.Combine(dir, newName);
            _cacheService.UpdateFilePath(SelectedInfo.CacheKey, newPath, newName);

            // Update list entry
            VideoList.SelectedFile.FilePath = newPath;
            VideoList.SelectedFile.FileName = newName;

            // Update info
            SelectedInfo = new VideoFileInfo
            {
                FilePath = newPath,
                FileName = newName,
                Extension = Path.GetExtension(newName).TrimStart('.').ToUpperInvariant(),
                FileSize = SelectedInfo.FileSize,
                LastModifiedUtc = SelectedInfo.LastModifiedUtc,
                CreationTimeUtc = SelectedInfo.CreationTimeUtc,
                CacheKey = SelectedInfo.CacheKey,
                Duration = SelectedInfo.Duration,
                FormatName = SelectedInfo.FormatName,
                Width = SelectedInfo.Width,
                Height = SelectedInfo.Height,
                BitRate = SelectedInfo.BitRate,
                Tracks = SelectedInfo.Tracks,
                ThumbnailPaths = SelectedInfo.ThumbnailPaths
            };
        }
    }

    [RelayCommand]
    private void CopyTo()
    {
        if (SelectedInfo == null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Copy to folder" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                _fileService.CopyFile(SelectedInfo.FilePath, dialog.FolderName);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Copy failed: {ex.Message}", "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private async Task MoveTo()
    {
        if (SelectedInfo == null || VideoList.SelectedFile == null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Move to folder" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await ReleaseFileAsync();
                var cacheKey = SelectedInfo.CacheKey;
                _fileService.MoveFile(SelectedInfo.FilePath, dialog.FolderName);
                _cacheService.Remove(cacheKey);
                var entry = VideoList.SelectedFile;
                VideoList.RemoveFile(entry);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Move failed: {ex.Message}", "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private async Task DeleteFile()
    {
        if (SelectedInfo == null || VideoList.SelectedFile == null) return;

        var result = System.Windows.MessageBox.Show(
            $"Delete \"{SelectedInfo.FileName}\"?\n\nThis cannot be undone.",
            "Delete file",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await ReleaseFileAsync();
            var cacheKey = SelectedInfo.CacheKey;
            _fileService.DeleteFile(SelectedInfo.FilePath);
            _cacheService.Remove(cacheKey);
            var entry = VideoList.SelectedFile;
            VideoList.RemoveFile(entry);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Delete failed: {ex.Message}", "Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Cancels all background work (thumbnails, ffmpeg) and stops VLC,
    /// then waits for processes to release file handles.
    /// </summary>
    private async Task ReleaseFileAsync()
    {
        _infoThumbCts?.Cancel();
        Player.StopAndWait();
        // Give VLC time to release file handles
        await Task.Delay(500);
    }

    [RelayCommand]
    private void EnterComparison()
    {
        var selected = VideoList.SelectedFiles.Where(f => !f.IsFolder).ToList();
        if (selected.Count != 2) return;

        IsComparing = true;
        Player.ShowControls = false;
        ComparePlayer.ShowControls = false;
        SyncOffsetMs = 0;

        Player.PlayFile(selected[0].FilePath);
        ComparePlayer.PlayFile(selected[1].FilePath);

        _syncTimer?.Start();
    }

    [RelayCommand]
    private void ExitComparison()
    {
        _syncTimer?.Stop();
        IsComparing = false;
        Player.ShowControls = true;

        ThreadPool.QueueUserWorkItem(_ => ComparePlayer.MediaPlayer.Stop());
    }

    [RelayCommand]
    private void CompareTogglePlayPause()
    {
        if (Player.MediaPlayer.IsPlaying)
        {
            Player.MediaPlayer.Pause();
            ComparePlayer.MediaPlayer.Pause();
        }
        else
        {
            Player.MediaPlayer.Play();
            ComparePlayer.MediaPlayer.Play();
            ApplySyncOffset();
        }
    }

    [RelayCommand]
    private void CompareStop()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Player.MediaPlayer.Stop();
            ComparePlayer.MediaPlayer.Stop();
        });
    }

    [RelayCommand]
    private void AdjustSyncOffset(string direction)
    {
        long step = SelectedSyncUnit switch
        {
            SyncUnit.Frames => 33,
            SyncUnit.Seconds => 1000,
            _ => 100
        };

        SyncOffsetMs += direction == "+" ? step : -step;
        ApplySyncOffset();
    }

    private void ApplySyncOffset()
    {
        if (!IsComparing) return;
        var primaryTime = Player.MediaPlayer.Time;
        if (primaryTime >= 0)
            ComparePlayer.MediaPlayer.Time = primaryTime + SyncOffsetMs;
    }

    private void CheckDrift()
    {
        if (!IsComparing || !Player.MediaPlayer.IsPlaying || !ComparePlayer.MediaPlayer.IsPlaying) return;
        var primaryTime = Player.MediaPlayer.Time;
        var expectedTime = primaryTime + SyncOffsetMs;
        var actualTime = ComparePlayer.MediaPlayer.Time;
        if (Math.Abs(actualTime - expectedTime) > 150)
            ComparePlayer.MediaPlayer.Time = expectedTime;
    }

    public void CompareSeekTo(float position)
    {
        Player.MediaPlayer.Position = position;
        // Apply offset after a brief delay to let VLC process the seek
        Task.Delay(50).ContinueWith(_ => ApplySyncOffset());
    }

    [RelayCommand]
    private void HandleKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (IsTextBoxFocused(e)) return;

        var ctrl = (e.KeyboardDevice.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        var shift = (e.KeyboardDevice.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape:
                if (IsComparing)
                {
                    ExitComparison();
                    e.Handled = true;
                }
                break;

            case System.Windows.Input.Key.Space:
                if (IsComparing)
                    CompareTogglePlayPause();
                else
                    Player.TogglePlayPauseCommand.Execute(null);
                e.Handled = true;
                break;

            case System.Windows.Input.Key.Left:
                if (shift)
                    SeekRelative(-300_000); // 5 min
                else if (ctrl)
                    SeekRelative(-30_000);  // 30 sec
                else
                    SeekRelative(-5_000);   // 5 sec
                e.Handled = true;
                break;

            case System.Windows.Input.Key.Right:
                if (shift)
                    SeekRelative(300_000);  // 5 min
                else if (ctrl)
                    SeekRelative(30_000);   // 30 sec
                else
                    SeekRelative(5_000);    // 5 sec
                e.Handled = true;
                break;

            case System.Windows.Input.Key.Delete:
                if (ctrl && !IsComparing)
                {
                    DeleteFileCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case System.Windows.Input.Key.Back:
                if (ctrl)
                {
                    Player.MediaPlayer.Position = 0.5f;
                    e.Handled = true;
                }
                break;

            case System.Windows.Input.Key.PageDown:
                VideoList.SelectAdjacentFile(1);
                e.Handled = true;
                break;

            case System.Windows.Input.Key.PageUp:
                VideoList.SelectAdjacentFile(-1);
                e.Handled = true;
                break;
        }
    }

    private void SeekRelative(long deltaMs)
    {
        var mp = Player.MediaPlayer;
        if (mp.Length <= 0) return;
        var newTime = Math.Clamp(mp.Time + deltaMs, 0, mp.Length);
        mp.Time = newTime;
    }

    private static bool IsTextBoxFocused(System.Windows.Input.KeyEventArgs e)
    {
        return e.OriginalSource is System.Windows.Controls.TextBox;
    }

    public void Dispose()
    {
        _infoThumbCts?.Cancel();
        _syncTimer?.Stop();
        _syncTimer?.Dispose();
        Player.Dispose();
        ComparePlayer.Dispose();
        _cacheService.Dispose();
    }
}
