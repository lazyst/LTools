using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace LTools.Features;

// ============================================================================
// 自动化流程的数据模型（对应 docs/ACTION_SYSTEM_PLAN.md 的「流程层」）。
//
// 设计取舍：
// - 动作仍是叶子（ActionDto 保持扁平、避免继续塞控制字段）；
// - 流程是**嵌套块**（If/Loop 带子列表），不提供 goto——「回到某步」用循环 + 跳出表达；
// - 条件与变量右值共用同一套 Probe（取值器），因此「判断」与「赋值」只有一种概念。
// ============================================================================

/// <summary>流程节点种类。JSON 中以小写字符串存储。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowNodeKind
{
    /// <summary>执行一个叶子动作。</summary>
    action,

    /// <summary>变量赋值。</summary>
    set,

    /// <summary>条件分支（带 Then / Else 子块）。</summary>
    @if,

    /// <summary>循环（固定次数或条件重复，带 Body 子块）。</summary>
    loop,

    /// <summary>跳出当前循环。</summary>
    @break,

    /// <summary>进入下一次循环。</summary>
    @continue,

    /// <summary>停止整个流程。</summary>
    stop,
}

/// <summary>循环方式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoopMode
{
    /// <summary>固定次数。</summary>
    count,

    /// <summary>条件为真时重复（最多受运行预算保护）。</summary>
    @while,
}

/// <summary>取值器种类。条件与 set 的右值共用。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProbeKind
{
    /// <summary>字面文本（支持 ${变量} 模板）。</summary>
    text,

    /// <summary>读取一个变量。</summary>
    variable,

    /// <summary>获取前台应用当前选中的文本（UI Automation，必要时退回 WM_COPY）。</summary>
    selection,

    /// <summary>读取剪贴板文本。</summary>
    clipboard,

    /// <summary>当前光标坐标。</summary>
    cursor,

    /// <summary>当前日期时间（可指定格式）。</summary>
    datetime,

    /// <summary>弹出输入框询问用户。</summary>
    user,

    /// <summary>屏幕图像匹配（OpenCV 模板匹配）。</summary>
    image,

    /// <summary>截图 OCR（系统 Windows.Media.Ocr，识别指定区域的文字）。</summary>
    ocr,

    /// <summary>屏幕像素颜色。</summary>
    color,

    /// <summary>窗口状态（存在 / 在前台）。</summary>
    window,

    /// <summary>进程是否存在。</summary>
    process,

    /// <summary>上一步被「继续」策略吞掉的错误文本（没有则为空）。</summary>
    lastError,
}

/// <summary>图像搜索区域。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImageRegion
{
    /// <summary>当前前台窗口（默认，速度与准确率最好）。</summary>
    foreground,

    /// <summary>整个虚拟桌面。</summary>
    screen,

    /// <summary>固定矩形（<see cref="Probe.Rect"/>）。</summary>
    rect,
}

/// <summary>条件比较方式。UI 会按取值器种类筛选可用项。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompareOp
{
    /// <summary>布尔为真（变量里存的是 bool 时用）。</summary>
    isTrue,

    /// <summary>布尔为假。</summary>
    isFalse,

    equals,
    notEquals,
    contains,
    notContains,
    startsWith,
    endsWith,
    regex,
    isEmpty,
    isNotEmpty,
    greater,
    less,
    greaterOrEqual,
    lessOrEqual,

    /// <summary>图像 / 窗口：找到了 / 存在。</summary>
    found,

    /// <summary>图像 / 窗口：没找到 / 不存在。</summary>
    notFound,

    /// <summary>窗口：是当前前台窗口。</summary>
    active,

    /// <summary>窗口：不是当前前台窗口。</summary>
    notActive,

    /// <summary>颜色接近目标值（容差见 <see cref="Probe.Tolerance"/>）。</summary>
    nearColor,
}

/// <summary>
/// 取值器参数。这是一个**参数包**（按 <see cref="Kind"/> 使用其中一部分字段），
/// 与 <see cref="ActionDto"/> 的扁平风格一致；运行时由 <see cref="ProbeEvaluator"/> 分发。
/// </summary>
public sealed class Probe
{
    /// <summary>取值方式。</summary>
    public ProbeKind Kind { get; set; }

    /// <summary>text: 字面文本（支持 ${} 模板）。</summary>
    public string? Text { get; set; }

    /// <summary>variable: 变量名（支持 a.b 取属性）。</summary>
    public string? Name { get; set; }

    /// <summary>datetime: .NET 日期格式（默认 yyyy-MM-dd HH:mm:ss）。</summary>
    public string? Format { get; set; }

