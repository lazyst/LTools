using System.Globalization;
using System.Windows.Data;

namespace CapsLockPro.Views.Controls;

/// <summary>
/// 值（宽度）≥ ConverterParameter 时返回 true。用于按容器宽度切换布局形态。
/// 组合动作步骤行：ListBox 够宽 → 单行（参数行内、名称拉长）；不够 → 两行（参数下沉，
/// 保证编辑/删除按钮常驻）。见 docs/ACTION_SYSTEM_PLAN.md §12。
/// </summary>
public sealed class WidthAtLeastConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double w
           && double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var min)
           && w >= min;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
