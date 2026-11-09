using System.Text.Json.Serialization;

namespace LTools.Features;

// ============================================================================
// 取值器的数据模型（截图 / 找图 / 识字 / 读速记…共用一套 Probe 参数包）。
// 步骤单元（Core/Actions）按自己的参数构造 Probe，交给 Features/Flow 下的实现执行。
// ============================================================================

/// <summary>取值器种类。</summary>
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

    /// <summary>截图：交互框选 / 前台窗口 / 整个屏幕 / 固定区域，取回区域 + 图片文件（见 <see cref="ScreenshotProbe"/>）。</summary>
    screenshot,

    /// <summary>屏幕图像匹配（OpenCV 模板匹配）。</summary>
    image,

    /// <summary>文字识别：识别一张图片（通常来自「截图」）里的文字，系统 Windows.Media.Ocr 或 PP-OCRv6。</summary>
    ocr,

    /// <summary>
    /// 找文字：在屏幕区域里查找一段文字，命中时给出位置（见 <see cref="TextLocator"/>）。
    /// 与「文字点击」共用同一套定位；没找到不是错误，只是 <c>found = false</c>。
    /// </summary>
    textSearch,

    /// <summary>屏幕像素颜色。</summary>
    color,

    /// <summary>读速记：按分类 / 关键字取最近修改的一条（见 <see cref="NoteRepository"/>）。</summary>
    note,

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

/// <summary>「截图」步骤的取图方式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotMode
{
    /// <summary>交互框选：运行时让用户框一块（默认）。</summary>
    interactive,

    /// <summary>当前前台窗口的可见范围。</summary>
    foreground,

    /// <summary>整个虚拟桌面。</summary>
    screen,

    /// <summary>固定区域（<see cref="Probe.ScreenshotRect"/>，可写 ${} 模板）。</summary>
    rect,
}

/// <summary>
/// 取值器参数。这是一个**参数包**（按 <see cref="Kind"/> 使用其中一部分字段）：
/// 步骤单元按自己的参数构造它，再由 <c>Features/Flow</c> 下的对应实现执行。
/// </summary>
public sealed class Probe
{
    /// <summary>取值方式。</summary>
    public ProbeKind Kind { get; set; }

    /// <summary>text: 字面文本（支持 ${} 模板）；textSearch: 要找的文字（同样支持 ${} 模板）。</summary>
    public string? Text { get; set; }

    /// <summary>变量名（支持 a.b 取属性）。</summary>
    public string? Name { get; set; }

    /// <summary>note: 分类（空 / 全部 = 所有分类；支持 ${} 模板）。</summary>
    public string? NoteCategory { get; set; }

    /// <summary>datetime: .NET 日期格式（默认 yyyy-MM-dd HH:mm:ss）。</summary>
    public string? Format { get; set; }

    /// <summary>user: 输入框提示。</summary>
    public string? Prompt { get; set; }

    /// <summary>user: 默认值。</summary>
    public string? Default { get; set; }

    /// <summary>user: 是否多行输入（长文本 / 多行内容用，Ctrl+Enter 确定）。</summary>
    public bool Multiline { get; set; }

    // —— image ——

    /// <summary>image: 模板图片路径（动作自己的 files/、相对 resources/images、相对数据根如 resources\user\a.png，或绝对路径）。</summary>
    public string? Image { get; set; }

    /// <summary>image: 搜索区域；textSearch: 找字区域（与找图同一套语义）。</summary>
    public ImageRegion Region { get; set; }

    /// <summary>image / textSearch: 固定区域 [x, y, w, h]（Region=rect 时使用）。</summary>
    public int[]? Rect { get; set; }

    /// <summary>image / textSearch: 最长等待时间（ms，0=只匹配一次）。</summary>
    public int TimeoutMs { get; set; }

    /// <summary>image / textSearch: 轮询间隔（ms）。</summary>
    public int IntervalMs { get; set; } = 200;

    /// <summary>image: 最低置信度（0..1）。</summary>
    public double MinScore { get; set; } = 0.85;

    // —— screenshot ——

    /// <summary>screenshot: 取图方式。</summary>
    public ScreenshotMode ScreenshotMode { get; set; }

    /// <summary>screenshot: 固定区域 «x,y,w,h»（支持 ${} 模板，如 <c>${区域}</c>）。</summary>
    public string? ScreenshotRect { get; set; }

    // —— ocr ——

    /// <summary>ocr: 要识别的图片（变量或路径，支持 ${} 模板，如 <c>${截图}</c> / <c>${截图.file}</c>）。</summary>
    public string? OcrImage { get; set; }

    /// <summary>ocr / textSearch: 识别引擎。<c>null</c>/<c>windows</c> = 系统 OCR；<c>v6tiny</c> / <c>v6small</c> = PP-OCRv6。</summary>
    public string? OcrEngine { get; set; }

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
        NoteCategory = NoteCategory,
        Format = Format,
        Prompt = Prompt,
        Default = Default,
        Multiline = Multiline,
        Image = Image,
        Region = Region,
        Rect = Rect != null ? (int[])Rect.Clone() : null,
        TimeoutMs = TimeoutMs,
        IntervalMs = IntervalMs,
        MinScore = MinScore,
        ScreenshotMode = ScreenshotMode,
        ScreenshotRect = ScreenshotRect,
        OcrImage = OcrImage,
        OcrEngine = OcrEngine,
        X = X,
        Y = Y,
        Color = Color,
        Tolerance = Tolerance,
        WindowTitle = WindowTitle,
        WindowClass = WindowClass,
        ProcessName = ProcessName,
    };
}
