using System.Globalization;
using System.Windows.Data;

namespace VideoSorter.Converters;

public sealed class FileSizeConverter : IValueConverter
{
    private static readonly string[] Suffixes = ["B", "KB", "MB", "GB", "TB"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes || bytes < 0) return "0 B";

        if (bytes == 0) return "0 B";

        int order = (int)Math.Log(bytes, 1024);
        order = Math.Min(order, Suffixes.Length - 1);
        double adjusted = bytes / Math.Pow(1024, order);
        return $"{adjusted:0.##} {Suffixes[order]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
