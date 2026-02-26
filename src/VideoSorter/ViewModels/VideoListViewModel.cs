using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSorter.Helpers;
using VideoSorter.Models;
using VideoSorter.Services;

namespace VideoSorter.ViewModels;

public partial class VideoFileEntry : ObservableObject
{
    public string FilePath { get; set; } = string.Empty;
    public string CacheKey { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime CreationTime { get; set; }
    public bool IsFolder { get; set; }

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private int _width;

    [ObservableProperty]
    private int _height;

    [ObservableProperty]
    private string _videoCodec = string.Empty;

    [ObservableProperty]
    private string _audioCodec = string.Empty;

    [ObservableProperty]
    private string _subtitleInfo = string.Empty;

    [ObservableProperty]
    private int _mode3D;

    public ObservableCollection<BitmapImage> Thumbnails { get; } = [];

    public void ApplyMetadata(Models.VideoFileInfo info)
    {
        Duration = info.Duration;
        Width = info.Width;
        Height = info.Height;
        VideoCodec = info.VideoCodecs;
        AudioCodec = info.AudioCodecs;
        SubtitleInfo = info.SubtitleLanguages;
        Mode3D = info.Mode3D;
    }
}

public enum SortOption
{
    NameAsc,
    NameDesc,
    SizeAsc,
    SizeDesc,
    DurationAsc,
    DurationDesc,
    CreationAsc,
    CreationDesc
}

public partial class VideoListViewModel : ObservableObject
{
    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoSorter", "debug.log");

    private static void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    private readonly FileOperationService _fileService;
    private readonly FfmpegService _ffmpegService;
    private readonly CacheService _cacheService;
    private readonly Action<VideoFileEntry?> _onSelectionChanged;
    private readonly SemaphoreSlim _thumbSemaphore = new(3, 3);
    private Action<string>? _navigateToFolder;
    private Func<int, int, int>? _spreadThumbCountFunc;
    private CancellationTokenSource? _loadCts;
    private int _defaultSpreadCount = 10;
    private string _currentPath = string.Empty;
    private List<VideoFileEntry> _allFiles = [];
    private int _processTotal;
    private int _processCompleted;

    /// <summary>
    /// Raised when background work status changes. Empty string = idle.
    /// </summary>
    public event Action<string>? StatusChanged;

    [ObservableProperty]
    private VideoFileEntry? _selectedFile;

    [ObservableProperty]
    private SortOption _selectedSort = SortOption.NameAsc;

    [ObservableProperty]
    private bool _isFilterVisible;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<VideoFileEntry> Files { get; } = [];
    public ObservableCollection<VideoFileEntry> SelectedFiles { get; } = [];

    public bool CanCompare => SelectedFiles.Count == 2 && SelectedFiles.All(f => !f.IsFolder);

    public static IReadOnlyList<SortOption> SortOptions { get; } = Enum.GetValues<SortOption>();

    /// <summary>
    /// Raised after LoadFolder populates Files, with the first video entry (or null if none).
    /// </summary>
    public event Action<VideoFileEntry?>? ScrollToFirstVideo;

