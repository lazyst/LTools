using System.Collections.Generic;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>
/// 采集时那一块屏幕的位置（虚拟桌面物理像素），照上游 <c>CaptureHistoryDisplayAsset</c> 的
/// 「一块屏一张图」：历史卡片 / 大图里每块屏是一张单独的预览（原始屏幕，不压暗、不画选框）。
/// </summary>
/// <remarks>
/// 只存几何：整屏底图是一张 <c>displays/&lt;id&gt;.png</c>（虚拟桌面拼好的），
/// 预览时按这些矩形从它上面裁——省一份重复的大图，换机 / 换缩放也还能裁对。
/// </remarks>
internal sealed class CaptureScreenDto
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public static CaptureScreenDto Of(Int32Rect rect) => new()
    {
        X = rect.X,
        Y = rect.Y,
        Width = rect.Width,
        Height = rect.Height,
    };

    public static List<CaptureScreenDto> Of(IEnumerable<Int32Rect> rects)
    {
        var list = new List<CaptureScreenDto>();
        foreach (var rect in rects)
            if (rect.Width > 0 && rect.Height > 0) list.Add(Of(rect));
        return list;
    }

    public Int32Rect Rect => new(X, Y, Width, Height);
}
