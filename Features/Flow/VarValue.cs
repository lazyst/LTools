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

    /// <summary>图像匹配置信度（0..1）。</summary>
    public double? Score { get; init; }

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

    /// <summary>取属性（x / y / found / score / text / number）；未知属性返回 null。</summary>
    public string? Property(string name) => name.ToLowerInvariant() switch
    {
        "x" => X?.ToString(CultureInfo.InvariantCulture),
        "y" => Y?.ToString(CultureInfo.InvariantCulture),
        "found" or "flag" => Flag?.ToString().ToLowerInvariant(),
        "score" => Score?.ToString("0.###", CultureInfo.InvariantCulture),
        "number" => Number?.ToString("0.####", CultureInfo.InvariantCulture),
        "text" => Text,
        _ => null,
    };
}
