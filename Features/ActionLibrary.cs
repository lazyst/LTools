using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 全局动作清单的读写入口：面板 / 快捷键 / 自动触发 / 悬浮都只存动作 Id（旧菜单系统已移除）。
/// </summary>
/// <remarks>
/// 内存里就是 <see cref="ActionRegistry"/>；落盘走 <see cref="ConfigIO.Modify(string?, ConfigDomain, Action{AppConfig})"/>
/// 只写动作目录，不再顺带重写别的域（旧 <c>MenuSystem.SaveToConfig</c> 会连菜单组一起写）。
/// </remarks>
internal static class ActionLibrary
{
    /// <summary>从配置聚合里重置内存清单（启动 / 重新加载）。</summary>
    public static void LoadFrom(AppConfig cfg)
    {
        ActionRegistry.Clear();
        if (cfg.Actions != null) ActionRegistry.RegisterAll(cfg.Actions);
    }

    public static ActionDto? Find(string id) => ActionRegistry.FindById(id);

    public static void Add(ActionDto action) => ActionRegistry.Register(action);

    public static void Replace(ActionDto action) => ActionRegistry.Register(action);

    public static bool Remove(string id) => ActionRegistry.Remove(id);

    /// <summary>把当前清单按 Id 排序后写回 <c>actions/</c>（内容没变的目录不会重写）。</summary>
    public static void Save(string configPath)
    {
        var snapshot = new List<ActionDto>(ActionRegistry.All.OrderBy(a => a.Id, StringComparer.Ordinal));
        ConfigIO.Modify(configPath, ConfigDomain.actions, cfg => cfg.Actions = snapshot);
    }
}
