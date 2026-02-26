using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

namespace VideoSorter.ViewModels;

public enum Video3DMode
{
    Off,
    SideBySide,
    TopBottom
}

public partial class VideoPlayerViewModel : ObservableObject, IDisposable
{
    private readonly LibVLC _libVlc;
    private int _playGeneration; // incremented each PlayFile, checked on UI thread
    private string? _currentFilePath;
    private System.Timers.Timer? _updateTimer;
    private bool _suppressAutoReplay;

    public MediaPlayer MediaPlayer { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _position; // 0..1

    [ObservableProperty]
    private TimeSpan _currentTime;

    [ObservableProperty]
    private TimeSpan _totalTime;

    [ObservableProperty]
    private int _volume = 0;

    [ObservableProperty]
    private int _selectedAudioTrackIndex = -1;

    [ObservableProperty]
    private int _selectedSubtitleTrackIndex = -1;

    [ObservableProperty]
    private Video3DMode _mode3D = Video3DMode.Off;

    [ObservableProperty]
    private bool _showControls = true;

    public string Mode3DLabel => Mode3D switch
    {
        Video3DMode.Off => "3D",
        Video3DMode.SideBySide => "SBS",
        Video3DMode.TopBottom => "T/B",
        _ => "3D"
    };

    public ObservableCollection<TrackItem> AudioTracks { get; } = [];
    public ObservableCollection<TrackItem> SubtitleTracks { get; } = [];

    public VideoPlayerViewModel(LibVLC libVlc)
    {
        _libVlc = libVlc;
        MediaPlayer = new MediaPlayer(_libVlc);
        MediaPlayer.Volume = 0;

        MediaPlayer.Playing += (_, _) => OnDispatcher(() =>
        {
            IsPlaying = true;
            _ = PopulateTracksAfterDelay();
        });
        MediaPlayer.Paused += (_, _) => OnDispatcher(() => IsPlaying = false);
        MediaPlayer.Stopped += (_, _) => OnDispatcher(() =>
        {
            IsPlaying = false;
            Position = 0;
            CurrentTime = TimeSpan.Zero;
        });
        MediaPlayer.EndReached += (_, _) => OnDispatcher(() =>
        {
            IsPlaying = false;
        });

        _updateTimer = new System.Timers.Timer(250);
        _updateTimer.Elapsed += (_, _) => UpdatePosition();
        _updateTimer.Start();
    }

    /// <summary>
    /// Sets Mode3D without triggering auto-replay of the current file.
    /// </summary>
    public void SetMode3DSilently(Video3DMode mode)
    {
        _suppressAutoReplay = true;
        Mode3D = mode;
        _suppressAutoReplay = false;
    }

    public void PlayFile(string filePath)
    {
        _currentFilePath = filePath;
        PlayFileInternal(filePath);
    }

    private void PlayFileInternal(string filePath)
    {
        // Increment generation so any stale timer callbacks are discarded
        Interlocked.Increment(ref _playGeneration);

        Title = Path.GetFileName(filePath);
        Position = 0;
        CurrentTime = TimeSpan.Zero;
        TotalTime = TimeSpan.Zero;
        AudioTracks.Clear();
        SubtitleTracks.Clear();

        // Clear any previous crop before starting new playback
        MediaPlayer.CropGeometry = string.Empty;
        MediaPlayer.AspectRatio = null;

        using var media = new Media(_libVlc, filePath, FromType.FromPath);
        MediaPlayer.Play(media);

        // Apply crop after VLC initializes the video output (retries until dimensions available)
        _ = ApplyCropWithRetry();
    }

    private async Task ApplyCropWithRetry()
    {
        var gen = Volatile.Read(ref _playGeneration);

        if (Mode3D == Video3DMode.Off)
        {
            OnDispatcher(() =>
            {
                MediaPlayer.CropGeometry = string.Empty;
                MediaPlayer.AspectRatio = null;
            });
            return;
        }

        // Retry: video dimensions may not be available immediately after Play()
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (gen != Volatile.Read(ref _playGeneration)) return;

            uint width = 0, height = 0;
            if (MediaPlayer.Size(0, ref width, ref height) && width > 0 && height > 0)
            {
                OnDispatcher(() =>
                {
                    if (gen != Volatile.Read(ref _playGeneration)) return;
                    switch (Mode3D)
                    {
                        case Video3DMode.SideBySide:
                            MediaPlayer.CropGeometry = $"{width / 2}x{height}+0+0";
                            MediaPlayer.AspectRatio = $"{width}:{height}";
                            break;
                        case Video3DMode.TopBottom:
                            MediaPlayer.CropGeometry = $"{width}x{height / 2}+0+0";
                            MediaPlayer.AspectRatio = $"{width}:{height}";
                            break;
                    }
                });
                return;
            }

            await Task.Delay(300);
        }
    }

