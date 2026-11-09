using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using LTools.Features;

namespace LTools.Features.Migration.Legacy;

/// <summary>
/// 旧动作（schema v1）的类型专属字段。新动作只保存 <see cref="ActionDto.Program"/>，
/// 这些字段**只给迁移器 / 旧实现读历史 JSON 用**，新代码不要再写。
/// </summary>
/// <remarks>
/// <para>
/// 字段保持「按旧的 <c>type</c> 平铺」的老结构，由 <see cref="ActionDto"/> 继承后仍是
/// JSON 扁平字段——旧配置不需要改写就能读进来。
/// </para>
/// <para>
/// 迁移完成后动作只写 <c>program</c>；这些字段为 null 时不会出现在 JSON 里。
/// </para>
/// </remarks>
public abstract class ActionDtoLegacy
{
    /// <summary>
    /// v1 配置里的 <c>type</c> 字符串（<c>runCommand</c> / <c>sendText</c> …）。
    /// 由 <see cref="ActionDto.RawType"/> 在反序列化时填充；新动作 / 已迁移动作为 null。
    /// </summary>
    [JsonIgnore]
    public string? LegacyType { get; set; }

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

    /// <summary>internal: 内部命令键（如 "quickNote.toggle"，见「LTools 内置功能」单元）。</summary>
    public string? Command { get; set; }

    // —— sendText / setClipboard ——

    /// <summary>sendText / setClipboard / clickText: 文本内容（支持 ${变量}）；clickText 时是「要点击的文字」。</summary>
    public string? Text { get; set; }

    /// <summary>sendText: 发送方式，null 视为 <see cref="SendTextMode.auto"/>。</summary>
    public SendTextMode? Mode { get; set; }

    /// <summary>sendText: 文本发送后是否在末尾追加一次回车。</summary>
    public bool? AppendEnter { get; set; }

    // —— toast ——

    /// <summary>toast: 停留时长（ms，null = 4000；0 = 不自动关闭，点一下关掉）。</summary>
    public int? DurationMs { get; set; }

    // —— quickNote ——

    /// <summary>quickNote: 标题（支持 ${变量}；留空则用正文第一行）。</summary>
    public string? NoteTitle { get; set; }

    /// <summary>quickNote: 分类（支持 ${变量}；留空 = 未分类；不存在时自动创建）。</summary>
    public string? NoteCategory { get; set; }

    // —— window ——

    /// <summary>window: 目标窗口标题包含的文本（空 = 不按标题过滤，支持 ${变量}）。</summary>
    public string? WindowTitle { get; set; }

    /// <summary>window: 目标窗口类名（精确匹配，一般留空；同名窗口分不清时才填）。</summary>
    public string? WindowClass { get; set; }

    /// <summary>window: 操作，null 视为 <see cref="WindowActionKind.activate"/>。</summary>
    public WindowActionKind? WindowOp { get; set; }

    /// <summary>window: <see cref="WindowActionKind.resize"/> 的目标矩形 [x, y, w, h]（虚拟桌面物理像素）。</summary>
    public int[]? WindowRect { get; set; }

    // —— clickText / imageClick ——

    /// <summary>clickText / imageClick: 搜索区域；null = 前台窗口（与「截图找图」同一套区域语义）。</summary>
    public ImageRegion? Region { get; set; }

    /// <summary>clickText / imageClick: <see cref="ImageRegion.rect"/> 时的区域 [x, y, w, h]（虚拟桌面物理像素）。</summary>
    public int[]? Rect { get; set; }

    /// <summary>clickText: 识别引擎（<c>null</c> / <c>windows</c> = 系统 OCR；<c>v6tiny</c> / <c>v6small</c> = PP-OCRv6）。</summary>
    public string? OcrEngine { get; set; }

    /// <summary>clickText / imageClick: 等目标出现的最长时间（ms，0 = 只找一次）。</summary>
    public int? TimeoutMs { get; set; }

    // —— imageClick ——

    /// <summary>imageClick: 模板图片路径（语义同「截图找图」）。</summary>
    public string? Image { get; set; }

    /// <summary>imageClick: 最低相似度（0..1；null = 0.85）。</summary>
    public double? MinScore { get; set; }

