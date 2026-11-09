using System.Globalization;
using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>列表处理：取项 / 拼接 / 拆分 / 查找 / 排序 / 增删。</summary>
public sealed class ListUnit : IStepUnit
{
    private static readonly string[] Operations =
    {
        "count", "get", "join", "split", "indexOf", "contains",
        "sort", "unique", "reverse", "add", "removeAt",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "data.list",
        Name = "列表操作",
        Category = "文本与数据",
        Icon = "list",
        Description = "取项、拼接、拆分、查找、排序、去重、反转、追加、删除",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("count"), Options: Operations),
            new InputSpec("list", "列表", ValueKind.list),
            new InputSpec("index", "下标", ValueKind.number, InputEditor.number, Default: Value.FromNumber(0)),
            new InputSpec("value", "值", Editor: InputEditor.text),
            new InputSpec("separator", "分隔符", ValueKind.text, Default: Value.FromText(",")),
        },
        Outputs = new[]
        {
            new OutputSpec("list", "列表", ValueKind.list, "list", Description: "操作后的列表（排序 / 去重 / 反转 / 追加 / 删除 / 拆分）"),
            new OutputSpec("text", "文本", ValueKind.text, "text", Description: "取出的那一项，或拼接出来的文本"),
            new OutputSpec("number", "数值", ValueKind.number, Description: "数量或下标（count / split / indexOf 等）"),
            new OutputSpec("found", "成立", ValueKind.@bool, Description: "contains / indexOf 是否命中"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim().ToLowerInvariant();
        var items = args.Has("list") ? args.List("list").ToList() : new List<Value>();
        int index = (int)Math.Round(args.Number("index"));
        string separator = args.Text("separator");
        if (separator.Length == 0) separator = ",";
        Value value = args.Has("value") ? args.Get("value") : Value.Empty;

        switch (op)
        {
            case "count":
                ctx.SetOutput("number", Value.FromNumber(items.Count));
                ctx.SetOutput("list", Value.FromList(items));
                return;

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
}

/// <summary>解析 JSON 文本，按 <c>a.b[0]</c> 取其中一段。</summary>
