# Video Sorter

C# WPF desktop application for browsing, previewing, and managing video files.

## Tech Stack

- **Framework**: .NET 9.0 (`net9.0-windows`), WPF
- **Pattern**: MVVM using CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`)
- **Video playback**: LibVLCSharp.WPF (LibVLC initialized in `App.xaml.cs`)
- **Metadata extraction**: ffprobe/ffmpeg via `System.Diagnostics.Process`
- **Caching**: SQLite via Microsoft.Data.Sqlite at `%LOCALAPPDATA%\VideoSorter\cache.db`
- **Thumbnails**: Stored at `%LOCALAPPDATA%\VideoSorter\thumbnails\{cacheKey}\`

## Solution Structure

```
VideoSorter.sln
src/VideoSorter/
  App.xaml(.cs)           - LibVLC initialization, app lifecycle
  MainWindow.xaml(.cs)    - Shell window, global key handling
  GlobalUsings.cs         - Adds System.IO (not included by ImplicitUsings for WPF)
  Models/
    VideoFileInfo.cs      - Video metadata model (file info, resolution, tracks, thumbnails)
    VideoTrackInfo.cs     - Individual track info (video/audio/subtitle)
  ViewModels/
    MainViewModel.cs      - Root VM: coordinates folder/list/player, file operations (rename/copy/move)
    FolderBarViewModel.cs - Folder navigation
    VideoListViewModel.cs - File listing, selection
    VideoItemViewModel.cs - Per-item VM for list entries
    VideoPlayerViewModel.cs - LibVLC playback control
  Views/
    FolderBarView.xaml    - Folder bar UI
    VideoListView.xaml    - Video list UI
    VideoItemView.xaml    - Individual list item template
    VideoPlayerView.xaml  - Video player with overlay controls
  Services/
    FfmpegService.cs      - Runs ffprobe (metadata) and ffmpeg (thumbnails) as processes
    CacheService.cs       - SQLite read/write/update for cached metadata
    FileOperationService.cs - File rename/copy/move operations
  Converters/
    FileSizeConverter.cs, BoolToVisibilityConverter.cs,
    TimeSpanToStringConverter.cs, CountToVisibilityConverter.cs
  Helpers/
    VideoFileExtensions.cs - Supported video extensions set
    HashHelper.cs          - SHA256 cache key from path|lastModified|size
```

## Build & Run

```bash
dotnet build src/VideoSorter/VideoSorter.csproj
dotnet run --project src/VideoSorter/VideoSorter.csproj
```

Requires ffmpeg/ffprobe on PATH for metadata and thumbnail generation.

## Key Patterns

- Cache key = SHA256 of `"{path}|{lastModifiedUTC}|{size}"` — invalidates automatically when file changes
- `VideoFileInfo` uses `init` properties — create new instances rather than mutating
- VLC `MediaPlayer.Stop()` must NOT be called from VLC event threads — always use `ThreadPool.QueueUserWorkItem`
- VideoView overlay needs `Background="#01000000"` (near-transparent) for mouse events (WPF airspace issue)
- ffmpeg process output: read both stdout and stderr concurrently to avoid pipe buffer deadlock

## WPF Gotchas

- `ImplicitUsings` in WPF projects does NOT include `System.IO` — added via `GlobalUsings.cs`
- `x:Static` in XAML cannot reference the declaring UserControl's own class at compile time — use resource-based converters instead
