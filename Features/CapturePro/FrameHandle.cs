namespace LTools.Features.CapturePro;

/// <summary>
/// 拖动把手的位置：八个方向 + 「整体移动」（落在框内）。
/// </summary>
/// <remarks>
/// 选区与标注共用这一套——两边都是「一个矩形 + 八个可拖的点」，
/// 命中与改框的算法都在 <see cref="MarkGeometry.HitFrame"/> / <see cref="MarkGeometry.ResizeFrame"/>，
/// 别再各写一份（以前选区那套写在窗口里，标注想加手柄就得抄一遍）。
/// </remarks>
internal enum FrameHandle
{
    None,
    Move,
    Left,
    Top,
    Right,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}
