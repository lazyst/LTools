using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace LTools.Views.Controls;

/// <summary>
/// 类 <see cref="WrapPanel"/>，但按「统一步长」横向铺满可用宽度——消除卡片网格每行右侧的空隙。
/// 做法：按自然宽度贪心分行 → 取各行最大子项数为列数 → 所有子项统一槽宽 = 行宽 / 列数。
/// 于是最满的那一行恰好填满整行（无右空隙），其余行左侧对齐、卡片尺寸与满行一致。
/// 用于 <see cref="ActionPoolPicker"/> 动作网格：固定宽窗口 + 固定宽卡片时 <c>WrapPanel</c> 会留出
/// 近一张卡宽的空隙，本面板把行宽均分到各列消除之；窗口缩放或出现滚动条时按
/// <see cref="FrameworkElement.ArrangeOverride"/> 传入的实际宽度重新分行。
/// </summary>
public sealed class StretchWrapPanel : Panel
{
    /// <summary>子项布局槽宽（含 Margin）。固定宽卡片稳定不受可用宽度影响。</summary>
    private static double SlotWidth(UIElement c)
    {
        var fe = c as FrameworkElement;
        return c.DesiredSize.Width + (fe?.Margin.Left ?? 0) + (fe?.Margin.Right ?? 0);
    }

    private static double SlotHeight(UIElement c)
    {
        var fe = c as FrameworkElement;
        return c.DesiredSize.Height + (fe?.Margin.Top ?? 0) + (fe?.Margin.Bottom ?? 0);
    }

    /// <summary>按自然槽宽贪心分行。宽度无穷时退化为一列。</summary>
    private static List<List<UIElement>> PackRows(IEnumerable<UIElement> children, double rowWidth)
    {
        var rows = new List<List<UIElement>>();
        var cur = new List<UIElement>();
        var x = 0.0;

        foreach (var c in children)
        {
            var sw = SlotWidth(c);
            // 放不下且本行已有内容 → 换行（无穷宽时永不触发 → 单行）
            if (x + sw > rowWidth + 0.001 && x > 0)
            {
                rows.Add(cur);
                cur = new List<UIElement>();
                x = 0;
            }
            cur.Add(c);
            x += sw;
        }
        rows.Add(cur);
        return rows;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var infiniteW = double.IsPositiveInfinity(availableSize.Width);

        foreach (UIElement c in InternalChildren)
            c.Measure(availableSize);

        var rows = PackRows(InternalChildren.Cast<UIElement>(), infiniteW ? double.PositiveInfinity : availableSize.Width);

        var totalH = 0.0;
        var maxRowW = 0.0;
        foreach (var r in rows)
        {
            var rh = 0.0;
            var rw = 0.0;
            foreach (var c in r)
            {
                rh = Math.Max(rh, SlotHeight(c));
                rw += SlotWidth(c);
            }
            totalH += rh;
            maxRowW = Math.Max(maxRowW, rw);
        }

        // 被约束时认领满宽（让 ScrollViewer 不出横向滚动条且铺满），无穷时用自然宽
        return new Size(infiniteW ? maxRowW : availableSize.Width, totalH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rows = PackRows(InternalChildren.Cast<UIElement>(), finalSize.Width);

        // 列数 = 各行最大子项数。统一槽宽 = 行宽 / 列数 → 最满行恰好填满、卡片尺寸各行一致
        var cols = 0;
        foreach (var r in rows) cols = Math.Max(cols, r.Count);
        var slotW = cols > 0 ? finalSize.Width / cols : 0;

        var y = 0.0;
        foreach (var r in rows)
        {
            var rh = 0.0;
            foreach (var c in r) rh = Math.Max(rh, SlotHeight(c));

            var cx = 0.0;
            foreach (var c in r)
            {
                // 兜底：不小于自然槽宽（卡片不同宽时不会被压扁）
                var sw = Math.Max(slotW, SlotWidth(c));
                // Arrange 的 Rect 为含 Margin 的布局槽；引擎自动扣除 Margin 后定位子元素
                c.Arrange(new Rect(cx, y, sw, rh));
                cx += sw;
            }
            y += rh;
        }

        return finalSize;
    }
}
