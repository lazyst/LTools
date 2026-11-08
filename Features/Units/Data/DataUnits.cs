using System.Globalization;
using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>写一条速记（与 CapsLock+N 同一个目录）。</summary>
public sealed class NoteWriteUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "data.noteWrite",
        Name = "写速记",
        Category = "数据",
        Icon = "note",
        Description = "把正文存进速记；标题留空时取正文第一行，分类留空 = 未分类",
        Inputs = new[]
        {
            new InputSpec("body", "正文", ValueKind.text, InputEditor.multiline, Required: true),
            new InputSpec("title", "标题", ValueKind.text),
            new InputSpec("category", "分类", ValueKind.text),
        },
        Outputs = new[]
        {
            new OutputSpec("title", "标题", ValueKind.text, "noteTitle", Description: "最终标题（没填标题时按时间生成）"),
            new OutputSpec("time", "时间", ValueKind.text, Description: "写入时间"),
            new OutputSpec("path", "文件", ValueKind.text, Description: "速记文件路径"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string body = args.Text("body");
        if (string.IsNullOrWhiteSpace(body)) throw new ActionException("写速记缺少正文");

        var result = NoteActions.Write(new NoteWriteRequest
        {
            Body = body,
            Title = args.Text("title"),
            Category = args.Text("category"),
        });

        ctx.SetOutput("title", Value.FromText(result.Title));
        ctx.SetOutput("time", Value.FromText(result.Time));
        ctx.SetOutput("path", Value.FromText(result.File));
        ctx.SetOutput("value", Value.Object(
            ("text", Value.FromText(result.File)),
            ("path", Value.FromText(result.File)),
            ("file", Value.FromText(result.File)),
            ("title", Value.FromText(result.Title)),
            ("time", Value.FromText(result.Time)),
            ("found", Value.FromBool(true))));
    }
}

/// <summary>读速记：按分类 + 关键字取最近修改的一条。</summary>
public sealed class NoteReadUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "data.noteRead",
        Name = "读速记",
        Category = "数据",
        Icon = "noteRead",
        Description = "按分类与关键字取最近修改的一条；没有匹配时 found = false",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("category", "分类", ValueKind.text),
            new InputSpec("filter", "关键字", ValueKind.text),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "正文", ValueKind.text, "note", Description: "命中的速记正文"),
            new OutputSpec("title", "标题", ValueKind.text, Description: "速记标题"),
            new OutputSpec("time", "时间", ValueKind.text, Description: "最后修改时间"),
            new OutputSpec("path", "文件", ValueKind.text, Description: "速记文件路径"),
            new OutputSpec("found", "找到", ValueKind.@bool, Description: "是否找到匹配的速记；没找到不是错误"),
            new OutputSpec("count", "匹配数量", ValueKind.number, Description: "匹配到的条数"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var probe = new Probe
        {
            Kind = ProbeKind.note,
            NoteCategory = args.Text("category"),
            Text = args.Text("filter"),
        };

        var value = LegacyFlow.Evaluate(ctx, legacy => NoteActions.Read(probe, legacy));
        string text = UnitValues.Member(value, "text")?.AsText() ?? "";
        string title = UnitValues.Member(value, "title")?.AsText() ?? "";
        string time = UnitValues.Member(value, "time")?.AsText() ?? "";
        string path = UnitValues.Member(value, "file")?.AsText() ?? "";
        bool found = UnitValues.Member(value, "found")?.AsBool() ?? false;
        int count = (int)Number(value, "number");

        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("title", Value.FromText(title));
        ctx.SetOutput("time", Value.FromText(time));
        ctx.SetOutput("path", Value.FromText(path));
        ctx.SetOutput("found", Value.FromBool(found));
        ctx.SetOutput("count", Value.FromNumber(count));
        ctx.SetOutput("value", Value.Object(
            ("text", Value.FromText(text)),
            ("title", Value.FromText(title)),
            ("time", Value.FromText(time)),
            ("path", Value.FromText(path)),
            ("file", Value.FromText(path)),
            ("found", Value.FromBool(found)),
            ("count", Value.FromNumber(count)),
            ("number", Value.FromNumber(count))));
    }

    private static double Number(Value value, string name)
    {
        var member = UnitValues.Member(value, name);
        if (member == null) return 0;
        try { return member.AsNumber(); }
        catch (ActionException) { return 0; }
    }
}

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
        Category = "数据",
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
public sealed class JsonUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "data.json",
        Name = "JSON",
        Category = "数据",
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
        Category = "数据",
        Icon = "calc",
        Description = "加减乘除、取余、幂、最值、绝对值、四舍五入",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("operation", "运算", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("add"), Options: Operations,
                Labels: OperationLabels),
            new InputSpec("a", "A", ValueKind.number, InputEditor.number, Required: true),
            new InputSpec("b", "B", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0)),
            new InputSpec("digits", "小数位", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), VisibleWhen: "operation == 'round'"),
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
public sealed class TimeUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "data.time",
        Name = "时间",
        Category = "数据",
        Icon = "clock",
        Description = "取当前时间并按格式输出；可加偏移毫秒",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("format", "格式", ValueKind.text,
                Default: Value.FromText("yyyy-MM-dd HH:mm:ss")),
            new InputSpec("offsetMs", "偏移毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0)),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "时间文本", ValueKind.text, "time", Description: "格式化后的时间文本"),
            new OutputSpec("timestamp", "Unix 毫秒", ValueKind.number, Description: "Unix 时间戳（秒）"),
            new OutputSpec("date", "日期", ValueKind.text, Description: "日期部分（yyyy-MM-dd）"),
            new OutputSpec("clock", "时分秒", ValueKind.text, Description: "时间部分（HH:mm:ss）"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var now = DateTime.Now.AddMilliseconds(args.Number("offsetMs"));
        string format = args.Text("format");
        if (format.Length == 0) format = "yyyy-MM-dd HH:mm:ss";

        ctx.SetOutput("text", Value.FromText(now.ToString(format, CultureInfo.InvariantCulture)));
        ctx.SetOutput("timestamp", Value.FromNumber(
            new DateTimeOffset(now).ToUnixTimeMilliseconds()));
        ctx.SetOutput("date", Value.FromText(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        ctx.SetOutput("clock", Value.FromText(now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
    }
}
