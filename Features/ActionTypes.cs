using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace LTools.Features;

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
    sendText,
    sendKeys,
    @internal,
    composite,
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

/// <summary>动作类型的中文显示名（UI 共用）。</summary>
public static class ActionTypeLabel
{
    /// <summary>取得中文类型名。</summary>
    public static string Of(ActionType t) => t switch
    {
        ActionType.launchApp => "启动软件",
        ActionType.openFile => "打开文件",
        ActionType.openFolder => "打开文件夹",
        ActionType.openUrl => "打开网址",
        ActionType.runCommand => "运行命令",
        ActionType.sendText => "发送文本",
        ActionType.sendKeys => "模拟按键",
        ActionType.@internal => "内部动作",
        ActionType.composite => "组合动作",
        _ => t.ToString(),
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

    // —— sendText（§3.2.2）——

    /// <summary>sendText: 要发送的多行文本。</summary>
    public string? Text { get; set; }

    /// <summary>sendText: 发送方式，null 视为 <see cref="SendTextMode.auto"/>。</summary>
    public SendTextMode? Mode { get; set; }

    /// <summary>sendText: 文本发送后是否在末尾追加一次回车。</summary>
    public bool? AppendEnter { get; set; }

    // —— sendKeys（§3.2.1）——

    /// <summary>sendKeys: 有序输入条目列表（chord / text / sleep 三种）。</summary>
    public List<KeyItem>? Items { get; set; }

    // —— composite ——

    /// <summary>composite: 有序步骤列表（nullable 以便 JSON 省略非 composite 动作的空列表）。</summary>
    public List<StepDto>? Steps { get; set; }

    /// <summary>
    /// composite: 组合级默认失败策略（对应 §4「组合可有整体默认」）。
    /// 步骤未显式设置 <see cref="StepDto.OnFail"/> 时使用此值；也为 null 则视为 continue。
    /// </summary>
    public OnFailStrategy? CompositeOnFail { get; set; }

    /// <summary>深拷贝（含 Steps 递归）；用于把动作池动作复制成组合步骤的独立快照（§12 动作解耦）。</summary>
    public ActionDto Clone() => new()
    {
        Id = Id, Name = Name, Icon = Icon, Type = Type,
        Target = Target, Args = Args, Workdir = Workdir, Path = Path, Url = Url,
        Cmd = Cmd, Terminal = Terminal, KeepWindow = KeepWindow, Command = Command,
        Text = Text, Mode = Mode, AppendEnter = AppendEnter,
        Items = Items?.Select(i => i.Clone()).ToList(),
        CompositeOnFail = CompositeOnFail,
        Steps = Steps?.Select(s => new StepDto { Action = s.Action.Clone(), DelayMs = s.DelayMs, OnFail = s.OnFail }).ToList(),
    };
}
