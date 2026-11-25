namespace LTools.Core.Link;

/// <summary>
/// 设置窗口相关的链路方法（**执行器 → 宿主**）：窗口只有一个家，普通权限宿主。
/// </summary>
/// <remarks>
/// 设置是最后一个搬走的窗口（`docs/PROCESS_SPLIT.md` §15.4 第 5 步）：它同时编辑动作 / 子动作 /
/// 快捷键 / 宏 / 触发 / 采集策略 / MCP / 备份，只有前面几个域都归宿主之后才能搬。搬完之后
/// 提权进程里**不再有** <c>ConfigHelperWindow</c>，托盘 / CapsLock+\ / 托盘双击这些入口
/// 只发一条意图（本文件），窗口由宿主按自己的身份开。
/// </remarks>
internal static class SettingsMethods
{
    /// <summary>开 / 切 / 关设置窗口。负载 <see cref="SettingsOpenRequest"/>。</summary>
    public const string Show = "settings.show";
}

/// <summary><see cref="SettingsOpenRequest.Action"/> 的取值。</summary>
internal static class SettingsActions
{
    /// <summary>开着就关（CapsLock+\ / 托盘「设置」）。</summary>
    public const string Toggle = "toggle";

    /// <summary>开并提到最前；<see cref="SettingsOpenRequest.Target"/> 为空时停在上次记住的页。</summary>
    public const string Open = "open";

    /// <summary>关掉（`--perf` 采样用）。</summary>
    public const string Close = "close";
}

/// <summary>
/// 一次「开设置」的意图。<see cref="Target"/> 是导航 tag（<c>actions</c> / <c>macros-list</c> /
/// <c>panel.scene</c>…），为空 = 停在上次记住的页。
/// </summary>
internal sealed record SettingsOpenRequest(string Action, string? Target = null);