    public VideoListViewModel(
        FileOperationService fileService,
        FfmpegService ffmpegService,
        CacheService cacheService,
        Action<VideoFileEntry?> onSelectionChanged)
    {
        _fileService = fileService;
        _ffmpegService = ffmpegService;
        _cacheService = cacheService;
        _onSelectionChanged = onSelectionChanged;
        SelectedFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanCompare));
    }

    /// <summary>
    /// Cancels any in-progress background work (thumbnail generation, metadata extraction).
    /// </summary>
    public void CancelBackgroundWork()
    {
        _loadCts?.Cancel();
    }

    private void ReportStatus(string status)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } d)
            d.BeginInvoke(() => StatusChanged?.Invoke(status));
    }

    public void SetFolderNavigator(Action<string> navigateToFolder)
    {
        _navigateToFolder = navigateToFolder;
    }

    /// <summary>
    /// Sets a function that computes the spread thumbnail count from (width, height).
    /// </summary>
    public void SetSpreadThumbCountFunc(Func<int, int, int> func)
    {
        _spreadThumbCountFunc = func;
    }

    partial void OnSelectedFileChanged(VideoFileEntry? value)
    {
        // Don't trigger video selection for folder entries
        if (value?.IsFolder == true)
            return;
        _onSelectionChanged(value);
    }

    partial void OnSelectedSortChanged(SortOption value)
    {
        if (!string.IsNullOrEmpty(_currentPath))
            LoadFolder(_currentPath);
    }

    partial void OnFilterTextChanged(string value)
    {
        ApplyFilter();
    }

    [RelayCommand]
    private void ToggleFilter()
    {
        IsFilterVisible = !IsFilterVisible;
        if (!IsFilterVisible)
        {
            FilterText = string.Empty;
        }
    }

    [RelayCommand]
    private void NavigateToSubfolder(VideoFileEntry entry)
    {
        if (!entry.IsFolder || _navigateToFolder == null) return;

        if (entry.FileName == "..")
        {
            var parent = Directory.GetParent(_currentPath);
            if (parent != null)
                _navigateToFolder(parent.FullName);
        }
        else
        {
            var subPath = Path.Combine(_currentPath, entry.FileName);
            if (Directory.Exists(subPath))
                _navigateToFolder(subPath);
        }
    }

    private void ApplyFilter()
    {
        var selected = SelectedFile;
        Files.Clear();

        var filtered = string.IsNullOrEmpty(FilterText)
            ? _allFiles
            : _allFiles.Where(f =>
                (f.IsFolder && f.FileName == "..") ||
                f.FileName.Contains(FilterText, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var entry in filtered)
            Files.Add(entry);

        if (selected != null && Files.Contains(selected))
            SelectedFile = selected;
        else
            SelectedFile = null;

        // Raise scroll event for first video
        var firstVideo = Files.FirstOrDefault(f => !f.IsFolder);
        ScrollToFirstVideo?.Invoke(firstVideo);
    }

    public async void LoadFolder(string path)
    {
        _currentPath = path;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _allFiles = [];
        Files.Clear();
        SelectedFile = null;
        IsLoading = true;

        var selectedSort = SelectedSort;

        ReportStatus("Scanning folder…");

        // Heavy work on background thread (file I/O, cache lookups — no BitmapImage here)
        var (allEntries, cachedThumbs) = await Task.Run(() =>
        {
            // Build folder entries
            var folderEntries = new List<VideoFileEntry>();

            var parentDir = Directory.GetParent(path);
            if (parentDir != null)
            {
                folderEntries.Add(new VideoFileEntry
                {
                    IsFolder = true,
                    FileName = "..",
                    FilePath = parentDir.FullName
                });
            }

            var subfolderNames = _fileService.EnumerateSubfolders(path);
            var subEntries = subfolderNames.Select(name => new VideoFileEntry
            {
                IsFolder = true,
                FileName = name,
                FilePath = Path.Combine(path, name)
            }).ToList();

            subEntries = selectedSort switch
            {
                SortOption.NameDesc => subEntries.OrderByDescending(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => subEntries.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList()
            };

            folderEntries.AddRange(subEntries);

            // Enumerate and build video entries
            ReportStatus("Reading file info…");
            var filePaths = _fileService.EnumerateVideoFiles(path);
            var videoEntries = new List<VideoFileEntry>();
            var thumbPaths = new List<(VideoFileEntry Entry, List<string> Paths)>();
            foreach (var filePath in filePaths)
            {
                ct.ThrowIfCancellationRequested();
                var fi = new FileInfo(filePath);
                var cacheKey = HashHelper.ComputeCacheKey(filePath, fi.LastWriteTimeUtc, fi.Length);
                var cached = _cacheService.Get(cacheKey);

                var entry = new VideoFileEntry
                {
                    FilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    CacheKey = cacheKey,
                    FileSize = cached?.FileSize ?? fi.Length,
                    CreationTime = cached?.CreationTimeUtc > DateTime.MinValue
                        ? cached.CreationTimeUtc.ToLocalTime()
                        : fi.CreationTime
                };

                if (cached != null)
                {
                    entry.ApplyMetadata(cached);
                    if (cached.ThumbnailPaths.Count > 0)
                        thumbPaths.Add((entry, cached.ThumbnailPaths.ToList()));
                }

                videoEntries.Add(entry);
            }

            IEnumerable<VideoFileEntry> sorted = selectedSort switch
            {
                SortOption.NameAsc => videoEntries.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase),
                SortOption.NameDesc => videoEntries.OrderByDescending(e => e.FileName, StringComparer.OrdinalIgnoreCase),
                SortOption.SizeAsc => videoEntries.OrderBy(e => e.FileSize),
                SortOption.SizeDesc => videoEntries.OrderByDescending(e => e.FileSize),
                SortOption.DurationAsc => videoEntries.OrderBy(e => e.Duration),
                SortOption.DurationDesc => videoEntries.OrderByDescending(e => e.Duration),
                SortOption.CreationAsc => videoEntries.OrderBy(e => e.CreationTime),
                SortOption.CreationDesc => videoEntries.OrderByDescending(e => e.CreationTime),
                _ => videoEntries
            };

            return (new List<VideoFileEntry>([..folderEntries, ..sorted]), thumbPaths);
        }, ct);

        if (ct.IsCancellationRequested) return;

        _allFiles = allEntries;
        ApplyFilter();
        IsLoading = false;
        Log($"LoadFolder: scan done, {allEntries.Count} entries, {cachedThumbs.Count} with cached thumbs");
        ReportStatus("Loading thumbnails…");

        // Load cached thumbnails on UI thread in batches to stay responsive
        foreach (var (entry, paths) in cachedThumbs)
        {
            if (ct.IsCancellationRequested) break;
            foreach (var tp in paths)
            {
                var bmp = LoadBitmapImage(tp);
                if (bmp != null) entry.Thumbnails.Add(bmp);
            }
        }

        // Background: generate metadata + both thumbnail strips for all video files
        // (GenerateAllThumbnailsAsync skips files already on disk, so cached entries are fast)
        var needProcess = Files.Where(f => !f.IsFolder).ToList();
        _processTotal = needProcess.Count;
        _processCompleted = 0;
        Log($"LoadFolder: needProcess={needProcess.Count} video files");

        if (_processTotal > 0)
            ReportStatus($"Processing 0/{_processTotal}");

        var tasks = needProcess.Select(f => GenerateThumbnailAsync(f, ct));
        await Task.WhenAll(tasks);

        ReportStatus("");
    }

    private async Task GenerateThumbnailAsync(VideoFileEntry entry, CancellationToken ct)
    {
        try
        {
            await _thumbSemaphore.WaitAsync(ct);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            Log($"GenThumb START: {entry.FileName} (cached thumbs={entry.Thumbnails.Count}, W={entry.Width}, H={entry.Height})");

            // Ensure metadata is cached (needed for duration)
            var cached = _cacheService.Get(entry.CacheKey);
            TimeSpan duration;
            if (cached != null)
            {
                duration = cached.Duration;
                System.Windows.Application.Current.Dispatcher.Invoke(() => entry.ApplyMetadata(cached));
                Log($"GenThumb META from cache: {entry.FileName} dur={duration} W={entry.Width} H={entry.Height}");
            }
            else
            {
                Log($"GenThumb META extracting: {entry.FileName}");
                var info = await _ffmpegService.ExtractMetadataAsync(entry.FilePath, entry.CacheKey, ct);
                if (info == null) { Log($"GenThumb META failed: {entry.FileName}"); return; }
                _cacheService.Save(info);
                duration = info.Duration;
                System.Windows.Application.Current.Dispatcher.Invoke(() => entry.ApplyMetadata(info));
                Log($"GenThumb META extracted: {entry.FileName} dur={duration} W={entry.Width} H={entry.Height}");
            }

            if (duration.TotalSeconds < 1) { Log($"GenThumb SKIP short: {entry.FileName}"); return; }

            var thumbDir = _cacheService.GetThumbnailDirectory(entry.CacheKey);
            var spreadCount = _spreadThumbCountFunc != null
                ? _spreadThumbCountFunc(entry.Width, entry.Height)
                : _defaultSpreadCount;
            if (spreadCount < 0) spreadCount = 0;

            Log($"GenThumb GENERATE: {entry.FileName} spreadCount={spreadCount} thumbDir={thumbDir}");

            var (listPaths, spreadPaths) = await _ffmpegService.GenerateAllThumbnailsAsync(
                entry.FilePath, duration, thumbDir, spreadCount, entry.Mode3D, ct);

            Log($"GenThumb RESULT: {entry.FileName} listPaths={listPaths.Count} spreadPaths={spreadPaths.Count} existingThumbs={entry.Thumbnails.Count}");

            if (listPaths.Count > 0 && entry.Thumbnails.Count == 0)
            {
                _cacheService.UpdateThumbnailPaths(entry.CacheKey, listPaths);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var p in listPaths)
                    {
                        var bmp = LoadBitmapImage(p);
                        if (bmp != null) entry.Thumbnails.Add(bmp);
                    }
                });
            }

            Log($"GenThumb DONE: {entry.FileName}");
        }
        catch (OperationCanceledException) { Log($"GenThumb CANCELLED: {entry.FileName}"); }
        catch (Exception ex) { Log($"GenThumb ERROR: {entry.FileName} {ex.Message}"); }
        finally
        {
            _thumbSemaphore.Release();
            var completed = Interlocked.Increment(ref _processCompleted);
            ReportStatus(completed >= _processTotal ? "" : $"Processing {completed}/{_processTotal}");
        }
    }

    private static BitmapImage? LoadBitmapImage(string path)
    {
        if (!File.Exists(path)) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.DecodePixelWidth = 120;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public async Task RegenerateThumbnailsAsync(VideoFileEntry entry)
    {
        var ct = _loadCts?.Token ?? CancellationToken.None;
        var cached = _cacheService.Get(entry.CacheKey);
        if (cached == null || cached.Duration.TotalSeconds < 1) return;

        var thumbDir = _cacheService.GetThumbnailDirectory(entry.CacheKey);
        var spreadCount = _spreadThumbCountFunc != null
            ? _spreadThumbCountFunc(entry.Width, entry.Height)
            : _defaultSpreadCount;
        if (spreadCount < 0) spreadCount = 0;

        var (listPaths, _) = await _ffmpegService.GenerateAllThumbnailsAsync(
            entry.FilePath, cached.Duration, thumbDir, spreadCount, entry.Mode3D, ct);

        if (listPaths.Count > 0)
        {
            _cacheService.UpdateThumbnailPaths(entry.CacheKey, listPaths);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                entry.Thumbnails.Clear();
                foreach (var p in listPaths)
                {
                    var bmp = LoadBitmapImage(p);
                    if (bmp != null) entry.Thumbnails.Add(bmp);
                }
            });
        }
    }

    public void RemoveFile(VideoFileEntry entry)
    {
        var idx = Files.IndexOf(entry);
        _allFiles.Remove(entry);
        Files.Remove(entry);
        if (SelectedFile == entry)
            SelectedFile = null;

        // Auto-select next video file
        if (idx >= 0)
        {
            // Search forward from the removed index
            for (int i = idx; i < Files.Count; i++)
            {
                if (!Files[i].IsFolder) { SelectedFile = Files[i]; return; }
            }
            // Search backward
            for (int i = idx - 1; i >= 0; i--)
            {
                if (!Files[i].IsFolder) { SelectedFile = Files[i]; return; }
            }
        }
    }

    public void SelectAdjacentFile(int direction)
    {
        if (Files.Count == 0) return;
        var current = SelectedFile;
        var idx = current != null ? Files.IndexOf(current) : -1;

        // Search in the given direction for the next non-folder entry
        int start = direction > 0 ? idx + 1 : idx - 1;
        for (int i = start; i >= 0 && i < Files.Count; i += direction)
        {
            if (!Files[i].IsFolder) { SelectedFile = Files[i]; return; }
        }
    }
}
