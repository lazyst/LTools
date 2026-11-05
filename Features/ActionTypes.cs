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
    setClipboard,

    /// <summary>提示弹窗：把一段文字用置顶提示卡显示出来，**不阻塞**后续步骤。</summary>
    toast,

    /// <summary>窗口操作：切到前台 / 最大化 / 最小化 / 还原 / 调整到指定大小。</summary>
    window,

    /// <summary>文字点击：在指定区域里识别文字，找到指定的文字就点它（见 <see cref="TextClickAction"/>）。</summary>
    clickText,

    /// <summary>图片点击：在指定区域里找模板图，找到就点它（见 <see cref="ImageClickAction"/>）。</summary>
    imageClick,

    sendKeys,
    mouse,
    macro,
    @internal,
    composite,
}

/// <summary>鼠标动作的子类型（<see cref="ActionType.mouse"/>）。</summary>
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

/// <summary>窗口操作子类型（<see cref="ActionType.window"/>）。</summary>
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

/// <summary>动作类型的中文显示名（UI 共用）。</summary>
public static class ActionTypeLabel
{
    /// <summary>取得中文类型名。</summary>
    public static string Of(ActionType t) => t switch
    {
        ActionType.launchApp => "启动程序",
        ActionType.openFile => "打开文件",
        ActionType.openFolder => "打开文件夹",
        ActionType.openUrl => "打开网址",
        ActionType.runCommand => "运行命令",
        ActionType.sendText => "发送文本",
        ActionType.setClipboard => "写入剪贴板",
        ActionType.toast => "提示弹窗",
        ActionType.window => "窗口操作",
        ActionType.clickText => "文字点击",
        ActionType.imageClick => "图片点击",
        ActionType.sendKeys => "模拟按键",
        ActionType.mouse => "鼠标操作",
        ActionType.macro => "键鼠宏",
        ActionType.@internal => "内部动作",
        ActionType.composite => "组合动作",
        _ => t.ToString(),
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

    /// <summary>launchApp / runCommand: 工作目录（空则默认应用目录）。</summary>
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

    // —— sendText（§3.2.2）/ setClipboard ——

    /// <summary>sendText / setClipboard / clickText: 文本内容（支持 ${变量}）；clickText 时是「要点击的文字」。</summary>
    public string? Text { get; set; }

    /// <summary>sendText: 发送方式，null 视为 <see cref="SendTextMode.auto"/>。</summary>
    public SendTextMode? Mode { get; set; }

    /// <summary>sendText: 文本发送后是否在末尾追加一次回车。</summary>
    public bool? AppendEnter { get; set; }

    // —— toast（提示弹窗）——

    /// <summary>toast: 停留时长（ms，null = 4000；0 = 不自动关闭，点一下关掉）。</summary>
    public int? DurationMs { get; set; }

    // —— window（窗口操作）——

    /// <summary>window: 目标窗口标题包含的文本（空 = 不按标题过滤，支持 ${变量}）。</summary>
    public string? WindowTitle { get; set; }

    /// <summary>window: 目标窗口类名（精确匹配，一般留空；同名窗口分不清时才填）。</summary>
    public string? WindowClass { get; set; }

    /// <summary>window: 操作，null 视为 <see cref="WindowActionKind.activate"/>。</summary>
    public WindowActionKind? WindowOp { get; set; }

    /// <summary>window: <see cref="WindowActionKind.resize"/> 的目标矩形 [x, y, w, h]（虚拟桌面物理像素）。</summary>
    public int[]? WindowRect { get; set; }

    // —— clickText（文字点击）/ imageClick（图片点击）——

    /// <summary>clickText / imageClick: 文字所在区域 / 模板图搜索区域；null = 前台窗口（与「截图找图」同一套区域语义）。</summary>
    public ImageRegion? Region { get; set; }

    /// <summary>clickText / imageClick: <see cref="ImageRegion.rect"/> 时的区域 [x, y, w, h]（虚拟桌面物理像素）。</summary>
    public int[]? Rect { get; set; }

    /// <summary>clickText: 识别引擎（<c>null</c> / <c>windows</c> = 系统 OCR；<c>v6tiny</c> / <c>v6small</c> = PP-OCRv6）。</summary>
    public string? OcrEngine { get; set; }

    /// <summary>clickText / imageClick: 等目标出现的最长时间（ms，0 = 只找一次）。</summary>
    public int? TimeoutMs { get; set; }

    // —— imageClick（图片点击）——

    /// <summary>
    /// imageClick: 模板图片路径（语义同「截图找图」：resources/images 下的文件名、
    /// resources\user 的数据根相对路径，或绝对路径）。
    /// </summary>
    public string? Image { get; set; }

    /// <summary>imageClick: 最低相似度（0..1；null = 0.85）。</summary>
    public double? MinScore { get; set; }

    // —— sendKeys（§3.2.1）——

    /// <summary>sendKeys: 有序输入条目列表（chord / text / sleep 三种）。</summary>
    public List<KeyItem>? Items { get; set; }

    // —— mouse（鼠标操作）——

    /// <summary>mouse: 目标 X 坐标（支持 ${变量} 模板）。</summary>
    public string? X { get; set; }

    /// <summary>mouse: 目标 Y 坐标（支持 ${变量} 模板）。</summary>
    public string? Y { get; set; }

    /// <summary>mouse: 鼠标动作，null 视为单击。</summary>
    public MouseActionKind? MouseAction { get; set; }

    /// <summary>mouse: 滚轮刻度（默认 120）。</summary>
    public int? WheelDelta { get; set; }

    // —— macro（录制宏）——

    /// <summary>macro: 宏 Id（事件本体存 <c>macros/&lt;id&gt;.json</c>）。</summary>
    public string? MacroId { get; set; }

    // —— composite ——

    /// <summary>composite: 有序步骤列表（nullable 以便 JSON 省略非 composite 动作的空列表）。</summary>
    public List<StepDto>? Steps { get; set; }

    /// <summary>
    /// composite: 组合级默认失败策略（对应 §4「组合可有整体默认」）。
    /// 步骤未显式设置 <see cref="StepDto.OnFail"/> 时使用此值；也为 null 则视为 continue。
    /// </summary>
    public OnFailStrategy? CompositeOnFail { get; set; }

    /// <summary>
    /// composite: 自动化流程（嵌套块）。旧配置只有 <see cref="Steps"/> 时由
    /// <see cref="FlowMigration"/> 在加载 / 执行 / 编辑时迁移，保存后写 <see cref="Flow"/> 并清空 Steps。
    /// </summary>
    public List<FlowNode>? Flow { get; set; }

    // —— 输出（组合动作步骤）——

    /// <summary>
    /// 把执行结果存入流程变量（仅组合动作步骤使用；空 = 不输出）。
    /// 支持的类型与结果内容见 <see cref="ActionResults"/>。
    /// </summary>
    public string? OutputVar { get; set; }

    /// <summary>深拷贝（含 Steps / Flow 递归）；用于把动作池动作复制成组合步骤的独立快照（§12 动作解耦）。</summary>
    public ActionDto Clone() => new()
    {
        Id = Id, Name = Name, Icon = Icon, Type = Type,
        Target = Target, Args = Args, Workdir = Workdir, Path = Path, Url = Url,
        Cmd = Cmd, Terminal = Terminal, KeepWindow = KeepWindow, Command = Command,
        Text = Text, Mode = Mode, AppendEnter = AppendEnter, DurationMs = DurationMs,
        WindowTitle = WindowTitle, WindowClass = WindowClass, WindowOp = WindowOp,
        WindowRect = WindowRect != null ? (int[])WindowRect.Clone() : null,
        Region = Region, Rect = Rect != null ? (int[])Rect.Clone() : null,
        OcrEngine = OcrEngine, TimeoutMs = TimeoutMs,
        Image = Image, MinScore = MinScore,
        Items = Items?.Select(i => i.Clone()).ToList(),
        X = X, Y = Y, MouseAction = MouseAction, WheelDelta = WheelDelta,
        MacroId = MacroId,
        OutputVar = OutputVar,
        CompositeOnFail = CompositeOnFail,
        Steps = Steps?.Select(s => new StepDto { Action = s.Action.Clone(), DelayMs = s.DelayMs, OnFail = s.OnFail }).ToList(),
        Flow = Flow != null ? FlowNode.CloneList(Flow) : null,
    };
}
