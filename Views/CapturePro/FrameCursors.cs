using System.Windows.Input;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 框把手 → 鼠标指针：覆盖层选区 / 标注框、拼图、全屏画布、图片编辑窗共用这一份映射。
/// </summary>
/// <remarks>
/// 八个方向的指针是死的，只有「不是把手」的那两种各处不同，所以由调用方给：
/// <paramref name="fallback"/> = 光标不在把手上时（覆盖层是画框的十字、拼图是移动手型、
/// 画布与编辑窗是箭头），<paramref name="move"/> = 落在框内可整体拖动时（不传就同 fallback）。
/// </remarks>
internal static class FrameCursors
{
    public static Cursor For(FrameHandle handle, Cursor fallback, Cursor? move = null) => handle switch
    {
        FrameHandle.Top or FrameHandle.Bottom => Cursors.SizeNS,
        FrameHandle.Left or FrameHandle.Right => Cursors.SizeWE,
        FrameHandle.TopLeft or FrameHandle.BottomRight => Cursors.SizeNWSE,
        FrameHandle.TopRight or FrameHandle.BottomLeft => Cursors.SizeNESW,
        FrameHandle.Move => move ?? fallback,
        _ => fallback,
    };
}
