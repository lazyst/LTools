using System;
using System.Collections.Generic;

namespace LTools.Features;

/// <summary>
/// 内部动作注册表（对应计划 §7）。<see cref="ActionType.@internal"/> 类型动作的
/// <see cref="ActionDto.Command"/> 在此查表分发到对应功能的入口方法。
/// 启动时由 App 调用 <see cref="RegisterDefaults"/> 注册 v1 的 6 个命令。
/// </summary>
/// <remarks>
/// 委托统一接收光标坐标：面板唤起时记录的光标位置（供 <c>windowPin.toggle</c> 使用），
/// 不关心坐标的命令忽略参数即可。
/// </remarks>
internal static class InternalActionRegistry
{
    /// <summary>命令键 → 入口委托（参数为光标坐标）。</summary>
    private static readonly Dictionary<string, Action<int, int>> _actions = new();

    /// <summary>注册 v1 的 6 个内部命令（由 App.OnStartup 调用一次）。</summary>
    public static void RegisterDefaults()
    {
        Register("quickNote.toggle", (_, _) => QuickNote.Toggle());
        Register("quickSearch.run", (_, _) => QuickSearch.Start());
        Register("magnifier.toggle", (_, _) => MiscKeys.ToggleMagnifier());
        Register("windowPin.toggle", (x, y) => WindowPin.ToggleAtCursor(x, y));
        Register("helpPanel.toggle", (_, _) => HelpPanel.Toggle());
        Register("settings.toggle", (_, _) => ConfigHelper.Toggle());
    }

    /// <summary>注册/覆盖一个内部命令。</summary>
    public static void Register(string command, Action<int, int> action) => _actions[command] = action;

    /// <summary>按命令键查找入口；找不到返回 false 且 action 为 null。</summary>
    public static bool TryGet(string command, out Action<int, int>? action) =>
        _actions.TryGetValue(command, out action);

    /// <summary>全部已注册命令键（供编辑器校验/展示）。</summary>
    public static IReadOnlyCollection<string> Commands => _actions.Keys;
}
