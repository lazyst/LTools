using System.Collections.Generic;

namespace LTools.Config;

/// <summary>
/// 截图 Pro 的快捷键（<c>configs/settings.json</c> 的 <c>CaptureShortcuts</c> 段）。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的四张快捷键表：<c>screenshot_shortcuts/*</c>（覆盖层）、
/// <c>drawing_shortcuts/*</c>（绘图工具）、<c>pin_to_screen_shortcuts/*</c>（贴图窗口）、
/// <c>screen_recording_shortcuts/*</c>（录屏面板）。
/// </para>
/// <para>
/// **只存改过的项**：键 = 上游动作 Id（如 <c>copy_to_clipboard</c>），值 = 组合键列表
/// （如 <c>["Ctrl+C", "Return"]</c>）。没有这一项 = 用上游默认值；列表为空 = 这个动作没有快捷键。
/// 默认值与归一化在 <c>Features/CapturePro/CaptureShortcutTable.cs</c>（纯函数，<c>--selftest</c> 直接断言）。
/// </para>
/// </remarks>
public sealed class CaptureShortcutsFile
{
    /// <summary>覆盖层快捷键（<c>screenshot_shortcuts/*</c>）。</summary>
    public Dictionary<string, List<string>> Screenshot { get; set; } = new();

    /// <summary>绘图工具快捷键（<c>drawing_shortcuts/*</c>）。</summary>
    public Dictionary<string, List<string>> Drawing { get; set; } = new();

    /// <summary>贴图窗口快捷键（<c>pin_to_screen_shortcuts/*</c>）。</summary>
    public Dictionary<string, List<string>> PinToScreen { get; set; } = new();

    /// <summary>录屏面板快捷键（<c>screen_recording_shortcuts/*</c>）。</summary>
    public Dictionary<string, List<string>> Recording { get; set; } = new();
}
