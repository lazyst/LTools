using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using LTools.Core.Actions;

namespace LTools.Features;

/// <summary>鼠标动作的子类型（旧 <c>mouse</c> 动作 / 新「鼠标操作」动作单元）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseActionKind
{
    /// <summary>左键单击。</summary>
    click,

    /// <summary>左键双击。</summary>
    doubleClick,

    /// <summary>右键单击。</summary>
    rightClick,

    /// <summary>中键单击。</summary>
    middleClick,

    /// <summary>只移动光标，不点击。</summary>
    move,

    /// <summary>滚轮向上（<see cref="ActionDto.WheelDelta"/> 个刻度，默认 120）。</summary>
    wheelUp,

    /// <summary>滚轮向下。</summary>
    wheelDown,
}

/// <summary>窗口操作子类型（旧 <c>window</c> 动作 / 新「窗口操作」动作单元）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WindowActionKind
{
    /// <summary>切到前台（最小化的先还原；系统前台锁会兜底处理）。</summary>
    activate,

    /// <summary>最大化。</summary>
    maximize,

    /// <summary>最小化。</summary>
    minimize,

    /// <summary>还原（取消最大化 / 最小化）。</summary>
    restore,

    /// <summary>调整到 <see cref="ActionDto.WindowRect"/> 指定的大小与位置（物理像素）。</summary>
    resize,
}

/// <summary>sendText 发送方式（§3.2.2）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SendTextMode
{
    /// <summary>自动：单行 → Unicode 注入（不碰剪贴板）；含换行 → 剪贴板粘贴（默认）。</summary>
    auto,

    /// <summary>强制模拟键入（非 ASCII 字符无法用 VkKeyScanW 映射，执行时预检报错，不静默粘贴）。</summary>
    type,

    /// <summary>强制剪贴板粘贴（写剪贴板 → Ctrl+V → 150ms 后整块还原原剪贴板）。</summary>
    paste,
}

/// <summary>步骤失败策略（对应计划 §4）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OnFailStrategy
{
    /// <summary>继续执行下一步（默认）。</summary>
    @continue,

    /// <summary>中止整个动作。</summary>
    abort,
}

/// <summary>窗口操作的中文显示名（编辑器下拉 / 步骤摘要共用）。</summary>
public static class WindowActionLabel
{
    /// <summary>完整名称（编辑器下拉）。</summary>
    public static string Of(WindowActionKind kind) => kind switch
    {
        WindowActionKind.activate => "切换到前台",
        WindowActionKind.maximize => "最大化",
        WindowActionKind.minimize => "最小化",
        WindowActionKind.restore => "还原",
        WindowActionKind.resize => "调整到指定大小",
        _ => kind.ToString(),
    };

    /// <summary>步骤行摘要用的短名称。</summary>
    public static string Short(WindowActionKind kind) => kind switch
    {
        WindowActionKind.activate => "切到前台",
        WindowActionKind.maximize => "最大化",
        WindowActionKind.minimize => "最小化",
        WindowActionKind.restore => "还原",
        WindowActionKind.resize => "改尺寸",
        _ => kind.ToString(),
    };
}

/// <summary>
/// 动作数据模型（对应计划 §2）。一个 Action = 全局动作清单中的一项，
/// 超级面板格子、快捷键绑定与悬浮项都只引用其 <see cref="Id"/>。
/// </summary>
/// <remarks>
/// 动作只有一种形状：<see cref="Program"/>（变量表 + 步骤）。键鼠宏也不例外——
/// 一个宏就是一个「1 步 · 回放宏」的动作（<c>input.macro</c>），宏的事件本体在
/// <c>macros/&lt;id&gt;.json</c>，由步骤的 <c>macroId</c> 参数引用。
/// </remarks>
public sealed class ActionDto
{
    /// <summary>稳定标识，如 "a1"、"cmd1"。编辑器自动生成，用户不可见。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>内置图标 Id（见 <see cref="IconCatalog"/>），可为空。</summary>
    public string? Icon { get; set; }

    /// <summary>动作体（变量表 + 步骤）。</summary>
    public ActionProgram? Program { get; set; }

    /// <summary>深拷贝（含动作体深拷贝）。</summary>
    public ActionDto Clone()
    {
        return new ActionDto
        {
            Id = Id,
            Name = Name,
            Icon = Icon,
            Program = Program?.Clone(),
        };
    }
}
