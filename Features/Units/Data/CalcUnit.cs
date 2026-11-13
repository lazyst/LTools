using System.Globalization;
using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>数值计算。</summary>
public sealed class CalcUnit : IStepUnit
{
    private static readonly string[] Operations =
        { "add", "subtract", "multiply", "divide", "modulo", "power", "min", "max", "abs", "round" };

    /// <summary>运算的中文名。<c>add</c> 在这里是「加法」，不能沿用列表的「追加一项」。</summary>
    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["add"] = "加法",
        ["subtract"] = "减法",
        ["multiply"] = "乘法",
        ["divide"] = "除法",
        ["modulo"] = "取余",
        ["power"] = "乘方",
        ["min"] = "最小值",
        ["max"] = "最大值",
        ["abs"] = "绝对值",
        ["round"] = "四舍五入",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "data.calc",
        Name = "计算",
        Category = "文本与数据",
        Icon = "calc",
        Description = "加减乘除、取余、幂、最值、绝对值、四舍五入",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("operation", "运算", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("add"), Options: Operations,
                Labels: OperationLabels,
                OptionParams: OptionMap.Of(("round", ["digits"]))),
            new InputSpec("a", "A", ValueKind.number, InputEditor.number, Required: true),
            new InputSpec("b", "B", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0)),
            new InputSpec("digits", "小数位", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0)),
        },
        Outputs = new[]
        {
            new OutputSpec("number", "结果", ValueKind.number, "number", Description: "计算结果（数值）"),
            new OutputSpec("text", "文本", ValueKind.text, Description: "计算结果的文本形式"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim().ToLowerInvariant();
        double a = args.Number("a");
        double b = args.Number("b");

        double result = op switch
        {
            "add" => a + b,
            "subtract" => a - b,
            "multiply" => a * b,
            "divide" => Math.Abs(b) < double.Epsilon
                ? throw new ActionException("除数不能为 0")
                : a / b,
            "modulo" => Math.Abs(b) < double.Epsilon
                ? throw new ActionException("取余的除数不能为 0")
                : a % b,
            "power" => Math.Pow(a, b),
            "min" => Math.Min(a, b),
            "max" => Math.Max(a, b),
            "abs" => Math.Abs(a),
            "round" => Math.Round(a, (int)Math.Clamp(args.Number("digits"), 0, 15)),
            _ => throw new ActionException($"未知运算：{op}"),
        };

        if (double.IsNaN(result) || double.IsInfinity(result))
            throw new ActionException($"计算结果无效（{op}）");

        ctx.SetOutput("number", Value.FromNumber(result));
        ctx.SetOutput("text", Value.FromText(
            result.ToString("0.############", CultureInfo.InvariantCulture)));
    }
}

/// <summary>时间：当前时间 / 按格式输出 / 偏移。</summary>