    /// <summary>user: 输入框提示。</summary>
    public string? Prompt { get; set; }

    /// <summary>user: 默认值。</summary>
    public string? Default { get; set; }

    // —— image ——

    /// <summary>image: 模板图片路径（相对 resources/images 或绝对路径）。</summary>
    public string? Image { get; set; }

    /// <summary>image / ocr: 搜索或截图区域。</summary>
    public ImageRegion Region { get; set; }

    /// <summary>image / ocr: 固定区域 [x, y, w, h]（Region=rect 时使用）。</summary>
    public int[]? Rect { get; set; }

    /// <summary>image: 最长等待时间（ms，0=只匹配一次）。</summary>
    public int TimeoutMs { get; set; }

    /// <summary>image: 轮询间隔（ms）。</summary>
    public int IntervalMs { get; set; } = 200;

    /// <summary>image: 最低置信度（0..1）。</summary>
    public double MinScore { get; set; } = 0.85;

    // —— color ——

    /// <summary>color: X 坐标（支持 ${} 模板）。</summary>
    public string? X { get; set; }

    /// <summary>color: Y 坐标（支持 ${} 模板）。</summary>
    public string? Y { get; set; }

    /// <summary>color: 目标颜色 #RRGGBB（条件比较时使用）。</summary>
    public string? Color { get; set; }

    /// <summary>color: 每通道容差（0..255）。</summary>
    public int Tolerance { get; set; } = 12;

    // —— window / process ——

    /// <summary>window: 标题包含的文本（空=不限）。</summary>
    public string? WindowTitle { get; set; }

    /// <summary>window: 窗口类名（空=不限）。</summary>
    public string? WindowClass { get; set; }

    /// <summary>process: 进程名（不含 .exe）。</summary>
    public string? ProcessName { get; set; }

    /// <summary>深拷贝。</summary>
    public Probe Clone() => new()
    {
        Kind = Kind,
        Text = Text,
        Name = Name,
        Format = Format,
        Prompt = Prompt,
        Default = Default,
        Image = Image,
        Region = Region,
        Rect = Rect != null ? (int[])Rect.Clone() : null,
        TimeoutMs = TimeoutMs,
        IntervalMs = IntervalMs,
        MinScore = MinScore,
        X = X,
        Y = Y,
        Color = Color,
        Tolerance = Tolerance,
        WindowTitle = WindowTitle,
        WindowClass = WindowClass,
        ProcessName = ProcessName,
    };
}

/// <summary>条件：取值 → 比较；<see cref="Bind"/> 可把取值结果写入变量供后续步骤使用。</summary>
public sealed class Condition
{
    /// <summary>取值器。</summary>
    public Probe Probe { get; set; } = new();

    /// <summary>比较方式。</summary>
    public CompareOp Op { get; set; }

    /// <summary>比较右值（支持 ${} 模板）；部分 Op 不需要。</summary>
    public string? Operand { get; set; }

    /// <summary>对比较结果取反。</summary>
    public bool Negate { get; set; }

    /// <summary>把取值结果存入该变量（可选）。图像匹配会把 x/y/found/score 一起存入。</summary>
    public string? Bind { get; set; }

    /// <summary>深拷贝。</summary>
    public Condition Clone() => new()
    {
        Probe = Probe.Clone(),
        Op = Op,
        Operand = Operand,
        Negate = Negate,
        Bind = Bind,
    };
}

/// <summary>变量赋值节点。</summary>
public sealed class SetNode
{
    /// <summary>目标变量名。</summary>
    public string Var { get; set; } = "";

    /// <summary>右值。</summary>
    public Probe Probe { get; set; } = new();

    /// <summary>深拷贝。</summary>
    public SetNode Clone() => new() { Var = Var, Probe = Probe.Clone() };
}

/// <summary>条件分支节点。</summary>
public sealed class IfNode
{
    /// <summary>条件。</summary>
    public Condition Condition { get; set; } = new();

    /// <summary>条件为真时执行。</summary>
    public List<FlowNode> Then { get; set; } = new();

    /// <summary>条件为假时执行；null / 空 = 不做任何事。</summary>
    public List<FlowNode>? Else { get; set; }

    /// <summary>深拷贝。</summary>
    public IfNode Clone() => new()
    {
        Condition = Condition.Clone(),
        Then = FlowNode.CloneList(Then),
        Else = Else != null ? FlowNode.CloneList(Else) : null,
    };
}

/// <summary>循环节点。</summary>
public sealed class LoopNode
{
    /// <summary>循环方式。</summary>
    public LoopMode Mode { get; set; }

