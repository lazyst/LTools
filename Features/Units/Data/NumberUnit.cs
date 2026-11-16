using System.Globalization;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>
/// 数字处理（<c>data.number</c>）：进制转换、取整、千分位 / 百分比格式化。
/// </summary>
/// <remarks>
/// 纯函数零依赖。输入合并成一个 <c>value</c>（既能填数字，也能填 <c>FF</c> / <c>1010</c> 这类进制串），
/// 避免参数表单里出现两个含义重叠的输入框；输出仍按「数值 + 文本 + hex + binary」四份给。
/// </remarks>
public sealed class NumberUnit : IStepUnit
{
    private static readonly string[] Operations =
        { "toBase", "fromBase", "round", "ceil", "floor", "thousands", "percent" };
    private static readonly string[] Bases = { "2", "8", "10", "16" };
    private static readonly string[] Roundings = { "round45", "banker", "truncate" };

    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["toBase"] = "转成 N 进制",
        ["fromBase"] = "按 N 进制解析",
        ["round"] = "四舍五入",
        ["ceil"] = "向上取整",
        ["floor"] = "向下取整",
        ["thousands"] = "千分位",
        ["percent"] = "百分比",
    };

    private static readonly Dictionary<string, string> BaseLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2"] = "二进制", ["8"] = "八进制", ["10"] = "十进制", ["16"] = "十六进制",
    };

    private static readonly Dictionary<string, string> RoundingLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["round45"] = "四舍五入（0.5 进位）",
        ["banker"] = "银行家舍入（0.5 取偶）",
        ["truncate"] = "直接截断",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "data.number",
        Name = "数字处理",
        Category = "文本与数据",
        Icon = "calc",
        Description = "进制转换、取整、千分位 / 百分比格式化",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("value", "数值", ValueKind.text, Required: true,
                Description: "数字文本；「按 N 进制解析」时可写 FF / 1010 这类。纯数字文本直接给即可（不做货币符号 / 千分位清洗）"),
            new InputSpec("operation", "方式", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("toBase"),
                Options: Operations, Labels: OperationLabels,
                OptionParams: OptionMap.Of(
                    ("toBase", ["base"]),
                    ("fromBase", ["base"]),
                    ("round", ["digits", "rounding"]),
                    ("ceil", ["digits"]),
                    ("floor", ["digits"]),
                    ("thousands", ["digits"]),
                    ("percent", ["digits"])),
                Description: "选了方式才显示它用得到的参数"),
            new InputSpec("base", "进制", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("16"), Options: Bases, Labels: BaseLabels,
                Description: "只在「转成 N 进制 / 按 N 进制解析」下生效"),
            new InputSpec("digits", "小数位", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), Description: "0–15；取整与格式化用"),
            new InputSpec("rounding", "舍入方式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("round45"), Options: Roundings, Labels: RoundingLabels,
                Description: "只在「四舍五入」下生效"),
        },
        Outputs = new[]
        {
            new OutputSpec("number", "数值", ValueKind.number, "number", Description: "结果的数值形式"),
            new OutputSpec("text", "文本", ValueKind.text, "formatted", Description: "格式化后的文本"),
            new OutputSpec("hex", "十六进制", ValueKind.text, Description: "数值的十六进制形式（无前缀）"),
            new OutputSpec("binary", "二进制", ValueKind.text, Description: "数值的二进制形式"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        NumberResult result = Compute(
            args.Text("value"),
            args.Text("operation").Trim(),
            args.Text("base").Trim(),
            (int)args.Number("digits"),
            args.Text("rounding").Trim());

        ctx.SetOutput("number", Value.FromNumber(result.Number));
        ctx.SetOutput("text", Value.FromText(result.Text));
        ctx.SetOutput("hex", Value.FromText(result.Hex));
        ctx.SetOutput("binary", Value.FromText(result.Binary));
    }

    internal readonly record struct NumberResult(double Number, string Text, string Hex, string Binary);

    /// <summary>纯计算：动作单元与 <c>--selftest</c> 共用。</summary>
    internal static NumberResult Compute(string value, string operation, string numberBase,
        int digits, string rounding)
    {
        if (Array.IndexOf(Operations, operation) < 0)
            throw ActionErrors.Precondition($"数字处理方式只能是 {string.Join(" / ", Operations)}（当前 {operation}）");
        if (Array.IndexOf(Bases, numberBase) < 0)
            throw ActionErrors.Precondition($"进制只能是 {string.Join(" / ", Bases)}（当前 {numberBase}）");

        int radix = numberBase switch { "2" => 2, "8" => 8, "16" => 16, _ => 10 };
        int places = (int)Math.Clamp(digits, 0, 15);

        double number;
        string text;

        switch (operation)
        {
            case "fromBase":
            {
                try { number = Convert.ToInt64((value ?? "").Trim(), radix); }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    throw ActionErrors.Precondition($"「{value}」不是合法的 {radix} 进制整数", ex);
                }
                text = ((long)number).ToString(CultureInfo.InvariantCulture);
                break;
            }

            case "toBase":
            {
                number = Parse(value);
                text = Convert.ToString((long)number, radix).ToUpperInvariant();
                break;
            }

            default:
            {
                number = Parse(value);
                double factor = Math.Pow(10, places);
                double result = operation switch
                {
                    "round" => Round(number, places, rounding),
                    "ceil" => Math.Ceiling(number * factor) / factor,
                    "floor" => Math.Floor(number * factor) / factor,
                    _ => number,
                };
                number = result;
                text = operation switch
                {
                    "thousands" => result.ToString("N" + places, CultureInfo.InvariantCulture),
                    "percent" => result.ToString("P" + places, CultureInfo.InvariantCulture),
                    _ => result.ToString("0.############", CultureInfo.InvariantCulture),
                };
                break;
            }
        }

        long whole = (long)Math.Round(number, 0, MidpointRounding.AwayFromZero);
        return new NumberResult(
            number,
            text,
            Convert.ToString(whole, 16).ToUpperInvariant(),
            Convert.ToString(whole, 2));
    }

    private static double Parse(string? value)
    {
        if (!double.TryParse((value ?? "").Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double number))
            throw ActionErrors.Precondition($"「{value}」不是数字");
        return number;
    }

    internal static double Round(double value, int places, string rounding) => rounding switch
    {
        "banker" => Math.Round(value, places, MidpointRounding.ToEven),
        "truncate" => Math.Truncate(value * Math.Pow(10, places)) / Math.Pow(10, places),
        _ => Math.Round(value, places, MidpointRounding.AwayFromZero),
    };
}
