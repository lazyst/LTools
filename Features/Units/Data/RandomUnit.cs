using System.Text;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>
/// 随机值：整数 / 随机字符串 / GUID / 从列表里抽一项（<c>data.random</c>）。
/// </summary>
/// <remarks>
/// 四种用途共用一个动作单元，靠「方式」区分——比在工具箱里摆四个只有一行实现的动作单元清楚。
/// 整数区间是<b>闭区间</b>（含 min 和 max），符合「1 到 6 点」这种直觉；
/// GUID 走 <see cref="Guid.NewGuid"/>，随机串走 <see cref="Random.Shared"/>（线程安全）。
/// </remarks>
public sealed class RandomUnit : IStepUnit
{
    private static readonly string[] Operations = { "number", "text", "guid", "pick" };

    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["number"] = "随机整数",
        ["text"] = "随机字符串",
        ["guid"] = "GUID",
        ["pick"] = "从列表抽一项",
    };

    private static readonly string[] Charsets = { "alnum", "letters", "digits", "hex", "lower", "upper" };

    private static readonly Dictionary<string, string> CharsetLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alnum"] = "字母 + 数字",
        ["letters"] = "只字母",
        ["digits"] = "只数字",
        ["hex"] = "十六进制（0-9a-f）",
        ["lower"] = "小写字母",
        ["upper"] = "大写字母",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "data.random",
        Name = "随机",
        Category = "文本与数据",
        Icon = "refresh",
        Description = "随机整数（含两端）、随机字符串、GUID，或从列表里抽一项",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("operation", "方式", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("number"),
                Options: Operations, Labels: OperationLabels),
            new InputSpec("min", "最小值", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(1), VisibleWhen: "operation == 'number'",
                Description: "闭区间下界，可以填负数"),
            new InputSpec("max", "最大值", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(100), VisibleWhen: "operation == 'number'",
                Description: "闭区间上界；比最小值小会自动对调"),
            new InputSpec("length", "长度", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(8), VisibleWhen: "operation == 'text'",
                Description: "字符个数（1～4096）"),
            new InputSpec("charset", "字符集", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("alnum"), Options: Charsets, Labels: CharsetLabels,
                VisibleWhen: "operation == 'text'"),
            new InputSpec("list", "列表", ValueKind.list,
                VisibleWhen: "operation == 'pick'",
                Description: "从这个列表里随机抽一项；每行一项，或直接填一个列表变量"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "结果", ValueKind.text, "random",
                Description: "随机结果（整数 / 字符串 / GUID / 抽中的那一项）"),
            new OutputSpec("number", "数值", ValueKind.number,
                Description: "随机整数方式：就是那个整数；抽一项：抽中的下标（0 起）；其余为 0"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim().ToLowerInvariant();

        switch (op)
        {
            case "number":
            {
                long a = (long)Math.Round(args.Number("min"));
                long b = (long)Math.Round(args.Number("max"));
                if (a > b) (a, b) = (b, a);
                long value = a == b ? a : Random.Shared.NextInt64(a, b + 1);
                ctx.SetOutput("text", Value.FromText(value.ToString()));
                ctx.SetOutput("number", Value.FromNumber(value));
                break;
            }

            case "text":
            {
                string pool = Pool(args.Text("charset"));
                int length = (int)Math.Clamp(args.Number("length"), 1, 4096);
                var sb = new StringBuilder(length);
                for (int i = 0; i < length; i++) sb.Append(pool[Random.Shared.Next(pool.Length)]);
                ctx.SetOutput("text", Value.FromText(sb.ToString()));
                ctx.SetOutput("number", Value.FromNumber(0));
                break;
            }

            case "guid":
            {
                string guid = Guid.NewGuid().ToString("D");
                ctx.SetOutput("text", Value.FromText(guid));
                ctx.SetOutput("number", Value.FromNumber(0));
                break;
            }

            case "pick":
            {
                var list = args.Has("list") ? args.List("list") : Array.Empty<Value>();
                if (list.Count == 0) throw new ActionException("列表是空的，抽不出东西");
                int index = Random.Shared.Next(list.Count);
                ctx.SetOutput("text", Value.FromText(list[index].AsText()));
                ctx.SetOutput("number", Value.FromNumber(index));
                break;
            }

            default:
                throw new ActionException($"未知方式：{op}");
        }
    }

    private static string Pool(string charset) => (charset ?? "").Trim().ToLowerInvariant() switch
    {
        "letters" => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz",
        "digits" => "0123456789",
        "hex" => "0123456789abcdef",
        "lower" => "abcdefghijklmnopqrstuvwxyz",
        "upper" => "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        "alnum" or "" => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789",
        _ => throw new ActionException($"未知字符集：{charset}"),
    };
}