    /// <summary>count: 次数（1..100000）。</summary>
    public int Count { get; set; } = 3;

    /// <summary>while: 每轮开始前判断；为真才继续。</summary>
    public Condition? While { get; set; }

    /// <summary>可选：把当前轮下标（0 开始）写进这个用户变量；留空表示不写。</summary>
    public string IndexVar { get; set; } = "";

    /// <summary>循环体。</summary>
    public List<FlowNode> Body { get; set; } = new();

    /// <summary>深拷贝。</summary>
    public LoopNode Clone() => new()
    {
        Mode = Mode,
        Count = Count,
        While = While?.Clone(),
        IndexVar = IndexVar,
        Body = FlowNode.CloneList(Body),
    };
}

/// <summary>流程节点。按 <see cref="Kind"/> 使用 Action / Set / If / Loop 之一。</summary>
public sealed class FlowNode
{
    /// <summary>节点种类。</summary>
    public FlowNodeKind Kind { get; set; }

    /// <summary>action: 内嵌的叶子动作快照（沿用组合动作「动作解耦」的约定）。</summary>
    public ActionDto? Action { get; set; }

    /// <summary>set: 赋值参数。</summary>
    public SetNode? Set { get; set; }

    /// <summary>if: 分支参数。</summary>
    public IfNode? If { get; set; }

    /// <summary>loop: 循环参数。</summary>
    public LoopNode? Loop { get; set; }

    /// <summary>执行本节点前等待的毫秒数。</summary>
    public int DelayMs { get; set; }

    /// <summary>本节点失败后的策略；null = 用组合级默认。</summary>
    public OnFailStrategy? OnFail { get; set; }

    /// <summary>深拷贝（含子块）。</summary>
    public FlowNode Clone() => new()
    {
        Kind = Kind,
        Action = Action?.Clone(),
        Set = Set?.Clone(),
        If = If?.Clone(),
        Loop = Loop?.Clone(),
        DelayMs = DelayMs,
        OnFail = OnFail,
    };

    /// <summary>深拷贝列表（null 安全）。</summary>
    public static List<FlowNode> CloneList(IEnumerable<FlowNode> nodes) =>
        nodes.Select(n => n.Clone()).ToList();
}

/// <summary>流程的展示文本（编辑器行摘要 / 日志）。</summary>
public static class FlowText
{
    /// <summary>节点摘要。</summary>
    public static string Summary(FlowNode n) => n.Kind switch
    {
        FlowNodeKind.action => ActionLabel(n.Action) + OutputSuffix(n.Action),
        FlowNodeKind.set => $"{n.Set?.Var} = {ProbeLabel(n.Set?.Probe)}",
        FlowNodeKind.@if => "如果 " + ConditionLabel(n.If?.Condition),
        FlowNodeKind.loop => n.Loop?.Mode == LoopMode.@while
            ? "循环 · 条件成立时"
            : $"循环 · {n.Loop?.Count ?? 0} 次",
        FlowNodeKind.@break => "跳出循环",
        FlowNodeKind.@continue => "进入下一次循环",
        FlowNodeKind.stop => "停止流程",
        _ => n.Kind.ToString(),
    };

    /// <summary>节点类型徽标。</summary>
    public static string Badge(FlowNode n) => n.Kind switch
    {
        FlowNodeKind.action => "动作",
        FlowNodeKind.set => "设置",
        FlowNodeKind.@if => "如果",
        FlowNodeKind.loop => "循环",
        FlowNodeKind.@break => "跳出",
        FlowNodeKind.@continue => "继续",
        FlowNodeKind.stop => "停止",
        _ => "?",
    };

    /// <summary>叶子动作显示名。</summary>
    public static string ActionLabel(ActionDto? a)
    {
        if (a == null) return "(空动作)";
        int count = a.Items?.Count ?? 0;
        return a.Type switch
        {
            ActionType.sendText => string.IsNullOrEmpty(a.Text) ? "发送文本" : $"发送文本：{Trim(a.Text, 24)}",
            ActionType.setClipboard => string.IsNullOrEmpty(a.Text) ? "写入剪贴板（清空）" : $"写入剪贴板：{Trim(a.Text, 24)}",
            ActionType.sendKeys => $"模拟按键（{count} 条）",
            ActionType.mouse => MouseLabel(a),
            _ => string.IsNullOrEmpty(a.Name) ? ActionTypeLabel.Of(a.Type) : a.Name,
        };
    }

