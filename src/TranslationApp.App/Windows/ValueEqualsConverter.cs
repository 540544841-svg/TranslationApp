using System.Globalization;
using System.Windows.Data;

namespace TranslationApp.Windows;

/// <summary>
/// 值比较转换器：把「当前值」与 ConverterParameter 比出 bool，供 RadioButton 组成分段控件
/// （设计稿 .seg 三态：跟随系统 / 纸 / 墨）。
/// ConvertBack 只在被选中时回传参数，取消选中回 <see cref="Binding.DoNothing"/>，
/// 避免同一组里两枚按钮互相把值写成空。
/// </summary>
public sealed class ValueEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter ?? Binding.DoNothing : Binding.DoNothing;
}
