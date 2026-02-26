namespace VideoSorter.Helpers;

public static class VideoFileExtensions
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
        ".mpg", ".mpeg", ".3gp", ".ogv", ".ts", ".mts", ".m2ts", ".vob",
        ".divx", ".xvid", ".asf", ".rm", ".rmvb", ".f4v"
    };

    public static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && Extensions.Contains(ext);
    }

    public static IReadOnlySet<string> All => Extensions;
}
