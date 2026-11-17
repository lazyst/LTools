using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>
/// 长截图的小预览面板摆在哪儿：优先落在选区**外面**（不挡 1:1 的拼接画面）。
/// </summary>
/// <remarks>
/// 纯函数：坐标已经是窗口自己的 DIP（预览面板画在覆盖层窗口里，与屏幕选字的候选条同一套规则）。
/// 顺序固定「右侧 → 下方 → 上方 → 左侧」，一个都放不下时才塞进工作区右下角（与选区重叠：
/// 面板是覆盖层自己的元素，不会被 <c>PrintWindow</c> 抓进画面，重叠只是挡一点预览）。
/// </remarks>
internal static class ScrollLayout
{
    /// <summary>面板与选区 / 窗口边缘的距离（DIP）。</summary>
    public const double Gap = 12;

    /// <summary>算面板落点（DIP）：<paramref name="selection"/> 与 <paramref name="view"/> 都是窗口内坐标。</summary>
    public static Rect PlaceIn(Rect selection, Size panel, Size view)
    {
        if (panel.Width <= 0 || panel.Height <= 0 || view.Width <= 0 || view.Height <= 0)
            return new Rect(0, 0, 0, 0);

        return PlaceAround(selection, new Rect(0, 0, view.Width, view.Height), panel);
    }

    /// <summary>「右侧 → 下方 → 上方 → 左侧」，都放不下才塞进右下角。</summary>
    private static Rect PlaceAround(Rect selection, Rect screen, Size panel)
    {
        var spots = new[]
        {
            new Point(selection.Right + Gap, selection.Top),                        // 右侧
            new Point(selection.Left, selection.Bottom + Gap),                      // 下方
            new Point(selection.Left, selection.Top - Gap - panel.Height),          // 上方
            new Point(selection.Left - Gap - panel.Width, selection.Top),           // 左侧
        };

        foreach (var at in spots)
        {
            var rect = new Rect(at, panel);
            if (screen.Contains(rect)) return rect;
        }

        // 都放不下（选区几乎占满整屏）：塞右下角
        double left = Math.Max(screen.Left, screen.Right - panel.Width - Gap);
        double top = Math.Max(screen.Top, screen.Bottom - panel.Height - Gap);
        return new Rect(left, top, panel.Width, panel.Height);
    }
}
