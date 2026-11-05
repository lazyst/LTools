using System.Globalization;

namespace LTools.Features;

/// <summary>
/// 流程变量值。一个值同时携带文本与可选的结构化属性：
/// 图像匹配带 <see cref="X"/>/<see cref="Y"/>/<see cref="Flag"/>/<see cref="Score"/>，
/// 数字带 <see cref="Number"/>，布尔带 <see cref="Flag"/>。
/// </summary>
/// <remarks>
/// 文本模板（<c>${name}</c>）取 <see cref="Text"/>；<c>${m.x}</c> 这类写法取属性。
/// 变量只在单次运行内有效，不落盘（用户明确不要跨运行持久化）。
/// </remarks>
public sealed class VarValue
{
    /// <summary>文本形式（`${name}` 的结果）。</summary>
    public string Text { get; init; } = "";

    /// <summary>数字形式（数值比较 / 循环下标用）。</summary>
    public double? Number { get; init; }

    /// <summary>布尔形式（图像是否找到 / 进程是否存在等）。</summary>
    public bool? Flag { get; init; }

    /// <summary>点坐标 X（图像匹配中心 / 光标）。</summary>
    public int? X { get; init; }

    /// <summary>点坐标 Y。</summary>
    public int? Y { get; init; }

    /// <summary>区域宽（截图步骤框选 / 取到的范围）。</summary>
    public int? W { get; init; }

    /// <summary>区域高（截图步骤框选 / 取到的范围）。</summary>
    public int? H { get; init; }

    /// <summary>关联的文件路径（截图步骤存下来的 png）。</summary>
    public string File { get; init; } = "";

    /// <summary>图像匹配置信度（0..1）。</summary>
    public double? Score { get; init; }

    /// <summary>错误输出（运行命令的 stderr；无则空）。</summary>
    public string Error { get; init; } = "";

    /// <summary>速记标题（读速记 / 写速记的结果）。</summary>
    public string Title { get; init; } = "";

    /// <summary>速记时间（yyyy-MM-dd HH:mm:ss）。</summary>
    public string Time { get; init; } = "";

    /// <summary>由文本构造。</summary>
    public static VarValue FromText(string? text) => new() { Text = text ?? "" };

    /// <summary>由数字构造。</summary>
    public static VarValue FromNumber(double value) => new()
    {
        Text = value.ToString("0.####", CultureInfo.InvariantCulture),
        Number = value,
    };

    /// <summary>由布尔构造。</summary>
    public static VarValue FromBool(bool value) => new()
    {
        Text = value ? "true" : "false",
        Flag = value,
    };

    /// <summary>由屏幕坐标构造。</summary>
    public static VarValue FromPoint(int x, int y) => new()
    {
        Text = $"{x},{y}",
        X = x,
        Y = y,
    };

    /// <summary>由框选区域构造：<c>${变量}</c> = <c>x,y,w,h</c>，另外带中心点与图片文件路径。</summary>
    public static VarValue FromRegion(int x, int y, int w, int h, string file) => new()
    {
        Text = $"{x},{y},{w},{h}",
        X = x,
        Y = y,
        W = w,
        H = h,
        File = file,
        Flag = true,
    };

    /// <summary>由图像匹配结果构造。</summary>
    public static VarValue FromMatch(int x, int y, double score) => new()
    {
        Text = $"{x},{y}",
        X = x,
        Y = y,
        Flag = true,
        Score = score,
        Number = score,
    };

    /// <summary>取属性（x / y / w / h / cx / cy / rect / file / found / score / number / text / err）；未知属性返回 null。</summary>
    public string? Property(string name) => name.ToLowerInvariant() switch
    {
        "x" => X?.ToString(CultureInfo.InvariantCulture),
        "y" => Y?.ToString(CultureInfo.InvariantCulture),
        "w" or "width" => W?.ToString(CultureInfo.InvariantCulture),
        "h" or "height" => H?.ToString(CultureInfo.InvariantCulture),
        "cx" => X is int rx && W is int rw ? (rx + rw / 2).ToString(CultureInfo.InvariantCulture) : null,
        "cy" => Y is int ry && H is int rh ? (ry + rh / 2).ToString(CultureInfo.InvariantCulture) : null,
        "rect" => W != null ? Text : null,
        "file" or "path" => File.Length > 0 ? File : null,
        "found" or "flag" => Flag?.ToString().ToLowerInvariant(),
        "score" => Score?.ToString("0.###", CultureInfo.InvariantCulture),
        "number" or "exit" or "code" or "count" => Number?.ToString("0.####", CultureInfo.InvariantCulture),
        "text" or "out" => Text,
        "err" or "error" or "stderr" => Error,
        "title" => Title,
        "time" => Time,
        _ => null,
    };
}
