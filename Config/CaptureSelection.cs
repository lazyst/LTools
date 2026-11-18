using System.Collections.Generic;

namespace LTools.Config;

/// <summary>
/// 截图 Pro 的选区设置（<c>configs/ui.json</c> 的 <c>CaptureSelection</c>）：
/// 圆角 / 阴影 / 边框 / 比例锁 / 智能选区，加上「上次选区」与用户存的快速集。
/// </summary>
/// <remarks>
/// 全部是「记不住也不影响截图」的外观偏好，所以放 ui 域（不升 schema、不动 settings）。
/// 长度单位是 DIP；<see cref="CaptureSelectionPreset"/> 里的 X / Y / W / H 是屏幕物理像素
/// （与界面显示、历史记录同一套），这样换机 / 换缩放比例时还能落到正确的位置上。
/// </remarks>
public sealed class CaptureSelectionFile
{
    /// <summary>圆角半径（DIP，0–256）。</summary>
    public double CornerRadius { get; set; }

    /// <summary>阴影宽（DIP，0–64）。</summary>
    public double ShadowWidth { get; set; }

    /// <summary>阴影色（<c>#RRGGBB</c>）；空 = 默认 <c>#333333</c>。</summary>
    public string? ShadowColor { get; set; }

    /// <summary>边框宽（DIP，0–64）。</summary>
    public double BorderWidth { get; set; }

    /// <summary>边框色（<c>#RRGGBB</c>）；空 = 默认白。</summary>
    public string? BorderColor { get; set; }

    /// <summary>影响方式：<c>shadow</c>（默认）/ <c>border</c>。</summary>
    public string? BorderType { get; set; }

    /// <summary>比例锁的预设（<c>free</c> / <c>1:1</c> / <c>16:9</c> …）；空 = 自由。</summary>
    public string? AspectPreset { get; set; }

    /// <summary>比例是不是锁着的（预设为 <c>free</c> 时表示「按当前选区的自定义比例」）。</summary>
    public bool? LockAspect { get; set; }

    /// <summary>智能选区：<c>window_sub_element</c>（默认）/ <c>window</c> / <c>disabled</c>。</summary>
    public string? SmartSelection { get; set; }

    /// <summary>切换元素层级时的过渡：<c>soft</c>（默认）/ <c>fast</c> / <c>none</c>。</summary>
    public string? SelectionTransition { get; set; }

    /// <summary>「上次选区」快速集：上一次提交的几何 + 外观。</summary>
    public CaptureSelectionPreset? Previous { get; set; }

    /// <summary>用户存的选区快速集（下拉里排在「上次选区」之后）。</summary>
    public List<CaptureSelectionPreset> Presets { get; set; } = new();
}

/// <summary>一个选区快速集：几何（屏幕物理像素）+ 当时的外观。</summary>
public sealed class CaptureSelectionPreset
{
    public string Name { get; set; } = string.Empty;

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>当时是不是锁着宽高比（选中这个快速集时一起恢复）。</summary>
    public bool? LockAspect { get; set; }

    public double CornerRadius { get; set; }

    public double ShadowWidth { get; set; }

    public string? ShadowColor { get; set; }

    public double BorderWidth { get; set; }

    public string? BorderColor { get; set; }

    public string? BorderType { get; set; }
}

