using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using LTools.Core.Actions;
using LTools.Features.Migration.Legacy;

namespace LTools.Features;

/// <summary>
/// 动作类型（新格式只有两种）。JSON 中以小写字符串存储。
/// </summary>
/// <remarks>
/// <para>
/// v1 的类型名（<c>launchApp</c> / <c>runCommand</c> / <c>sendText</c> / <c>internal</c> …）
/// 不再进枚举，而是留在 <see cref="ActionDto.LegacyType"/>（由 JSON 的 <c>type</c> 读入），
/// 只给迁移器与旧动作摘要用。读旧配置时这些动作在内存里按 <see cref="composite"/> 对待，
/// 第一次执行 / 编辑后由 <see cref="Features.Migration.ActionPrograms"/> 迁移成
/// <see cref="ActionProgram"/> 并丢掉旧字段。
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionType
{
    /// <summary>动作体（<see cref="ActionDto.Program"/>）：新建动作、迁移后的旧动作都是它。</summary>
    composite,

    /// <summary>「一个键鼠宏就是一个动作」的顶层宏动作（事件本体在 <c>macros/&lt;id&gt;.json</c>）。</summary>
    macro,
}

/// <summary>鼠标动作的子类型（旧 <c>mouse</c> 动作 / 新「鼠标操作」单元）。</summary>
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

/// <summary>窗口操作子类型（旧 <c>window</c> 动作 / 新「窗口操作」单元）。</summary>
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
    /// <summary>自动：纯 ASCII 且 ≤100 字符 → 键入；否则剪贴板粘贴（默认）。</summary>
    auto,

    /// <summary>强制模拟键入（非 ASCII 字符无法用 VkKeyScanW 映射，执行时预检报错，不静默粘贴）。</summary>
    type,

    /// <summary>强制剪贴板粘贴（写剪贴板 → Ctrl+V → 150ms 后恢复原剪贴板文本，§12 修订）。</summary>
    paste,
}

/// <summary>组合步骤失败策略（对应计划 §4）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OnFailStrategy
{
    /// <summary>继续执行下一步（默认）。</summary>
    @continue,

    /// <summary>中止整个组合。</summary>
    abort,
}

/// <summary>sendKeys 序列条目类型（§3.2.1）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyItemKind
{
    /// <summary>组合键 / 按键序列（按住修饰键依次发主键，如 ctrl+k、alt+p）。</summary>
    chord,

    /// <summary>内联短文本条目（表单填充中一格；执行复用 sendText 的 auto 逻辑）。</summary>
    text,

    /// <summary>延时等待（毫秒）。</summary>
    sleep,
}

/// <summary>sendKeys 动作内的一条输入条目（§3.2.1）。</summary>
public sealed class KeyItem
{
    /// <summary>条目类型。</summary>
    public KeyItemKind Kind { get; set; }

    /// <summary>chord: 按键序列，每项形如 "ctrl+k"、"alt+p"、"s"、"f5"（修饰键组+主键，或无修饰单键）。</summary>
    public List<string>? Strokes { get; set; }

    /// <summary>text: 内联短文本。</summary>
    public string? Text { get; set; }

    /// <summary>sleep: 毫秒数。</summary>
    public int Ms { get; set; }

    /// <summary>深拷贝。</summary>
    public KeyItem Clone() => new()
    {
        Kind = Kind,
        Strokes = Strokes != null ? new List<string>(Strokes) : null,
        Text = Text,
        Ms = Ms,
    };
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

/// <summary>组合动作的单步（对应计划 §4）。</summary>
/// <remarks>
/// 步骤**内嵌**一个 <see cref="Action"/>（深拷贝快照），不再以 <c>ActionId</c> 引用动作池——
/// 组合动作与动作池**解耦**：编辑/删除池动作不影响已复制的步骤（§12「动作解耦」决策）。
/// <c>Action</c> 禁止为 <see cref="ActionType.composite"/>（组合不支持嵌套，编辑器与执行器双侧拦）。
/// </remarks>
public sealed class StepDto
{
    /// <summary>本步骤内嵌的动作快照。</summary>
    public ActionDto Action { get; set; } = new();

    /// <summary>执行本步前等待的毫秒数（默认 0）。</summary>
    public int DelayMs { get; set; }

