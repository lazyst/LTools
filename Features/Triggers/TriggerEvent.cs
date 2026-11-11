using System;
using System.Collections.Generic;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 一次系统事件的上下文（§13.3）。事件源只负责把它交给
/// <see cref="TriggerService.FireEvent"/>，不自己筛选规则、不自己调动作执行链。
/// </summary>
internal sealed class TriggerEvent
{
    /// <summary>事件类型键（<c>window.foreground</c> / <c>clipboard.change</c> / <c>file.change</c>）。</summary>
    public required string EventType { get; init; }

    /// <summary>发生时间（本地时间）。</summary>
    public DateTime OccurredAt { get; init; } = DateTime.Now;

    /// <summary>
    /// 事件实际字段，键与 <c>TriggerRule.Params</c> 对齐（如 <c>title</c> / <c>text</c> / <c>path</c>），
    /// 供 <see cref="EventMatcher"/> 做条件匹配。
    /// </summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>注入动作的事件输出变量（键 = 变量名，见 §13.4）。</summary>
    public IReadOnlyDictionary<string, Value> Values { get; init; } =
        new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 命中时的前台窗口快照（窗口切换事件用切换后的窗口）；
    /// null = 退回 <see cref="ForegroundAppCache.Current"/>（剪贴板 / 文件事件）。
    /// </summary>
    public ForegroundApp? Foreground { get; init; }

    /// <summary>
    /// 这次事件是不是 **LTools 自己**引起的（自己的窗口成为前台 / 自己写剪贴板）。
    /// <see cref="EventMatcher"/> 用它实现 `excludeOwn` / `ignoreOwnWrites` 两个默认开的开关，
    /// 避免动作注入键鼠 / 粘贴形成回环。文件事件恒为 false。
    /// </summary>
    public bool OwnEvent { get; init; }
}
