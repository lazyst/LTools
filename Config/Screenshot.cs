using System.Collections.Generic;

namespace LTools.Config;

/// <summary>
/// 截图 Pro 的全局设置（<c>configs/settings.json</c> 的 <c>Screenshot</c> 段）。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>screenshot/*</c>：「延时截图」的等待秒数
/// （<c>screenshot/delay_seconds</c>，clamp 1–10，默认 3），以及覆盖层里
/// 「选择工具 + 双击 / 中键」各自的完成动作（<c>screenshot/double_click_action</c>、
/// <c>screenshot/middle_mouse_button_action</c>）。取值见
/// <c>Features/CapturePro/PointerActions.cs</c>（英文键与上游一致）。
/// 采集入口自己的参数（截哪块、要不要自动保存）挂在功能参数上，不在这里。
/// </remarks>
public sealed class ScreenshotFile
{
    /// <summary>「延时截图」等待多少秒（1–10，默认 3）。</summary>
    public int? DelaySeconds { get; set; } = 3;

    /// <summary>覆盖层里双击（选择工具）做什么：copy / save / quick_save / pin / ocr / ocr_copy / cancel / none。</summary>
    public string? DoubleClickAction { get; set; } = "copy";

    /// <summary>覆盖层里中键做什么：同 <see cref="DoubleClickAction"/> 的取值。</summary>
    public string? MiddleButtonAction { get; set; } = "pin";

    /// <summary>
    /// 直接截前台窗口 / 当前显示器时响一声快门音（照上游
    /// <c>screenshot/shutter_sound_notification</c>，默认开）。
    /// </summary>
    public bool? ShutterSound { get; set; } = true;

    /// <summary>
    /// 覆盖层开始截图时响一声快门音（照上游
    /// <c>screenshot/screenshot_sound_notification</c>，默认关）。
    /// </summary>
    public bool? ScreenshotSound { get; set; } = false;

    /// <summary>
    /// 描边颜色预设（照上游 <c>screenshot/stroke_color_presets</c>）：恒 5 个
    /// <c>#RRGGBBAA</c>，主色 / 文字描边色 / 聚光共用。空 = 用
    /// <see cref="Features.CapturePro.ColorPresets"/> 的默认值。
    /// </summary>
    public List<string>? StrokeColorPresets { get; set; }

    /// <summary>
    /// 填充颜色预设（照上游 <c>screenshot/fill_color_presets</c>）：恒 5 个
    /// <c>#RRGGBBAA</c>，填充色 / 文字底色 / 箭头外描边共用。
    /// </summary>
    public List<string>? FillColorPresets { get; set; }

    /// <summary>
    /// 荧光颜色预设（照上游 <c>screenshot/highlight_color_presets</c>）：恒 5 个
    /// <c>#RRGGBBAA</c>，荧光笔与矩形荧光共用。
    /// </summary>
    public List<string>? HighlightColorPresets { get; set; }

    /// <summary>
    /// 长截图自动滚动间隔毫秒（照上游 <c>screenshot/scrolling_auto_scroll_interval_ms</c>，
    /// clamp 128–1000，默认 200）。
    /// </summary>
    public int? ScrollingAutoScrollIntervalMs { get; set; } = 200;

    /// <summary>
    /// 新截图默认把鼠标指针画进成品（照上游 <c>screenshot/capture_cursor</c>，默认关）。
    /// 只作用于「截图」的抓帧，不污染模板找图 / OCR。
    /// </summary>
    public bool? CaptureCursor { get; set; } = false;

    /// <summary>
    /// Esc（截图快捷键）退出前先确认（照上游
    /// <c>screenshot/confirm_before_exiting_via_shortcut</c>，默认关）。
    /// </summary>
    public bool? ConfirmBeforeExitViaShortcut { get; set; } = false;

    /// <summary>
    /// 非移动工具下也允许拖选区边框改大小（照上游
    /// <c>screenshot/quick_selection_modification</c>，默认开）。
    /// </summary>
    public bool? QuickSelectionModification { get; set; } = true;

    /// <summary>
    /// 拖拽改选区尺寸时边框怎么跟鼠标（照上游
    /// <c>screenshot/selection_resize_mode</c>）：<c>follow_mouse_movement</c>（按位移）/
    /// <c>follow_mouse_position</c>（起拖就把边框贴到指针）。
    /// </summary>
    public string? SelectionResizeMode { get; set; } = "follow_mouse_movement";

    /// <summary>
    /// 复制 / 保存截图时按显示器缩放降采样（照上游 <c>screenshot/hidpi_image_scaling</c>）：
    /// <c>disabled</c> / <c>auto</c> / 125…350（每 25 一档）。
    /// </summary>
    public string? HidpiImageScaling { get; set; } = "disabled";

    /// <summary>
    /// 截图存成 PDF 时的纸张（照上游 <c>screenshot/pdf_page_size</c>）：
    /// <c>image_size</c> / <c>a4_portrait</c> / <c>a4_landscape</c>。
    /// </summary>
    public string? PdfPageSize { get; set; } = "a4_portrait";
}
