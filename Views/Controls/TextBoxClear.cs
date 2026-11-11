using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LTools.Views.Controls;

/// <summary>
/// 全局输入框框内右侧的清除按钮（×），见 <c>Themes/Modern.xaml</c> 的 TextBox / VariableTextBox 模板。
/// 模板里的 Button 用 <c>Command</c> 指向这里的单例，点击清空 TextBox 并把焦点还回去；
/// 只在「非空 + 可编辑 + 启用」时由模板 Trigger 显示，不需要逐处改 XAML。
/// </summary>
public static class TextBoxClear
{
    /// <summary>模板里 <c>Button.Command</c> 用 <c>{x:Static}</c> 指向这个单例。</summary>
    public static readonly ClearCommand Command = new();

    /// <summary>
    /// 关掉某个 TextBox 的清除按钮（默认全开）。ComboBox 内部的编辑框用它排除：
    /// 右侧已经有下拉箭头，再挂一个 × 会挤，且清空应该用「选预设」而不是清空编辑框。
    /// </summary>
    public static readonly DependencyProperty AttachProperty = DependencyProperty.RegisterAttached(
        "Attach", typeof(bool), typeof(TextBoxClear), new PropertyMetadata(true));

    public static bool GetAttach(DependencyObject obj) => (bool)obj.GetValue(AttachProperty);
    public static void SetAttach(DependencyObject obj, bool value) => obj.SetValue(AttachProperty, value);

    public sealed class ClearCommand : ICommand
    {
        // 按钮只在有内容时可见，不需要动态刷新 CanExecute；恒 true 也避免绑定未就绪时按钮被禁用。
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (parameter is not TextBox tb) return;
            tb.Clear();
            tb.Focus();
        }
    }
}
