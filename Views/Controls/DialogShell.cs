using System.Windows;
using System.Windows.Controls;

namespace LTools.Views.Controls;

/// <summary>
/// 共用对话框外壳：模板里「标题栏 36 / <c>*</c> 滚动内容 / <c>Auto</c> 常驻按钮栏」三行。
/// </summary>
/// <remarks>
/// <para>
/// 刻意用 <see cref="ContentControl"/> 派生而不是 <c>UserControl</c>：Content / Footer 里的
/// <c>x:Name</c> 由 XAML 编译器直接连到宿主窗口的字段，不新建命名范围，
/// 现有对话框的 code-behind（<c>OkBtn</c> / <c>MessageText</c> / <c>Click</c>）一行都不用改。
/// </para>
/// <para>
/// 隐性样式在 <c>Themes/Modern.xaml</c>；窗口仍用 <c>ModernWindow</c> 套外框与圆角。
/// 配 <c>WindowSizing.Mode="Dialog"</c>：窗口被工作区夹住时 <c>*</c> 行压缩、内容滚动，
/// Footer 按钮栏永远留在可视区（不会被任务栏挡住）。
/// </para>
/// </remarks>
public class DialogShell : ContentControl
{
    /// <summary>底部常驻按钮栏（不跟着内容滚）。</summary>
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(object), typeof(DialogShell), new PropertyMetadata(null));

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }
}
