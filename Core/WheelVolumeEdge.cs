namespace LTools.Core;

/// <summary>
/// 「边缘滚轮音量」算哪条屏幕边（设置 → 通用）。
/// </summary>
/// <remarks>
/// 只看光标所在显示器的 <c>rcMonitor</c>（整块屏，不是工作区）：任务栏压在边缘时也算，
/// 因为用户要的就是「滚到屏幕尽头调音量」这个肌肉记忆。
/// </remarks>
public enum WheelVolumeEdge
{
    /// <summary>屏幕底部边缘（默认，沿用最早的行为）。</summary>
    bottom,

    /// <summary>屏幕顶部边缘（任务栏放顶部的人用）。</summary>
    top,

    /// <summary>上下边缘都算。</summary>
    both,
}
