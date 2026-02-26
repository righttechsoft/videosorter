using System.Globalization;
using System.Windows.Data;
using VideoSorter.ViewModels;

namespace VideoSorter.Converters;

public sealed class SortOptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is SortOption opt ? opt switch
        {
            SortOption.NameAsc => "Name (A\u2026Z)",
            SortOption.NameDesc => "Name (Z\u2026A)",
            SortOption.SizeAsc => "Size (Smallest)",
            SortOption.SizeDesc => "Size (Largest)",
            SortOption.DurationAsc => "Duration (Shortest)",
            SortOption.DurationDesc => "Duration (Longest)",
            SortOption.CreationAsc => "Created (Oldest)",
            SortOption.CreationDesc => "Created (Newest)",
            _ => value.ToString()!
        } : value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
