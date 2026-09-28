using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CapsLockPro.Features;

/// <summary>
/// 动作类型枚举（对应计划 §3 动作类型表）。JSON 中以小写字符串存储。
/// </summary>
/// <remarks>
/// 成员名与 JSON schema 一致；<c>@internal</c> 的 <c>@</c> 仅转义 C# 关键字，
/// 运行时成员名为 <c>internal</c>，<see cref="JsonStringEnumConverter"/> 序列化为 <c>"internal"</c>。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionType
{
    launchApp,
    openFile,
    openFolder,
    openUrl,
    runCommand,
    @internal,
    composite,
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

/// <summary>组合动作的单步（引用另一个动作 Id，对应计划 §4）。</summary>
public sealed class StepDto
{
    /// <summary>引用的动作 Id（可为另一个 composite）。</summary>
    public string ActionId { get; set; } = "";

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
/// 类型特定字段平铺存放（discriminated union by <see cref="Type"/>），
/// 不使用的字段保持默认值，便于 System.Text.Json 序列化。
/// 字段对应关系见计划 §3 动作类型表。
/// </remarks>
public sealed class ActionDto
{
    /// <summary>稳定标识，如 "a1"、"cmd1"。编辑器自动生成，用户不可见。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>内置图标 Id（见 <see cref="IconCatalog"/>），可为空。</summary>
    public string? Icon { get; set; }

    /// <summary>动作类型，决定使用哪些字段。</summary>
    public ActionType Type { get; set; }

    // —— launchApp / runCommand 共用 ——

    /// <summary>launchApp: 目标 exe 路径。</summary>
    public string? Target { get; set; }

    /// <summary>launchApp: 命令行参数（可选）。</summary>
    public string? Args { get; set; }

    /// <summary>launchApp / runCommand: 工作目录（空则默认桌面）。</summary>
    public string? Workdir { get; set; }

    // —— openFile / openFolder ——

    /// <summary>openFile / openFolder: 路径。</summary>
    public string? Path { get; set; }

    // —— openUrl ——

    /// <summary>openUrl: URL。</summary>
    public string? Url { get; set; }

    // —— runCommand ——

    /// <summary>runCommand: 命令字符串。</summary>
    public string? Cmd { get; set; }

    /// <summary>runCommand: 终端 key（direct/pwsh7/pwsh5/cmd/gitbash/wslbash/wt）。</summary>
    public string? Terminal { get; set; }

    /// <summary>runCommand: 执行完是否保持终端窗口。</summary>
    public bool? KeepWindow { get; set; }

    // —— internal ——

    /// <summary>internal: 内部命令键（如 "quickNote.toggle"，见 <see cref="InternalActionRegistry"/>）。</summary>
    public string? Command { get; set; }

    // —— composite ——

    /// <summary>composite: 有序步骤列表（nullable 以便 JSON 省略非 composite 动作的空列表）。</summary>
    public List<StepDto>? Steps { get; set; }

    /// <summary>
    /// composite: 组合级默认失败策略（对应 §4「组合可有整体默认」）。
    /// 步骤未显式设置 <see cref="StepDto.OnFail"/> 时使用此值；也为 null 则视为 continue。
    /// </summary>
    public OnFailStrategy? CompositeOnFail { get; set; }
}
