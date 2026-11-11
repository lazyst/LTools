using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 自动触发的运行期规则表：启动 / 保存后从 <see cref="TriggersFile"/> 重建，照
/// <see cref="HotkeyRegistry"/> 的「只读快照 + Rebuild」模式。
/// </summary>
/// <remarks>
/// <para>
/// 界面拿 <see cref="File"/>.Clone() 做编辑副本，改完 <see cref="Load(TriggersFile)"/>
/// 立即生效（不必重启），与快捷键页的 <c>HotkeysView.Save()</c> 一致。
/// </para>
/// <para>
/// <see cref="Active"/> 是「可参与排程 / 投递」的规则快照：总开关开、单条启用、有目标引用。
/// 规则顺序就是优先级（保留 <c>Rules</c> 的列表顺序），<see cref="TriggerScheduler"/> 与三个事件源
/// 都只读这份快照，不碰 <see cref="File"/> 里的可编辑对象。
/// </para>
/// </remarks>
internal static class TriggerRegistry
{
    private static TriggersFile _file = new();
    private static TriggerRule[] _active = Array.Empty<TriggerRule>();

    /// <summary>当前生效配置（界面直接读它再 Clone 编辑）。</summary>
    public static TriggersFile File => _file;

    /// <summary>全局总开关。</summary>
    public static bool Enabled => _file.Enabled;

    /// <summary>可参与排程 / 投递的规则（只读快照，按优先级顺序）。</summary>
    public static IReadOnlyList<TriggerRule> Active => _active;

    public static void Load(AppConfig cfg) => Load(cfg.Triggers ?? new TriggersFile());

    public static void Load(TriggersFile file)
    {
        _file = file ?? new TriggersFile();
        _file.Normalize();
        Rebuild();
    }

    /// <summary>
    /// 按当前 <see cref="File"/> 重建可投递规则表（保存配置后调用）。
    /// <c>Kind</c> 只用于**分流**：<see cref="TriggerScheduler"/> 排 <c>timer</c> / <c>startup</c>，
    /// 事件源投 <c>event</c>；这里不再按 Kind 过滤，事件规则也要在快照里（§13.3）。
    /// </summary>
    public static void Rebuild()
    {
        _active = _file.Enabled
            ? _file.Rules
                .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.ActionRef))
                .ToArray()
            : Array.Empty<TriggerRule>();
    }
}
