using System;
using System.Windows;
using System.Windows.Controls;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 「值来自变量 / 取值」时的冻结状态机（P4）：布尔、选项、多选这三种控件只能表达字面量，
/// 遇到别的三态就保留原引用，直到用户明确点「改用固定值」。
/// </summary>
/// <remarks>
/// <para>
/// 以前这段判断散在 <c>FreezeIfDynamic</c> 的局部变量里（<c>frozen</c> + 捕获的 <c>preserved</c>），
/// 只有真机点一遍才能验证。现在收成这个纯逻辑类：自测直接断言「三个状态 × 读回值 × 解冻后」。
/// </para>
/// <para>
/// 三态指 <see cref="StepArgKind"/>：<see cref="StepArgKind.literal"/>（可编辑）、
/// <see cref="StepArgKind.variable"/> 与 <see cref="StepArgKind.inline"/>（冻结）。
/// </para>
/// </remarks>
internal sealed class ArgFreezeState
{
    /// <summary>
    /// 取一个判据：<paramref name="current"/> 为 null 或字面量时不冻结（<c>read</c> 直接读控件），
    /// 变量 / 取值时冻结（<c>read</c> 原样返回它，控件当前值不参与）。
    /// </summary>
    public ArgFreezeState(StepArg? current)
    {
        Preserved = current;
        Frozen = IsFrozen(current);
    }

    /// <summary>要不要冻结（= 值不是字面量）。</summary>
    public static bool IsFrozen(StepArg? current) =>
        current != null && current.Kind != StepArgKind.literal;

    /// <summary>冻结时原样写回步骤的那个引用；不冻结时为 null。</summary>
    public StepArg? Preserved { get; }

    /// <summary>当前是否冻结。</summary>
    public bool Frozen { get; private set; }

    /// <summary>读回值：冻结时是原引用，否则是控件当前值。</summary>
    public StepArg? Read(Func<StepArg?> literal) => Frozen ? Preserved : literal();

    /// <summary>用户点了「改用固定值」：从此读控件当前值（控件自身的置灰由 UI 解开）。</summary>
    public void Thaw() => Frozen = false;
}

/// <summary>
/// 参数编辑器的公共件：冻结装饰（<see cref="Decorate"/>）与「这个值从哪儿来」的文案。
/// </summary>
internal static class ArgFreeze
{
    /// <summary>
    /// 把控件（或它外面那层「来源 + 改用固定值」）交回去，并给出读回值的方法。
    /// 冻结时额外摆一行「来自变量 ${x}」说明和一个出口按钮。
    /// </summary>
    /// <param name="edited">解冻之后要通知宿主「内容变了」（同步参数 + 刷新可见性）。</param>
    /// <param name="thaw">解冻时的额外动作（选项控件要落到第一项）。</param>
    public static FrozenEditor Decorate(FrameworkElement owner, StepArg? current,
        FrameworkElement control, Func<StepArg?> literalRead, Action edited, Action? thaw = null)
    {
        var state = new ArgFreezeState(current);
        if (!state.Frozen) return new FrozenEditor(control, literalRead);

        StepArg preserved = current!;
        control.IsEnabled = false;
        control.ToolTip = $"这个参数的值来自{Source(preserved)}——要改请改那一处，"
                          + "或点旁边的「改用固定值」。";

        var hint = new TextBlock
        {
            Text = $"来自{Source(preserved)}",
            Foreground = (System.Windows.Media.Brush)owner.FindResource("SecondaryTextBrush"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var convert = new Button
        {
            Content = "改用固定值",
            Style = (Style)owner.FindResource("BtnGhost"),
            FontSize = 12,
            Padding = new Thickness(8, 1, 8, 1),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "丢掉这个变量 / 取值引用，改成在这里直接选的值",
        };
        convert.Click += (_, _) =>
        {
            state.Thaw();
            control.IsEnabled = true;
            control.ToolTip = null;
            hint.Visibility = Visibility.Collapsed;
            convert.Visibility = Visibility.Collapsed;
            thaw?.Invoke();
            edited();
        };

        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.Children.Add(control);
        Grid.SetColumn(hint, 1);
        panel.Children.Add(hint);
        Grid.SetColumn(convert, 2);
        panel.Children.Add(convert);
        return new FrozenEditor(panel, () => state.Read(literalRead));
    }

    /// <summary>「这个值从哪儿来」：<c>变量 ${x}</c> / <c>取值「截图」</c>。</summary>
    public static string Source(StepArg arg) => arg.Kind switch
    {
        StepArgKind.variable => "变量 ${" + arg.VariablePath + "}",
        StepArgKind.inline => "取值「" + StepSummary.Title(arg.InlineStep?.Unit ?? "") + "」",
        _ => "固定值",
    };
}

/// <summary>装饰后的编辑器：摆在行里的控件 + 读回值。</summary>
internal sealed record FrozenEditor(FrameworkElement Element, Func<StepArg?> Read);
