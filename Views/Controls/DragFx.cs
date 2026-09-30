using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CapsLockPro.Views.Controls;

/// <summary>
/// 拖动排序视觉辅助（§12「所有拖动排序都要有幽灵跟随、占位、丝滑动画过渡」）：
/// <list type="bullet">
/// <item><see cref="CaptureByIndex"/> / <see cref="AnimateReorderByIndex"/>：ListBox 项让位动画
///   （重排后位置变化的 container 从旧位置滑到新位置，160ms 缓出）；</item>
/// <item><see cref="AnimateSwap"/>：格子交换动画（两格内容从对方位置滑入）；</item>
/// <item><see cref="Slide"/>：单元素从指定偏移滑回原位。</item>
/// </list>
/// 位置匹配用「新索引 → 旧索引」映射（<see cref="OldIndexOfMove"/> / <see cref="OldIndexOfSwap"/>），
/// 不依赖项对象身份——菜单项列表等会因序号重写而每次重建字符串，身份型匹配会失效。
/// </summary>
internal static class DragFx
{
    private const int DefaultDurationMs = 160;

    /// <summary>记录 ListBox 各 index 的 container 当前位置（相对 list）；重排前调用。</summary>
    public static Point?[] CaptureByIndex(ItemsControl list)
    {
        var arr = new Point?[list.Items.Count];
        for (int i = 0; i < list.Items.Count; i++)
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c)
                arr[i] = c.TransformToAncestor(list).Transform(new Point(0, 0));
        return arr;
    }

    /// <summary>
    /// 重排已发生、布局已更新后调用：对每个新 index，用 <paramref name="oldIndexOf"/>
    /// 映射到旧 index 的位置作为起始，滑回当前位置。
    /// </summary>
    public static void AnimateReorderByIndex(
        ItemsControl list, Point?[] old, Func<int, int> oldIndexOf, int durationMs = DefaultDurationMs)
    {
        if (old.Length == 0) return;
        list.UpdateLayout();
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            int oi = oldIndexOf(i);
            if (oi < 0 || oi >= old.Length || old[oi] is not Point p) continue;
            var now = c.TransformToAncestor(list).Transform(new Point(0, 0));
            Slide(c, p.X - now.X, p.Y - now.Y, durationMs);
        }
    }

    /// <summary>插入语义的新→旧索引映射：把 <paramref name="from"/> 拔出插到 <paramref name="to"/>。</summary>
    public static int OldIndexOfMove(int i, int from, int to)
    {
        if (i == to) return from;
        if (from < to) return i >= from && i < to ? i + 1 : i;
        return i > to && i <= from ? i - 1 : i;
    }

    /// <summary>交换语义的新→旧索引映射：交换 <paramref name="a"/> 与 <paramref name="b"/>。</summary>
    public static int OldIndexOfSwap(int i, int a, int b)
        => i == a ? b : i == b ? a : i;

    /// <summary>两元素交换动画（格子场景）：交换已发生后调用，让两格内容从对方旧位置滑入。</summary>
    public static void AnimateSwap(
        FrameworkElement a, FrameworkElement b, FrameworkElement relativeTo,
        int durationMs = DefaultDurationMs)
    {
        var pa = a.TransformToAncestor(relativeTo).Transform(new Point(0, 0));
        var pb = b.TransformToAncestor(relativeTo).Transform(new Point(0, 0));
        Slide(a, pb.X - pa.X, pb.Y - pa.Y, durationMs);
        Slide(b, pa.X - pb.X, pa.Y - pb.Y, durationMs);
    }

    /// <summary>单元素从指定偏移滑回原位（dx/dy 为起始偏移，目标 0）。</summary>
    public static void Slide(UIElement el, double dx, double dy, int durationMs = DefaultDurationMs)
    {
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
        if (el.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            el.RenderTransform = tt;
        }
        tt.X = dx; tt.Y = dy;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(durationMs);
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
    }
}
