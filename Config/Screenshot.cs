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
}
