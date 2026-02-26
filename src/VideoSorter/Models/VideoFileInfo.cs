namespace VideoSorter.Models;

public sealed class VideoFileInfo
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public DateTime LastModifiedUtc { get; init; }
    public DateTime CreationTimeUtc { get; init; }
    public string CacheKey { get; init; } = string.Empty;

    // Metadata from ffprobe
    public TimeSpan Duration { get; init; }
    public string FormatName { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public long BitRate { get; init; }

    // Tracks
    public List<VideoTrackInfo> Tracks { get; init; } = [];

    // Thumbnail paths (up to 4)
    public List<string> ThumbnailPaths { get; init; } = [];

    // 3D mode: 0=Off, 1=SideBySide, 2=TopBottom
    public int Mode3D { get; init; }

    public IEnumerable<VideoTrackInfo> VideoTracks => Tracks.Where(t => t.Type == "video");
    public IEnumerable<VideoTrackInfo> AudioTracks => Tracks.Where(t => t.Type == "audio");
    public IEnumerable<VideoTrackInfo> SubtitleTracks => Tracks.Where(t => t.Type == "subtitle");

    public string VideoCodecs => string.Join(", ", VideoTracks.Select(t => t.Codec));
    public string AudioCodecs => string.Join(", ", AudioTracks.Select(t => t.Codec));
    public string SubtitleLanguages => string.Join(", ", SubtitleTracks.Select(t =>
        !string.IsNullOrEmpty(t.Title) ? t.Title :
        !string.IsNullOrEmpty(t.Language) ? t.Language :
        $"Track {t.Id}"));
}
