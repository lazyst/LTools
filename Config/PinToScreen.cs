namespace LTools.Config;

/// <summary>
/// 贴图的行为开关（<c>configs/settings.json</c> 的 <c>PinToScreen</c> 段）。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>pin_to_screen/*</c>：拖动贴图时抖动隐藏同组其他贴图
/// （<c>shake_window_to_hide_others</c>，默认开）、双击 / 中键各自的动作
/// （<c>double_click_action</c> 默认 <c>thumbnail_mode</c>、<c>middle_mouse_button_action</c>
/// 默认 <c>reset_zoom</c>）、删除前确认（<c>confirm_before_destroying_window</c>，默认开）。
/// 取值见 <c>Features/CapturePro/PointerActions.cs</c>（英文键与上游一致）。
/// 点击穿透的开关与不透明度是**每张贴图自己的状态**（存在 <c>pins/pins.json</c> 的记录里），
/// 不是全局设置，所以不在这里。
/// </remarks>
public sealed class PinToScreenFile
{
    /// <summary>拖动贴图时左右快速抖动是否隐藏同组其他贴图。缺省 = 开。</summary>
    public bool? ShakeWindowToHideOthers { get; set; } = true;

    /// <summary>贴图双击做什么：none / reset_zoom / thumbnail_mode / close / destroy（hide_to_top 等还没做）。</summary>
    public string? DoubleClickAction { get; set; } = "thumbnail_mode";

    /// <summary>贴图中键做什么：与 <see cref="DoubleClickAction"/> 同一套取值。</summary>
    public string? MiddleButtonAction { get; set; } = "reset_zoom";

    /// <summary>「删除贴图」前先弹确认（关掉之后删了就找不回来）。缺省 = 开。</summary>
    public bool? ConfirmBeforeDestroyingWindow { get; set; } = true;
}
