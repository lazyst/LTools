namespace LTools.Config;

/// <summary>
/// 贴图的行为开关（<c>configs/settings.json</c> 的 <c>PinToScreen</c> 段）。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>pin_to_screen/*</c>：目前只有「拖动贴图时抖动隐藏同组其他贴图」一项
/// （<c>shake_window_to_hide_others</c>，默认开）。点击穿透的开关与不透明度是**每张贴图自己的状态**
/// （存在 <c>pins/pins.json</c> 的记录里），不是全局设置，所以不在这里。
/// </remarks>
public sealed class PinToScreenFile
{
    /// <summary>拖动贴图时左右快速抖动是否隐藏同组其他贴图。缺省 = 开。</summary>
    public bool? ShakeWindowToHideOthers { get; set; } = true;
}