    /// <summary>
    /// 本步失败后的策略。null = 用组合级默认 <see cref="ActionDto.CompositeOnFail"/>，
    /// 组合也未设则视为 <see cref="OnFailStrategy.@continue"/>。
    /// </summary>
    public OnFailStrategy? OnFail { get; set; }
}

/// <summary>
/// 动作数据模型（对应计划 §2）。一个 Action = 全局动作清单中的一项，
/// 超级面板格子与 CapsLock+数字菜单项都只引用其 <see cref="Id"/>。
/// </summary>
/// <remarks>
/// <para>
/// 新动作只保存 <see cref="Program"/>（变量表 + 步骤 + 动作内子动作）。
/// 旧格式的类型专属字段（<c>target</c> / <c>cmd</c> / <c>text</c> / <c>steps</c> / <c>flow</c> …）
/// 已搬到 <see cref="Features.Migration.Legacy.ActionDtoLegacy"/>，由本类继承后仍是 JSON 扁平字段，
/// 只给 <see cref="Features.Migration.ActionPrograms"/> 的懒迁移读。
/// </para>
/// <para>
/// 新建动作一律写 <see cref="ActionType.composite"/> + <see cref="Program"/>；
/// <see cref="ActionType.macro"/> 保留给「一个键鼠宏就是一个动作」的顶层宏动作。
/// </para>
/// </remarks>
public sealed class ActionDto : ActionDtoLegacy
{
    /// <summary>稳定标识，如 "a1"、"cmd1"。编辑器自动生成，用户不可见。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>内置图标 Id（见 <see cref="IconCatalog"/>），可为空。</summary>
    public string? Icon { get; set; }

    /// <summary>
    /// 动作类型。新动作是 <see cref="ActionType.composite"/>（宏动作是 <see cref="ActionType.macro"/>）。
    /// 旧配置的 v1 类型名不在这里，见 <see cref="RawType"/> / <see cref="LegacyType"/>。
    /// </summary>
    [JsonIgnore]
    public ActionType Type { get; set; }

    /// <summary>
    /// JSON 里的 <c>Type</c>：新动作写 <c>composite</c> / <c>macro</c>；
    /// 旧配置读到的 v1 类型名（<c>runCommand</c> / <c>sendText</c> …）原样留在
    /// <see cref="ActionDtoLegacy.LegacyType"/>，迁移器按它映射，回写时也写回原值。
    /// </summary>
    [JsonPropertyName("Type")]
    [JsonConverter(typeof(LegacyTypeNameConverter))]
    public string RawType
    {
        get => LegacyType ?? Type.ToString();
        set
        {
            string raw = (value ?? "").Trim();
            if (raw.Equals("macro", StringComparison.OrdinalIgnoreCase))
            {
                LegacyType = null;
                Type = ActionType.macro;
            }
            else if (raw.Length == 0 || raw.Equals("composite", StringComparison.OrdinalIgnoreCase))
            {
                LegacyType = null;
                Type = ActionType.composite;
            }
            else
            {
                // v1 类型名：先按「组合动作」对待（它会被迁移成程序），原字符串留给迁移器。
                LegacyType = raw;
                Type = ActionType.composite;
            }
        }
    }

    /// <summary>
    /// 迁移 / 旧动作摘要用的类型名：旧动作是 v1 的 <c>type</c> 字符串，
    /// 新动作是 <c>composite</c> / <c>macro</c>。
    /// </summary>
    [JsonIgnore]
    public string SourceType => LegacyType ?? Type.ToString();

    // —— macro（录制宏）——

    /// <summary>macro: 宏 Id（事件本体存 <c>macros/&lt;id&gt;.json</c>）。</summary>
    public string? MacroId { get; set; }

    /// <summary>
    /// 动作体（schema v2：变量表 + 步骤 + 动作内子动作）。为 null = 旧格式，
    /// 由 <c>Features.Migration.ActionPrograms</c> 按需迁移；非 null 时执行走新引擎。
    /// </summary>
    public ActionProgram? Program { get; set; }

    /// <summary>深拷贝（含 Program 与旧字段的深拷贝）。</summary>
    public ActionDto Clone()
    {
        var clone = new ActionDto
        {
            Id = Id,
            Name = Name,
            Icon = Icon,
            Type = Type,
            MacroId = MacroId,
            Program = Program?.Clone(),
        };
        CopyLegacyTo(clone);
        return clone;
    }
}
