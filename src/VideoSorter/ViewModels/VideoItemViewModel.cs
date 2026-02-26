using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSorter.Models;
using VideoSorter.Services;

namespace VideoSorter.ViewModels;

public partial class VideoItemViewModel : ObservableObject
{
    private readonly FileOperationService _fileService;
    private readonly CacheService _cacheService;
    private readonly Action<VideoItemViewModel> _onPlayRequested;
    private readonly Action<VideoItemViewModel> _onRemoveFromList;

    [ObservableProperty]
    private VideoFileInfo _info;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    private bool _isLoading = true;

    public ObservableCollection<BitmapImage> Thumbnails { get; } = [];

    public string FileName => Info.FileName;
    public string Extension => Info.Extension;
    public long FileSize => Info.FileSize;
    public TimeSpan Duration => Info.Duration;
    public int Width => Info.Width;
    public int Height => Info.Height;

    public string VideoCodecs => string.Join(", ", Info.VideoTracks.Select(t => t.Codec));
    public string AudioCodecs => string.Join(", ", Info.AudioTracks.Select(t => t.Codec));
    public string SubtitleLanguages => string.Join(", ", Info.SubtitleTracks.Select(t =>
        !string.IsNullOrEmpty(t.Title) ? t.Title :
        !string.IsNullOrEmpty(t.Language) ? t.Language :
        $"Track {t.Id}"));

    public bool HasSubtitles => Info.SubtitleTracks.Any();
    public bool HasMultipleAudio => Info.AudioTracks.Count() > 1;

    public VideoItemViewModel(
        VideoFileInfo info,
        FileOperationService fileService,
        CacheService cacheService,
        Action<VideoItemViewModel> onPlayRequested,
        Action<VideoItemViewModel> onRemoveFromList)
    {
        _info = info;
        _fileService = fileService;
        _cacheService = cacheService;
        _onPlayRequested = onPlayRequested;
        _onRemoveFromList = onRemoveFromList;
    }

    public void LoadThumbnails()
    {
        Thumbnails.Clear();
        foreach (var path in Info.ThumbnailPaths)
        {
            if (!File.Exists(path)) continue;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 320;
            bmp.EndInit();
            bmp.Freeze();
            Thumbnails.Add(bmp);
        }
        IsLoading = false;
    }

    public void UpdateInfo(VideoFileInfo newInfo)
    {
        Info = newInfo;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Extension));
        OnPropertyChanged(nameof(FileSize));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(VideoCodecs));
        OnPropertyChanged(nameof(AudioCodecs));
        OnPropertyChanged(nameof(SubtitleLanguages));
        OnPropertyChanged(nameof(HasSubtitles));
        OnPropertyChanged(nameof(HasMultipleAudio));
        LoadThumbnails();
    }

    [RelayCommand]
    private void Play() => _onPlayRequested(this);

    [RelayCommand]
    private void StartRename()
    {
        RenameText = Path.GetFileNameWithoutExtension(Info.FileName);
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (!IsRenaming) return;
        IsRenaming = false;

        var newName = RenameText.Trim();
        if (string.IsNullOrEmpty(newName)) return;

        var ext = Path.GetExtension(Info.FileName);
        var newFileName = newName + ext;
        if (newFileName == Info.FileName) return;

        if (_fileService.RenameFile(Info.FilePath, newFileName))
        {
            var dir = Path.GetDirectoryName(Info.FilePath)!;
            var newPath = Path.Combine(dir, newFileName);
            _cacheService.UpdateFilePath(Info.CacheKey, newPath, newFileName);

            Info = new VideoFileInfo
            {
                FilePath = newPath,
                FileName = newFileName,
                Extension = Info.Extension,
                FileSize = Info.FileSize,
                LastModifiedUtc = Info.LastModifiedUtc,
                CacheKey = Info.CacheKey,
                Duration = Info.Duration,
                FormatName = Info.FormatName,
                Width = Info.Width,
                Height = Info.Height,
                BitRate = Info.BitRate,
                Tracks = Info.Tracks,
                ThumbnailPaths = Info.ThumbnailPaths
            };
            OnPropertyChanged(nameof(FileName));
        }
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private void CopyTo()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Copy to folder" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                _fileService.CopyFile(Info.FilePath, dialog.FolderName);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Copy failed: {ex.Message}", "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void MoveTo()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Move to folder" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                _fileService.MoveFile(Info.FilePath, dialog.FolderName);
                _onRemoveFromList(this);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Move failed: {ex.Message}", "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }
}