    // —— sendKeys ——

    /// <summary>sendKeys: 有序输入条目列表（chord / text / sleep 三种）。</summary>
    public List<KeyItem>? Items { get; set; }

    // —— mouse ——

    /// <summary>mouse: 目标 X 坐标（支持 ${变量} 模板）。</summary>
    public string? X { get; set; }

    /// <summary>mouse: 目标 Y 坐标（支持 ${变量} 模板）。</summary>
    public string? Y { get; set; }

    /// <summary>mouse: 鼠标动作，null 视为单击。</summary>
    public MouseActionKind? MouseAction { get; set; }

    /// <summary>mouse: 滚轮刻度（默认 120）。</summary>
    public int? WheelDelta { get; set; }

    // —— composite（旧步骤 / 旧流程）——

    /// <summary>composite: 线性步骤列表（旧版；由 <see cref="FlowMigration"/> 升级成 <see cref="Flow"/>）。</summary>
    public List<StepDto>? Steps { get; set; }

    /// <summary>composite: 组合级默认失败策略。</summary>
    public OnFailStrategy? CompositeOnFail { get; set; }

    /// <summary>composite: 自动化流程（嵌套块），旧配置的最终形态。</summary>
    public List<FlowNode>? Flow { get; set; }

    /// <summary>组合步骤：把执行结果存入流程变量（空 = 不输出）。</summary>
    public string? OutputVar { get; set; }

    /// <summary>复制全部旧字段（含数组 / 列表的深拷贝）。</summary>
    public void CopyLegacyTo(ActionDtoLegacy target)
    {
        target.LegacyType = LegacyType;
        target.Target = Target;
        target.Args = Args;
        target.Workdir = Workdir;
        target.Path = Path;
        target.Url = Url;
        target.Cmd = Cmd;
        target.Terminal = Terminal;
        target.KeepWindow = KeepWindow;
        target.Command = Command;
        target.Text = Text;
        target.Mode = Mode;
        target.AppendEnter = AppendEnter;
        target.DurationMs = DurationMs;
        target.NoteTitle = NoteTitle;
        target.NoteCategory = NoteCategory;
        target.WindowTitle = WindowTitle;
        target.WindowClass = WindowClass;
        target.WindowOp = WindowOp;
        target.WindowRect = WindowRect != null ? (int[])WindowRect.Clone() : null;
        target.Region = Region;
        target.Rect = Rect != null ? (int[])Rect.Clone() : null;
        target.OcrEngine = OcrEngine;
        target.TimeoutMs = TimeoutMs;
        target.Image = Image;
        target.MinScore = MinScore;
        target.Items = Items?.Select(i => i.Clone()).ToList();
        target.X = X;
        target.Y = Y;
        target.MouseAction = MouseAction;
        target.WheelDelta = WheelDelta;
        target.OutputVar = OutputVar;
        target.CompositeOnFail = CompositeOnFail;
        target.Steps = Steps?.Select(s => new StepDto
        {
            Action = s.Action.Clone(),
            DelayMs = s.DelayMs,
            OnFail = s.OnFail,
        }).ToList();
        target.Flow = Flow != null ? FlowNode.CloneList(Flow) : null;
    }

    /// <summary>迁移成功后清掉旧字段（动作从此只写 <c>program</c>）。</summary>
    public void ClearLegacy()
    {
        LegacyType = null;
        Target = null;
        Args = null;
        Workdir = null;
        Path = null;
        Url = null;
        Cmd = null;
        Terminal = null;
        KeepWindow = null;
        Command = null;
        Text = null;
        Mode = null;
        AppendEnter = null;
        DurationMs = null;
        NoteTitle = null;
        NoteCategory = null;
        WindowTitle = null;
        WindowClass = null;
        WindowOp = null;
        WindowRect = null;
        Region = null;
        Rect = null;
        OcrEngine = null;
        TimeoutMs = null;
        Image = null;
        MinScore = null;
        Items = null;
        X = null;
        Y = null;
        MouseAction = null;
        WheelDelta = null;
        OutputVar = null;
        CompositeOnFail = null;
        Steps = null;
        Flow = null;
    }
}
