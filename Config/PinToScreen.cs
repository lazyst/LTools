namespace LTools.Config;

/// <summary>
/// 贴图的行为开关（<c>configs/settings.json</c> 的 <c>PinToScreen</c> 段）。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>pin_to_screen/*</c>：拖动贴图时抖动隐藏同组其他贴图
/// （<c>shake_window_to_hide_others</c>，默认开）、双击 / 中键各自的动作
/// （<c>double_click_action</c> 默认 <c>thumbnail_mode</c>、<c>middle_mouse_button_action</c>
/// 默认 <c>reset_zoom</c>）、删除前确认（<c>confirm_before_destroying_window</c>，默认开）。
/// 取值见 <c>Features/CapturePro/PointerActions.cs</c>（英文键与上游一致）。
/// </para>
/// <para>
/// 外观与滚轮那一段（<c>show_border_by_default</c> / 六个颜色 / <c>mouse_wheel_*</c> /
/// <c>show_window_buttons</c> / <c>confirm_before_closing_window</c>）由
/// <c>Features/CapturePro/PinSettings.cs</c> 读写；默认值逐字照
/// <c>configurationschema.cpp</c>。**每张贴图自己的边框 / 阴影开关**存在
/// <c>pins/pins.json</c> 的记录里（<c>PinRecord.ShowBorder</c> / <c>ShowShadow</c>），
/// 这里只放「新贴图默认怎么显示」。
/// </para>
/// <para>
/// 点击穿透的开关与不透明度同样是**每张贴图自己的状态**，不在这里。
/// </para>
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

    /// <summary>「关闭贴图」（还能从历史贴回）前先弹确认。缺省 = 关。</summary>
    public bool? ConfirmBeforeClosingWindow { get; set; } = false;

    /// <summary>新贴图默认显示边框。缺省 = 开。</summary>
    public bool? ShowBorderByDefault { get; set; } = true;

    /// <summary>新贴图默认显示阴影。缺省 = 关。</summary>
    public bool? ShowShadowByDefault { get; set; } = false;

    /// <summary>默认边框色（<c>#RRGGBBAA</c>）。</summary>
    public string? BorderColor { get; set; } = "#DBDBDBFF";

    /// <summary>拖动 / 悬停等「活动」状态的边框色。</summary>
    public string? BorderActiveColor { get; set; } = "#69B1FFFF";

    /// <summary>锁定状态的边框色。</summary>
    public string? LockedBorderColor { get; set; } = "#FAAD14FF";

    /// <summary>默认阴影色（<c>#RRGGBBAA</c>）。</summary>
    public string? ShadowColor { get; set; } = "#BFBFBFFF";

    /// <summary>活动状态的阴影色。</summary>
    public string? ShadowActiveColor { get; set; } = "#69B1FFFF";

    /// <summary>锁定状态的阴影色。</summary>
    public string? LockedShadowColor { get; set; } = "#FAAD14FF";

    /// <summary>
    /// 滚轮缩放锚点：<c>mouse_position</c> / <c>top_left</c> / <c>top_right</c> /
    /// <c>bottom_left</c> / <c>bottom_right</c> / <c>center</c>。缺省 = 光标位置。
    /// </summary>
    public string? MouseWheelZoomMode { get; set; } = "mouse_position";

    /// <summary>滚轮一格改多少不透明度（百分点，1–50）。缺省 = 5。</summary>
    public int? MouseWheelOpacityStep { get; set; } = 5;

    /// <summary>滚轮一格改多少缩放（百分点，1–50）。缺省 = 10。</summary>
    public int? MouseWheelZoomStep { get; set; } = 10;

    /// <summary>贴图右上角显示窗口按钮（关闭 ✕ / 标注）。缺省 = 开。</summary>
    public bool? ShowWindowButtons { get; set; } = true;

    /// <summary>
    /// 贴图创建时后台先识别一次文字（照上游 <c>pin_to_screen/automatic_text_recognition</c>）。
    /// 识别结果给「显示文字」与隐形文字层复用，点菜单时不用再等。缺省 = 开。
    /// </summary>
    public bool? AutomaticTextRecognition { get; set; } = true;

    /// <summary>
    /// 什么时候能在贴图上直接划选识别出的文字：<c>only_when_displayed</c>（只在「显示文字」里）/
    /// <c>always</c>（照上游 <c>pin_to_screen/text_selection_on_recognition_results</c>，
    /// 在图片上叠一层隐形文字层，直接拖选 + 复制）。缺省 = <c>always</c>。
    /// </summary>
    public string? TextSelectionOnRecognitionResults { get; set; } = "always";

    /// <summary>
    /// 贴到重复内容时做什么：<c>none</c>（什么都不贴）/ <c>shake_window</c>（抖一下已有的那张）/
    /// <c>restore_last_closed_window</c>（贴回最近关掉的那张）/ <c>repeat_action</c>（照常再贴一张）。
    /// 照上游 <c>pin_to_screen/duplicate_content_action</c>，缺省 = <c>shake_window</c>。
    /// </summary>
    public string? DuplicateContentAction { get; set; } = "shake_window";

    /// <summary>
    /// 贴图比屏幕工作区大时自动缩小（照上游 <c>pin_to_screen/auto_resize_window</c>）。
    /// 关掉之后剪贴板 / 文件贴图按原始尺寸贴出，可能超出屏幕。缺省 = 开。
    /// </summary>
    public bool? AutoResizeWindow { get; set; } = true;
}
