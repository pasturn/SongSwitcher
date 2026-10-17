using System.Globalization;
using System.Windows.Data;

namespace SongSwitcher;

/// <summary>把布尔值取反，供"刷新中禁用控件"这类场景绑定。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}