    [RelayCommand]
    private void Cycle3DMode()
    {
        Mode3D = Mode3D switch
        {
            Video3DMode.Off => Video3DMode.SideBySide,
            Video3DMode.SideBySide => Video3DMode.TopBottom,
            Video3DMode.TopBottom => Video3DMode.Off,
            _ => Video3DMode.Off
        };
    }

    partial void OnMode3DChanged(Video3DMode value)
    {
        OnPropertyChanged(nameof(Mode3DLabel));

        // Apply crop in-place without restarting playback
        if (!_suppressAutoReplay && _currentFilePath != null && (IsPlaying || MediaPlayer.Length > 0))
        {
            _ = ApplyCropWithRetry();
        }
    }

    private async Task PopulateTracksAfterDelay()
    {
        await Task.Delay(1000);
        OnDispatcher(() =>
        {
            AudioTracks.Clear();
            SubtitleTracks.Clear();

            var audioDescs = MediaPlayer.AudioTrackDescription;
            if (audioDescs != null)
            {
                int currentIdx = 0;
                foreach (var desc in audioDescs)
                {
                    AudioTracks.Add(new TrackItem(desc.Id, desc.Name ?? $"Track {desc.Id}"));
                    if (desc.Id == MediaPlayer.AudioTrack)
                        SelectedAudioTrackIndex = currentIdx;
                    currentIdx++;
                }
            }

            var spuDescs = MediaPlayer.SpuDescription;
            if (spuDescs != null)
            {
                int currentIdx = 0;
                SubtitleTracks.Add(new TrackItem(-1, "Disable"));
                currentIdx++;
                foreach (var desc in spuDescs)
                {
                    if (desc.Id == -1) continue;
                    SubtitleTracks.Add(new TrackItem(desc.Id, desc.Name ?? $"Track {desc.Id}"));
                    if (desc.Id == MediaPlayer.Spu)
                        SelectedSubtitleTrackIndex = currentIdx;
                    currentIdx++;
                }
                if (SelectedSubtitleTrackIndex < 0)
                    SelectedSubtitleTrackIndex = 0;
            }
        });
    }

    private void UpdatePosition()
    {
        try
        {
            var pos = MediaPlayer.Position;
            var length = MediaPlayer.Length;
            if (length <= 0) return;

            OnDispatcher(() =>
            {
                Position = pos;
                CurrentTime = TimeSpan.FromMilliseconds(pos * length);
                TotalTime = TimeSpan.FromMilliseconds(length);
            });
        }
        catch { }
    }

    partial void OnVolumeChanged(int value) => MediaPlayer.Volume = value;

    partial void OnSelectedAudioTrackIndexChanged(int value)
    {
        if (value >= 0 && value < AudioTracks.Count)
            MediaPlayer.SetAudioTrack(AudioTracks[value].Id);
    }

    partial void OnSelectedSubtitleTrackIndexChanged(int value)
    {
        if (value >= 0 && value < SubtitleTracks.Count)
            MediaPlayer.SetSpu(SubtitleTracks[value].Id);
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (MediaPlayer.IsPlaying)
            MediaPlayer.Pause();
        else
            MediaPlayer.Play();
    }

    [RelayCommand]
    private void Stop()
    {
        ThreadPool.QueueUserWorkItem(_ => MediaPlayer.Stop());
    }

    /// <summary>
    /// Stops playback and blocks until VLC has fully released the file.
    /// Must NOT be called from a VLC event thread.
    /// </summary>
    public void StopAndWait()
    {
        using var done = new ManualResetEventSlim(false);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            MediaPlayer.Stop();
            done.Set();
        });
        done.Wait(TimeSpan.FromSeconds(5));
    }

    private static void OnDispatcher(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _updateTimer?.Stop();
        _updateTimer?.Dispose();
        _updateTimer = null;
        MediaPlayer.Stop();
        MediaPlayer.Dispose();
    }
}

public record TrackItem(int Id, string Name)
{
    public override string ToString() => Name;
}
