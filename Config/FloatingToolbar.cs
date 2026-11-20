namespace LTools.Config;

/// <summary>
/// 悬浮工具条（<c>configs/settings.json</c> 的 <c>FloatingToolbar</c> 段）。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>floating_toolbar/*</c>：开关 / 模式 / 不透明度 / 拖放行为 /
/// 全屏隐藏 / 截图时隐藏 / 落点 / 布局。默认值逐字照 <c>configurationschema.cpp</c>。
/// </remarks>
public sealed class FloatingToolbarFile
{
    /// <summary>显示悬浮工具条（默认关）。</summary>
    public bool Enabled { get; set; }

    /// <summary><c>icon</c> = 平时只露一个图标、悬停展开；<c>toolbar</c> = 一直展开。</summary>
    public string? Mode { get; set; } = "icon";

    /// <summary>不透明度（10–100，默认 50；指针停上去恢复 100）。</summary>
    public int Opacity { get; set; } = 50;

    /// <summary>
    /// 把文件拖到工具条上做什么：<c>pin_to_screen</c>（默认）/ <c>image_stitching</c> /
    /// <c>image_splitting</c> / <c>image_editing</c>。
    /// </summary>
    public string? DropBehavior { get; set; } = "pin_to_screen";

    /// <summary>前台窗口全屏时隐藏（默认开）。</summary>
    public bool HideInFullscreen { get; set; } = true;

    /// <summary>截图 / 录屏进行时隐藏（默认开）。</summary>
    public bool HideDuringCapture { get; set; } = true;

    /// <summary>工具条落点（屏幕 DIP）；<c>null</c> = 还没摆过，用默认位置。</summary>
    public FloatingToolbarPlacement? Placement { get; set; }

    /// <summary>布局：一行一叠（叠内是功能引用，如 <c>ltools.captureScreen</c>）。</summary>
    public System.Collections.Generic.List<string>? Positions { get; set; }

    /// <summary>收起来的功能引用。</summary>
    public System.Collections.Generic.List<string>? Hidden { get; set; }
}

/// <summary>悬浮工具条的落点（DIP，虚拟桌面坐标）。</summary>
public sealed class FloatingToolbarPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }
}
