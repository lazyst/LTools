using System.Windows;

namespace LTools.Views.Controls;

/// <summary>
/// 全局输入框的占位文字（见 <c>CONTEXT.md</c>「占位文字」）。
/// 只提供两个附加属性，显隐画在 <c>Themes/Modern.xaml</c> 两份 TextBox 模板的
/// <c>PART_Placeholder</c> 上——别在业务 XAML 里再叠一层 TextBlock。
/// </summary>
/// <remarks>
/// <c>Attach</c> 默认 true；<c>ComboBox</c> 的 <c>PART_EditableTextBox</c> 必须设 False
/// （右侧 26px 已留给下拉箭头，清空走「选预设」而不是清空编辑框）。
/// 模板 Trigger 里附加属性写 <c>Property="controls:TextBoxPlaceholder.Attach"</c>（不带括号），
/// 别用 DataTrigger + RelativeSource TemplatedParent（求值不可靠，踩过）。
/// </remarks>
public static class TextBoxPlaceholder
{
    /// <summary>占位文字；空串表示这个输入框不显示占位。</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(TextBoxPlaceholder), new PropertyMetadata(""));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

    /// <summary>关掉某个输入框的占位文字（默认全开）。</summary>
    public static readonly DependencyProperty AttachProperty = DependencyProperty.RegisterAttached(
        "Attach", typeof(bool), typeof(TextBoxPlaceholder), new PropertyMetadata(true));

    public static bool GetAttach(DependencyObject obj) => (bool)obj.GetValue(AttachProperty);
    public static void SetAttach(DependencyObject obj, bool value) => obj.SetValue(AttachProperty, value);
}
