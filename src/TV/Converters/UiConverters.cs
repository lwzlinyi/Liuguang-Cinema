using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace TV.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is true;
        if (parameter is "Inverse") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is Visibility.Visible;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text));
        if (parameter is "Inverse") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class BoolToFavoriteConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? "已收藏" : "收藏";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class IntroMaxLinesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? 0 : 5;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class IntroToggleTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? "收起 ▲" : "查看全部 ▼";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>分类胶囊配色：parameter = bg / fg / border。</summary>
public sealed class PillBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var active = value is true;
        var key = (parameter as string) switch
        {
            "fg" => active ? "PillActiveInkBrush" : "PrimaryTextBrush",
            "border" => active ? "AccentBrush" : "SubtleBorderBrush",
            _ => active ? "AccentBrush" : "PillIdleBrush"
        };
        return Application.Current.Resources[key];
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>
/// 影视仓式来源角标：按来源名稳定映射到一组鲜艳底色，返回 SolidColorBrush。
/// 同一站点每次显示颜色一致；用于搜索结果/首页海报左上角的来源胶囊。
/// </summary>
public sealed class SourceTagBrushConverter : IValueConverter
{
    private static readonly uint[] Palette =
    {
        0xFF0EA5E9, // 天蓝
        0xFFF59E0B, // 橙黄
        0xFF22C55E, // 绿
        0xFF8B5CF6, // 紫
        0xFFEC4899, // 粉
        0xFF06B6D4, // 青
        0xFFEF4444, // 红
        0xFF3B82F6, // 蓝
    };

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var text = value as string ?? value?.ToString() ?? string.Empty;
        unchecked
        {
            var hash = 0;
            foreach (var ch in text) hash = hash * 31 + ch;
            var argb = Palette[(uint)hash % (uint)Palette.Length];
            var color = Windows.UI.Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        }
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
