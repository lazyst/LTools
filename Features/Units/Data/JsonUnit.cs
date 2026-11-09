using System.Globalization;
using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>解析 JSON 文本，按 <c>a.b[0]</c> 取其中一段。</summary>
public sealed class JsonUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "data.json",
        Name = "JSON",
        Category = "文本与数据",
        Icon = "file",
        Description = "把 JSON 文本解析成值，可按 a.b[0] 取出列表 / 字段；语法错误直接失败，取不到只是「找到」为假",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("text", "JSON", ValueKind.text, InputEditor.multiline, Required: true,
                Description: "要解析的 JSON 文本；可以来自文件、命令输出或剪贴板（${变量}）"),
            new InputSpec("path", "取值路径", ValueKind.text,
                Description: "例如 a.b[0]；留空 = 取整个 JSON"),
        },
        Outputs = new[]
        {
            new OutputSpec("value", "结果", null,
                Description: "取到的值（保持原本类型，可直接交给别的单元）"),
            new OutputSpec("text", "文本", ValueKind.text, Description: "结果的文本形式（列表是 [a, b]）"),
            new OutputSpec("list", "列表", ValueKind.list, "list",
                Description: "结果是数组时才有：其中的每一项"),
            new OutputSpec("count", "数量", ValueKind.number,
                Description: "数组 / 对象的成员数，文本是长度"),
            new OutputSpec("type", "类型", ValueKind.text, Description: "text / number / bool / list / object"),
            new OutputSpec("found", "找到", ValueKind.@bool,
                Description: "路径是否存在（JSON 语法错误会直接失败，不算「找到」为假）"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string json = args.Text("text");
        if (json.Trim().Length == 0) throw new ActionException("没有 JSON 文本");

        Value root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = ValueJsonConverter.FromElement(doc.RootElement);
        }
        catch (JsonException ex)
        {
            long line = (ex.LineNumber ?? 0) + 1;
            throw new ActionException($"JSON 写法不对（第 {line} 行）：{ex.Message}", ex);
        }

        if (!TryResolve(root, args.Text("path"), out Value value))
        {
            ctx.SetOutput("found", Value.FromBool(false));
            ctx.SetOutput("value", Value.Empty);
            ctx.SetOutput("text", Value.FromText(""));
            ctx.SetOutput("count", Value.FromNumber(0));
            ctx.SetOutput("type", Value.FromText(""));
            return;
        }

        ctx.SetOutput("found", Value.FromBool(true));
        ctx.SetOutput("value", value);
        ctx.SetOutput("text", Value.FromText(value.AsText()));
        ctx.SetOutput("type", Value.FromText(value.Kind.ToString()));
        ctx.SetOutput("count", Value.FromNumber(value.Kind switch
        {
            ValueKind.list => value.Items.Count,
            ValueKind.@object => value.Members.Count,
            ValueKind.text => value.AsText().Length,
            _ => 0,
        }));

        if (value.Kind == ValueKind.list) ctx.SetOutput("list", value);
    }

    /// <summary>
    /// 按 <c>a.b[0].c</c> / <c>$..</c> 取值。路径不存在返回 false（取值器语义），
    /// 但形状不对（在文本上取成员、列表用名字取项）是配置错误，直接抛。
    /// </summary>
    private static bool TryResolve(Value root, string path, out Value value)
    {
        value = root;

        string expr = (path ?? "").Trim();
        if (expr.StartsWith('$')) expr = expr[1..];
        expr = expr.TrimStart('.');

        int i = 0;
        while (i < expr.Length)
        {
            if (expr[i] == '.')
            {
                i++;
                continue;
            }

            if (expr[i] == '[')
            {
                int close = expr.IndexOf(']', i);
                if (close < 0)
                    throw new ActionException($"取值路径缺少「]」：{path}");

                string indexText = expr[(i + 1)..close].Trim();
                if (!int.TryParse(indexText, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int index) || index < 0)
                    throw new ActionException($"取值路径里的下标不对：{indexText}");

                if (value.Kind != ValueKind.list)
                    throw new ActionException(
                        $"取值路径「{path}」用到下标，但当前值是 {value.Kind}（只有列表能按下标取）");
                if (index >= value.Items.Count) return false;

                value = value.Items[index];
                i = close + 1;
                continue;
            }

            int start = i;
            while (i < expr.Length && expr[i] != '.' && expr[i] != '[') i++;
            string name = expr[start..i].Trim();
            if (name.Length == 0) continue;

            if (value.Kind == ValueKind.list)
            {
                // 列表上允许 list.0：模板里 ${列表.0} 也是这么写的。
                if (!int.TryParse(name, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int index))
                    throw new ActionException(
                        $"「{name}」要作用在对象上，但当前值是列表（列表用下标，如 items[0]）");
                if (index < 0 || index >= value.Items.Count) return false;

                value = value.Items[index];
                continue;
            }

            if (value.Kind != ValueKind.@object)
                throw new ActionException(
                    $"取值路径「{path}」在 {value.Kind} 类型的值上取成员「{name}」");

            if (!value.Members.TryGetValue(name, out Value? member)) return false;
            value = member;
        }

        return true;
    }
}

/// <summary>数值计算。</summary>
