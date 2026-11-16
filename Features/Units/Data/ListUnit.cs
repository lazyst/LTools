using System.Globalization;
using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>列表处理：取项 / 拼接 / 拆分 / 查找 / 排序 / 增删。</summary>
public sealed class ListUnit : IStepUnit
{
    private static readonly string[] Operations =
    {
        "count", "sum", "avg", "get", "join", "split", "indexOf", "contains",
        "sort", "unique", "reverse", "add", "removeAt",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "data.list",
        Name = "列表操作",
        Category = "文本与数据",
        Icon = "list",
        Description = "取项、拼接、拆分、查找、排序、去重、反转、追加、删除、求和 / 平均",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("count"), Options: Operations,
                OptionParams: OptionMap.Of(
                    ("count", ["list"]),
                    ("sum", ["list"]),
                    ("avg", ["list"]),
                    ("get", ["list", "index"]),
                    ("join", ["list", "separator"]),
                    ("split", ["value", "separator", "list"]),
                    ("indexOf", ["list", "value"]),
                    ("contains", ["list", "value"]),
                    ("sort", ["list"]),
                    ("unique", ["list"]),
                    ("reverse", ["list"]),
                    ("add", ["list", "value"]),
                    ("removeAt", ["list", "index"]))),
            new InputSpec("list", "列表", ValueKind.list,
                Description: "要处理的列表；拆分时可留空、改用下面的「值」"),
            new InputSpec("index", "下标", ValueKind.number, InputEditor.number, Default: Value.FromNumber(0),
                Description: "从 0 开始数；取项 / 删除时用"),
            new InputSpec("value", "值", Editor: InputEditor.text,
                Description: "拆分时是要拆的文本；追加 / 查找时是那一项"),
            new InputSpec("separator", "分隔符", ValueKind.text,
                Pickers: PickerKind.variable | PickerKind.separator | PickerKind.inline | PickerKind.clear,
                Default: Value.FromText(","),
                Description: "拆分 / 拼接用的分隔符；换行写 \\n、制表符写 \\t，例如 , 或 \\r\\n"),
        },
        Outputs = new[]
        {
            new OutputSpec("list", "列表", ValueKind.list, "list", Description: "操作后的列表（排序 / 去重 / 反转 / 追加 / 删除 / 拆分）"),
            new OutputSpec("text", "文本", ValueKind.text, "text", Description: "取出的那一项，或拼接出来的文本；求和 / 平均时是数字的文本形式"),
            new OutputSpec("number", "数值", ValueKind.number, Description: "数量、下标，或求和 / 平均的结果（count / split / indexOf / sum / avg 等）"),
            new OutputSpec("found", "成立", ValueKind.@bool, Description: "contains / indexOf 是否命中"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim().ToLowerInvariant();
        var items = args.Has("list") ? args.List("list").ToList() : new List<Value>();
        int index = (int)Math.Round(args.Number("index"));
        string separator = TextEscapes.Unescape(args.Text("separator"));
        if (separator.Length == 0) separator = ",";
        Value value = args.Has("value") ? args.Get("value") : Value.Empty;

        switch (op)
        {
            case "count":
                ctx.SetOutput("number", Value.FromNumber(items.Count));
                ctx.SetOutput("list", Value.FromList(items));
                return;

            case "sum":
            case "avg":
            {
                double result = Aggregate(items, op);

                ctx.SetOutput("number", Value.FromNumber(result));
                ctx.SetOutput("text", Value.FromText(result.ToString("0.############", CultureInfo.InvariantCulture)));
                ctx.SetOutput("list", Value.FromList(items));
                return;
            }

            case "get":
                if (index < 0 || index >= items.Count)
                    throw new ActionException($"列表下标越界：{index}（共 {items.Count} 项）");
                ctx.SetOutput("text", Value.FromText(items[index].AsText()));
                ctx.SetOutput("list", Value.FromList(items));
                return;

            case "join":
                ctx.SetOutput("text", Value.FromText(
                    string.Join(separator, items.Select(v => v.AsText()))));
                return;

            case "split":
            {
                string text = args.Has("value") ? value.AsText() : args.Text("list");
                var split = text.Split(separator, StringSplitOptions.None)
                    .Select(part => Value.FromText(part)).ToList();
                ctx.SetOutput("list", Value.FromList(split));
                ctx.SetOutput("number", Value.FromNumber(split.Count));
                return;
            }

            case "indexOf":
            {
                string needle = value.AsText();
                int found = items.FindIndex(v =>
                    string.Equals(v.AsText(), needle, StringComparison.OrdinalIgnoreCase));
                ctx.SetOutput("number", Value.FromNumber(found));
                ctx.SetOutput("found", Value.FromBool(found >= 0));
                return;
            }

            case "contains":
            {
                string needle = value.AsText();
                bool found = items.Any(v =>
                    string.Equals(v.AsText(), needle, StringComparison.OrdinalIgnoreCase));
                ctx.SetOutput("found", Value.FromBool(found));
                return;
            }

            case "sort":
                items.Sort((a, b) => string.Compare(a.AsText(), b.AsText(), StringComparison.OrdinalIgnoreCase));
                ctx.SetOutput("list", Value.FromList(items));
                ctx.SetOutput("number", Value.FromNumber(items.Count));
                return;

            case "unique":
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                items = items.Where(v => seen.Add(v.AsText())).ToList();
                ctx.SetOutput("list", Value.FromList(items));
                ctx.SetOutput("number", Value.FromNumber(items.Count));
                return;
            }

            case "reverse":
                items.Reverse();
                ctx.SetOutput("list", Value.FromList(items));
                ctx.SetOutput("number", Value.FromNumber(items.Count));
                return;

            case "add":
                items.Add(value);
                ctx.SetOutput("list", Value.FromList(items));
                ctx.SetOutput("number", Value.FromNumber(items.Count));
                return;

            case "removeAt":
                if (index < 0 || index >= items.Count)
                    throw new ActionException($"列表下标越界：{index}（共 {items.Count} 项）");
                items.RemoveAt(index);
                ctx.SetOutput("list", Value.FromList(items));
                ctx.SetOutput("number", Value.FromNumber(items.Count));
                return;

            default:
                throw new ActionException($"未知列表操作：{op}");
        }
    }

    /// <summary>
    /// 求和 / 平均（<c>sum</c> / <c>avg</c>）：纯函数，动作单元只负责把结果写进输出。
    /// </summary>
    /// <remarks>
    /// 空列表：两者都是 0，不报错——「没数据」很常见，要不要当失败由调用方按 <c>count</c> 判断。
    /// 有非数字项：**整步失败**并指出第几项（静默跳过会给出一个错的和）。
    /// </remarks>
    internal static double Aggregate(IReadOnlyList<Value> items, string op)
    {
        double total = 0;
        for (int i = 0; i < items.Count; i++) total += ItemNumber(items[i], i);

        return op == "avg" && items.Count > 0 ? total / items.Count : total;
    }

    /// <summary>
    /// 列表项 → 数字（与参数绑定的隐式转换同一套规则：文本按不变文化解析，布尔 true = 1）。
    /// </summary>
    private static double ItemNumber(Value item, int position)
    {
        try { return item.AsNumber(); }
        catch (ActionException ex)
        {
            throw new ActionException($"第 {position + 1} 项「{item.AsText()}」不是数字", ex);
        }
    }
}

/// <summary>解析 JSON 文本，按 <c>a.b[0]</c> 取其中一段。</summary>
