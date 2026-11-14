using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Core.Actions;
using LTools.Features.Script;

namespace LTools.Views;

/// <summary>
/// 代码参数（<see cref="InputEditor.code"/>）：等宽多行编辑器 + 「检查语法」。
/// 内容原样交给动作单元（声明里配 <see cref="InputSpec.Raw"/>，不走 <c>${变量}</c> 模板）。
/// </summary>
internal sealed class CodeArgEditor : IArgEditor
{
    public CodeArgEditor(ArgEditorContext context)
    {
        FrameworkElement owner = context.Owner;
        var box = new TextBox
        {
            Text = context.Current?.Literal.AsText() ?? "",
            FontFamily = (FontFamily)owner.FindResource("MonoFont"),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 260,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Top,
            ToolTip = "AHK v2 代码。LT_Var 读变量、LT_Set 写回变量、LT_Print 写标准输出。",
        };

        var check = new Button
        {
            Content = "检查语法",
            Style = (Style)owner.FindResource("Btn"),
            MinHeight = 28,
            Padding = new Thickness(10, 2, 10, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "只做语法检查，不会执行脚本（也不会产生任何副作用）",
        };

        var status = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        check.Click += (_, _) =>
        {
            status.Foreground = (Brush)owner.FindResource("SecondaryTextBrush");
            status.Text = "检查中…";
            check.IsEnabled = false;
            try
            {
                AhkCheckResult result = AhkRunner.Check(box.Text ?? "");
                Status(status, result.Ok, result.Ok ? "语法没问题" : result.Message);
            }
            catch (Exception ex)
            {
                Status(status, false, ex.Message);
            }
            finally
            {
                check.IsEnabled = true;
            }
        };

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
        };
        bar.Children.Add(check);
        bar.Children.Add(status);

        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(box);
        Grid.SetRow(bar, 1);
        panel.Children.Add(bar);

        Element = panel;
        _box = box;
        box.TextChanged += (_, _) => context.Edited();
    }

    public FrameworkElement Element { get; }

    // 代码是原样文本：没有变量 / 取值三态，也不需要冻结。
    public StepArg? Read() => StepArg.FromText(_box.Text ?? "");

    public ValueField? Field => null;

    private readonly TextBox _box;

    /// <summary>检查结果显示：成功用次要色，失败用与其它表单错误一致的危险色。</summary>
    private static void Status(TextBlock label, bool ok, string text)
    {
        label.Foreground = (Brush)label.FindResource(ok ? "SecondaryTextBrush" : "DangerBrush");
        label.Text = text;
    }
}