    /// <summary>动作输出变量的后缀（步骤行里显示「→ 变量名」）。</summary>
    private static string OutputSuffix(ActionDto? a)
        => string.IsNullOrWhiteSpace(a?.OutputVar) ? "" : $" → {a!.OutputVar!.Trim()}";

    private static string MouseLabel(ActionDto a)
    {
        string what = a.MouseAction switch
        {
            MouseActionKind.doubleClick => "双击",
            MouseActionKind.rightClick => "右键",
            MouseActionKind.middleClick => "中键",
            MouseActionKind.move => "移动",
            MouseActionKind.wheelUp => "滚轮上",
            MouseActionKind.wheelDown => "滚轮下",
            _ => "点击",
        };
        return $"{what} {a.X ?? "?"}, {a.Y ?? "?"}";
    }

    /// <summary>取值器摘要。</summary>
    public static string ProbeLabel(Probe? p)
    {
        if (p == null) return "?";
        return p.Kind switch
        {
            ProbeKind.text => $"\"{Trim(p.Text, 24)}\"",
            ProbeKind.variable => "${" + p.Name + "}",
            ProbeKind.selection => "选中文本",
            ProbeKind.clipboard => "剪贴板",
            ProbeKind.cursor => "光标位置",
            ProbeKind.datetime => "当前时间",
            ProbeKind.user => "\"询问用户\"",
            ProbeKind.image => $"图像 {Trim(System.IO.Path.GetFileName(p.Image), 20)}",
            ProbeKind.ocr => "截图文字",
            ProbeKind.color => $"颜色 ({p.X}, {p.Y})",
            ProbeKind.window => $"窗口 {Trim(p.WindowTitle ?? p.WindowClass, 16)}",
            ProbeKind.process => $"进程 {p.ProcessName}",
            ProbeKind.lastError => "上一步的错误",
            _ => p.Kind.ToString(),
        };
    }

    /// <summary>条件摘要。</summary>
    public static string ConditionLabel(Condition? c)
    {
        if (c == null) return "?";
        string core = OpLabel(c) + (NeedsOperand(c.Op) && !string.IsNullOrEmpty(c.Operand) ? $" {c.Operand}" : "");
        return c.Negate ? "非 (" + core + ")" : core;
    }

    private static string OpLabel(Condition c) => c.Op switch
    {
        CompareOp.isTrue => $"{ProbeLabel(c.Probe)} 为真",
        CompareOp.isFalse => $"{ProbeLabel(c.Probe)} 为假",
        CompareOp.equals => $"{ProbeLabel(c.Probe)} 等于",
        CompareOp.notEquals => $"{ProbeLabel(c.Probe)} 不等于",
        CompareOp.contains => $"{ProbeLabel(c.Probe)} 包含",
        CompareOp.notContains => $"{ProbeLabel(c.Probe)} 不包含",
        CompareOp.startsWith => $"{ProbeLabel(c.Probe)} 开头是",
        CompareOp.endsWith => $"{ProbeLabel(c.Probe)} 结尾是",
        CompareOp.regex => $"{ProbeLabel(c.Probe)} 匹配正则",
        CompareOp.isEmpty => $"{ProbeLabel(c.Probe)} 为空",
        CompareOp.isNotEmpty => $"{ProbeLabel(c.Probe)} 不为空",
        CompareOp.greater => $"{ProbeLabel(c.Probe)} 大于",
        CompareOp.less => $"{ProbeLabel(c.Probe)} 小于",
        CompareOp.greaterOrEqual => $"{ProbeLabel(c.Probe)} ≥",
        CompareOp.lessOrEqual => $"{ProbeLabel(c.Probe)} ≤",
        CompareOp.found => $"{ProbeLabel(c.Probe)} 找到了",
        CompareOp.notFound => $"{ProbeLabel(c.Probe)} 没找到",
        CompareOp.active => $"{ProbeLabel(c.Probe)} 在前台",
        CompareOp.notActive => $"{ProbeLabel(c.Probe)} 不在前台",
        CompareOp.nearColor => $"{ProbeLabel(c.Probe)} 接近 {c.Probe.Color}",
        _ => c.Op.ToString(),
    };

    /// <summary>该比较方式是否需要右值。</summary>
    public static bool NeedsOperand(CompareOp op) => op is
        CompareOp.equals or CompareOp.notEquals or CompareOp.contains or CompareOp.notContains or
        CompareOp.startsWith or CompareOp.endsWith or CompareOp.regex or
        CompareOp.greater or CompareOp.less or CompareOp.greaterOrEqual or CompareOp.lessOrEqual;

    private static string Trim(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
