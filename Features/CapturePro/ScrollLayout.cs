using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>
/// 长截图小面板摆在哪儿：优先落在选区**外面**（否则会被滚进自己的画面）。
/// </summary>
/// <remarks>
/// 纯函数：输入都是物理像素（选区 / 所在显示器工作区）与 DIP 尺寸（面板），返回 DIP 位置。
/// 顺序固定「右侧 → 下方 → 上方 → 左侧」，一个都放不下时才塞进工作区右下角（与选区重叠，
/// 这时调用方抓帧前要把面板藏一下，见 <c>CaptureScrollWindow</c>）。
/// </remarks>
internal static class ScrollLayout
{
    /// <summary>面板与选区 / 工作区边缘的距离（DIP）。</summary>
    public const double Gap = 12;

    /// <summary>算面板落点（DIP）。</summary>
    /// <param name="area">截图选区（物理像素）。</param>
    /// <param name="work">选区所在显示器的工作区（物理像素，已避开任务栏）。</param>
    /// <param name="scale">物理像素 / DIP。</param>
    /// <param name="panel">面板尺寸（DIP）。</param>
    public static Rect Place(Int32Rect area, Int32Rect work, double scale, Size panel)
    {
        if (scale <= 0) scale = 1;
        if (panel.Width <= 0 || panel.Height <= 0) return new Rect(0, 0, 0, 0);

        var screen = new Rect(work.X / scale, work.Y / scale,
            Math.Max(1, work.Width) / scale, Math.Max(1, work.Height) / scale);
        var selection = new Rect(area.X / scale, area.Y / scale,
            Math.Max(1, area.Width) / scale, Math.Max(1, area.Height) / scale);

        return PlaceAround(selection, screen, panel);
    }

    /// <summary>
    /// 同一个落点规则，但坐标已经是**窗口自己的 DIP**（屏幕选字的候选条画在覆盖层窗口里，
    /// 不需要再换一次物理像素）。<paramref name="view"/> 是窗口内容矩形。
    /// </summary>
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

        // 都放不下（选区几乎占满工作区）：塞右下角，抓帧时靠透明度让开
        double left = Math.Max(screen.Left, screen.Right - panel.Width - Gap);
        double top = Math.Max(screen.Top, screen.Bottom - panel.Height - Gap);
        return new Rect(left, top, panel.Width, panel.Height);
    }
}
