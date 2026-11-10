using System;

namespace LTools.Config;

/// <summary>
/// 配置域（见 <c>docs/ARCHITECTURE.md</c> P3）：一次写入只碰被点的那个文件。
/// </summary>
/// <remarks>
/// 用法：单域走 <see cref="DomainStores"/> 里对应的 store；只有「菜单组 + 动作清单」这种
/// 同生共死的组合才用 <see cref="ConfigDomain"/> 按位或交给 <see cref="LTools.Core.ConfigIO"/>。
/// </remarks>
[Flags]
internal enum ConfigDomain
{
    none = 0,

    /// <summary><c>configs/settings.json</c>：全局行为。</summary>
    settings = 1 << 0,

    /// <summary><c>configs/ui.json</c>：界面状态。</summary>
    ui = 1 << 1,

    /// <summary><c>configs/hotkeys.json</c>：全局快捷键（预设组 + 绑定 + 默认名单）。</summary>
    hotkeys = 1 << 2,

    /// <summary><c>configs/panel.json</c>：超级面板。</summary>
    panel = 1 << 3,

    /// <summary><c>configs/floating.json</c>：悬浮项偏好。</summary>
    floating = 1 << 4,

    /// <summary><c>configs/macros.json</c>：宏设置 + 元数据。</summary>
    macros = 1 << 5,

    /// <summary><c>actions/&lt;id&gt;/action.json</c>：动作目录（写回经 <c>ActionStore.SaveAll</c>）。</summary>
    actions = 1 << 6,

    /// <summary><c>configs/triggers.json</c>：自动触发规则（见 <c>docs/AUTO_TRIGGER.md</c>）。</summary>
    triggers = 1 << 7,

    all = settings | ui | hotkeys | panel | floating | macros | actions | triggers,
}
