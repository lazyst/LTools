using System.Collections.Generic;
using System.Windows;

namespace LTools.Views.Controls;

/// <summary>
/// 把「当前流程可用的变量名」挂到编辑窗口上，供 <see cref="VariableTextBox"/> 使用。
/// </summary>
/// <remarks>
/// 附加属性带 <see cref="FrameworkPropertyMetadataOptions.Inherits"/>，在窗口上设置一次就会
/// 沿逻辑树传给所有子元素，每个输入框不必逐个传参。编辑窗口的变量在构造时收集一次即可
/// （变量只在流程结构变化时增加，而结构变化时编辑窗口早已关闭）。
/// </remarks>
public static class VariableSource
{
    /// <summary>可插入的变量名（不带 <c>${}</c>）。</summary>
    public static readonly DependencyProperty NamesProperty = DependencyProperty.RegisterAttached(
        "Names", typeof(IReadOnlyList<string>), typeof(VariableSource),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetNames(DependencyObject element, IReadOnlyList<string>? names)
        => element.SetValue(NamesProperty, names);

    public static IReadOnlyList<string>? GetNames(DependencyObject element)
        => element.GetValue(NamesProperty) as IReadOnlyList<string>;

    /// <summary>取最近的变量列表；没有变量时返回 null（输入框据此隐藏变量按钮）。</summary>
    public static IReadOnlyList<string>? Resolve(DependencyObject element)
        => GetNames(element) is { Count: > 0 } names ? names : null;
}
