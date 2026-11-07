using System;
using System.Collections.Generic;

namespace LTools.Features;

/// <summary>
/// 内部动作注册表（对应计划 §7）。v1 配置里 <c>type = "internal"</c> 的动作，迁移后变成「内部动作」单元。
/// <see cref="ActionDto.Command"/> 在此查表分发到对应功能的入口方法。
/// 启动时由 App 调用 <see cref="RegisterDefaults"/> 注册内置命令。
/// </summary>
/// <remarks>
/// 委托统一接收光标坐标：面板唤起时记录的光标位置（供 <c>windowPin.toggle</c> 使用），
/// 不关心坐标的命令忽略参数即可。
/// <para>
/// v2.3.3 起，以下命令不再动作化（它们不是「可重复执行的操作」，而是工具入口 / 创作会话）：
/// <c>settings.toggle</c>、<c>helpPanel.toggle</c>（全局入口：CapsLock+\ / CapsLock+` / 托盘 /
/// 超级面板标题栏齿轮）、<c>macro.recordToggle</c>、<c>macro.manage</c>（录制与宏管理：托盘 /
/// 动作编辑器 / 组合动作左栏；宏管理入口已彻底移除）。放进动作池只会让面板和菜单多出重复项，
/// 旧配置中的这些动作由启动迁移清理。内置命令现只剩真正「执行一次就产生效果」的 4 个。
/// </para>
/// </remarks>
internal static class InternalActionRegistry
{
    /// <summary>命令键 → 入口委托（参数为光标坐标）。</summary>
    private static readonly Dictionary<string, Action<int, int>> _actions = new();

    /// <summary>注册内置内部命令（由 App.OnStartup 调用一次）。</summary>
    public static void RegisterDefaults()
    {
        Register("quickNote.toggle", (_, _) => QuickNote.Toggle());
        Register("quickSearch.run", (_, _) => QuickSearch.Start());
        Register("magnifier.toggle", (_, _) => MiscKeys.ToggleMagnifier());
        Register("windowPin.toggle", (x, y) => WindowPin.ToggleAtCursor(x, y));
    }

    /// <summary>注册/覆盖一个内部命令。</summary>
    public static void Register(string command, Action<int, int> action) => _actions[command] = action;

    /// <summary>按命令键查找入口；找不到返回 false 且 action 为 null。</summary>
    public static bool TryGet(string command, out Action<int, int>? action) =>
        _actions.TryGetValue(command, out action);

    /// <summary>全部已注册命令键（供编辑器校验/展示）。</summary>
    public static IReadOnlyCollection<string> Commands => _actions.Keys;
}
