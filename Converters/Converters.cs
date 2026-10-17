using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SongSwitcher.Converters;

/// <summary>把字符串与当前播放路径比较，相等返回 true（用于卡片高亮）。</summary>
public class EqualsToCurrentConverter : IValueConverter
{
    public string? CurrentPath { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(CurrentPath)) return false;
        return string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}