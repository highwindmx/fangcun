using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Fangcun
{
    // "#RRGGBB" / "#AARRGGBB" -> SolidColorBrush
    public class StringToBrush : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value?.ToString() ?? "#FFFFFF")); }
            catch { return new SolidColorBrush(Colors.White); }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => ((SolidColorBrush)value).Color.ToString();
    }

    // "Left/Center/Right" -> HorizontalAlignment
    public class StringToHAlign : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value?.ToString() switch
            {
                "Center" => HorizontalAlignment.Center,
                "Right" => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left
            };
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => ((HorizontalAlignment)value!) switch
            {
                HorizontalAlignment.Center => "Center",
                HorizontalAlignment.Right => "Right",
                _ => "Left"
            };
    }

    // bool -> Visibility（param="invert" 时取反）
    public class BoolToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool bb && bb;
            if (parameter?.ToString() == "invert") b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Visible;
    }

    // 字符串 == param -> Visible，否则 Collapsed（用于图标/列表布局切换）
    public class EqualToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null!;
    }

    // (显示名称? , 是否正在重命名?) -> 名称文本可见：showName && !renaming
    public class ShowNameVisibility : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool show = values.Length > 0 && values[0] is bool s && s;
            bool renaming = values.Length > 1 && values[1] is bool r && r;
            return (show && !renaming) ? Visibility.Visible : Visibility.Collapsed;
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => new object[] { false, false };
    }

    // (IsEllipsis, ItemLayout) -> 图标布局面板可见：!ellipsis && layout!=List
    public class IconPanelVisibility : IMultiValueConverter
    {
        public object Convert(object[] v, Type t, object p, CultureInfo c)
        {
            bool ellipsis = v.Length > 0 && v[0] is bool e && e;
            string? layout = v.Length > 1 ? v[1] as string : null;
            return (!ellipsis && layout != "List") ? Visibility.Visible : Visibility.Collapsed;
        }
        public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => new object[] { false, "" };
    }

    // (IsEllipsis, ItemLayout) -> 列表布局面板可见：!ellipsis && layout==List
    public class ListPanelVisibility : IMultiValueConverter
    {
        public object Convert(object[] v, Type t, object p, CultureInfo c)
        {
            bool ellipsis = v.Length > 0 && v[0] is bool e && e;
            string? layout = v.Length > 1 ? v[1] as string : null;
            return (!ellipsis && layout == "List") ? Visibility.Visible : Visibility.Collapsed;
        }
        public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => new object[] { false, "" };
    }
}
