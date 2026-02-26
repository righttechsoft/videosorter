namespace VideoSorter.Models;

public sealed class VideoTrackInfo
{
    public int Id { get; init; }
    public string Type { get; init; } = string.Empty; // "video", "audio", "subtitle"
    public string Codec { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    // Video-specific
    public int Width { get; init; }
    public int Height { get; init; }

    // Audio-specific
    public int Channels { get; init; }
    public int SampleRate { get; init; }

    public string DisplayName
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Title)) parts.Add(Title);
            if (!string.IsNullOrEmpty(Language)) parts.Add(Language);
            if (!string.IsNullOrEmpty(Codec)) parts.Add(Codec);
            if (Type == "video" && Width > 0) parts.Add($"{Width}x{Height}");
            if (Type == "audio" && Channels > 0) parts.Add($"{Channels}ch");
            return parts.Count > 0 ? string.Join(" | ", parts) : $"Track {Id}";
        }
    }
}
