using System.Text.RegularExpressions;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Text;

/// <summary>读取前台应用里选中的文本（UI Automation，必要时退回 WM_COPY）。</summary>
public sealed class SelectionUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "text.selection",
        Name = "读取选中文本",
        Category = "键鼠与输入",
        Icon = "selection",
        Description = "读取前台应用里当前选中的文本；没有选中时返回空文本",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("timeoutMs", "超时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(800)),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "文本", ValueKind.text, "selection", Description: "选中的文本；没选中时为空"),
            new OutputSpec("found", "有内容", ValueKind.@bool, Description: "是否取到选中文本"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        int timeout = (int)Math.Clamp(args.Number("timeoutMs"), 0, 10_000);
        string text = SelectionCapture.Capture(timeout);
        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("found", Value.FromBool(text.Length > 0));
    }
}

/// <summary>发送文本：自动（Unicode 注入）/ 键入 / 粘贴。</summary>
public sealed class SendTextUnit : IStepUnit
{
    private static readonly string[] Modes = { "auto", "type", "paste" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "text.send",
        Name = "发送文本",
        Category = "键鼠与输入",
        Icon = "keyboard",
        Description = "把文本发送到当前焦点窗口；自动模式不碰剪贴板、不受输入法影响",
        Inputs = new[]
        {
            new InputSpec("text", "文本", ValueKind.text, InputEditor.multiline, Required: true),
            new InputSpec("mode", "方式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("auto"), Options: Modes),
            new InputSpec("appendEnter", "末尾回车", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false)),
            new InputSpec("focusDelayMs", "焦点延时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(50)),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string text = args.Text("text");
        if (text.Length == 0) throw new ActionException("发送文本的内容为空");

        int delay = (int)Math.Clamp(args.Number("focusDelayMs"), 0, 10_000);
        if (delay > 0 && !ctx.Wait(delay))
            throw new ActionAbortedException("已停止", userCancelled: true);

        string mode = args.Text("mode").Trim().ToLowerInvariant();
        TextSender.Dispatch(text, mode switch
        {
            "type" => SendTextMode.type,
            "paste" => SendTextMode.paste,
            "auto" or "" => SendTextMode.auto,
            _ => throw new ActionException($"发送方式只能是 auto / type / paste（当前 {mode}）"),
        }, "发送文本");

        if (args.Bool("appendEnter"))
            InputHelper.Tap((ushort)Win32.VkReturn);
    }
}

/// <summary>文本处理：去空白 / 大小写 / 替换 / 截取 / 拆分 / 查找。</summary>
public sealed class ProcessTextUnit : IStepUnit
{
    private static readonly string[] Operations =
    {
        "trim", "upper", "lower", "length", "substring", "replace",
        "split", "join", "contains", "indexOf", "startsWith", "endsWith",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "text.process",
        Name = "文本处理",
        Category = "文本与数据",
        Icon = "text",
        Description = "去空白、改大小写、替换、截取、拆分、拼接、查找",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("trim"), Options: Operations),
            new InputSpec("text", "文本", ValueKind.text, InputEditor.multiline,
                Description: "要处理的文本；可写 ${变量}"),
            new InputSpec("list", "列表", ValueKind.list,
                Description: "拆分 / 拼接时用：列表变量，或每行一项"),
            new InputSpec("find", "查找", ValueKind.text,
                Description: "替换 / 查找时要比对的内容"),
            new InputSpec("replace", "替换为", ValueKind.text,
                Description: "替换成什么；留空 = 删掉"),
            new InputSpec("separator", "分隔符", ValueKind.text, Default: Value.FromText(","),
                Description: "拆分 / 拼接用的分隔符，例如 , 或 \\n"),
            new InputSpec("start", "起始位置", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), VisibleWhen: "operation == 'substring'",
                Description: "从 0 开始数；超过文本长度会报错"),
            new InputSpec("count", "长度", ValueKind.number, InputEditor.number,
                VisibleWhen: "operation == 'substring'",
                Description: "取多少个字符；留空或 0 = 一直取到末尾"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "结果文本", ValueKind.text, "text", Description: "处理后的文本"),
            new OutputSpec("list", "结果列表", ValueKind.list, Description: "拆分 / 排序后的列表"),
            new OutputSpec("number", "数值", ValueKind.number, Description: "长度或位置等数值结果"),
            new OutputSpec("found", "成立", ValueKind.@bool, Description: "查找是否命中"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim().ToLowerInvariant();
        string text = args.Text("text");
        string find = args.Text("find");
        string separator = args.Text("separator");

        string result = text;
        IReadOnlyList<Value>? list = null;
        double? number = null;
        bool? found = null;

        switch (op)
        {
            case "trim":
                result = text.Trim();
                break;

            case "upper":
                result = text.ToUpperInvariant();
                break;

            case "lower":
                result = text.ToLowerInvariant();
                break;

            case "length":
                number = text.Length;
                result = text.Length.ToString();
                break;

            case "substring":
            {
                int start = (int)Math.Round(args.Number("start"));
                if (start < 0 || start > text.Length)
                    throw new ActionException($"截取起始位置越界：{start}（文本共 {text.Length} 字符）");

                double rawCount = args.Has("count") ? args.Number("count") : 0;
                int length = rawCount > 0 ? (int)Math.Round(rawCount) : text.Length - start;
                if (start + length > text.Length) length = text.Length - start;
                result = text.Substring(start, length);
                break;
            }

            case "replace":
                result = text.Replace(find, args.Text("replace"), StringComparison.Ordinal);
                break;

            case "split":
                if (separator.Length == 0) separator = ",";
                list = text.Split(separator, StringSplitOptions.None)
                    .Select(part => Value.FromText(part)).ToList();
                number = list.Count;
                break;

            case "join":
            {
                var items = args.Has("list") ? args.List("list") : text.Length == 0
                    ? Array.Empty<Value>()
                    : new[] { Value.FromText(text) };
                result = string.Join(separator, items.Select(v => v.AsText()));
                break;
            }

            case "contains":
                found = text.Contains(find, StringComparison.OrdinalIgnoreCase);
                result = found.Value ? "true" : "false";
                break;

            case "indexOf":
                number = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
                result = number.Value.ToString("0");
                break;

            case "startsWith":
                found = text.StartsWith(find, StringComparison.OrdinalIgnoreCase);
                result = found.Value ? "true" : "false";
                break;

            case "endsWith":
                found = text.EndsWith(find, StringComparison.OrdinalIgnoreCase);
                result = found.Value ? "true" : "false";
                break;

            default:
                throw new ActionException($"未知文本操作：{op}");
        }

        ctx.SetOutput("text", Value.FromText(result));
        if (list != null) ctx.SetOutput("list", Value.FromList(list));
        if (number != null) ctx.SetOutput("number", Value.FromNumber(number.Value));
        if (found != null) ctx.SetOutput("found", Value.FromBool(found.Value));
    }
}

/// <summary>用正则把文本里所有命中提取成列表。</summary>
public sealed class RegexUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "text.regex",
        Name = "正则提取",
        Category = "文本与数据",
        Icon = "filter",
        Description = "找出全部命中（或其中某一组）放进列表；没命中只是「找到」为假",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("text", "文本", ValueKind.text, InputEditor.multiline, Required: true,
                Description: "要匹配的文本；可写 ${变量}"),
            new InputSpec("pattern", "正则", ValueKind.text, Required: true,
                Description: "正则表达式（.NET 写法）；写错会直接失败"),
            new InputSpec("group", "分组", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0),
                Description: "取第几个括号分组；0 = 整个匹配"),
            new InputSpec("ignoreCase", "忽略大小写", ValueKind.@bool, InputEditor.boolean),
        },
        Outputs = new[]
        {
            new OutputSpec("list", "列表", ValueKind.list, "matches",
                Description: "全部命中（取分组时是那个分组的文本），可直接给「遍历列表」"),
            new OutputSpec("count", "数量", ValueKind.number, Description: "命中个数"),
            new OutputSpec("text", "第一个", ValueKind.text, Description: "第一个命中的文本"),
            new OutputSpec("found", "找到", ValueKind.@bool, Description: "是否至少命中一次"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string text = args.Text("text");
        string pattern = args.Text("pattern");
        if (pattern.Length == 0) throw new ActionException("正则为空");

        int group = (int)Math.Round(args.Number("group"));
        if (group < 0) throw new ActionException($"分组不能是负数（当前 {group}）");

        var options = RegexOptions.CultureInvariant;
        if (args.Bool("ignoreCase")) options |= RegexOptions.IgnoreCase;

        var matches = new List<Value>();
        try
        {
            var regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
            foreach (Match match in regex.Matches(text))
            {
                if (group >= match.Groups.Count)
                    throw new ActionException(
                        $"正则只有 {match.Groups.Count - 1} 个分组（当前要第 {group} 组）");

                matches.Add(Value.FromText(match.Groups[group].Value));
            }
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new ActionException("正则匹配超过 2 秒仍未结束，检查是否写成了灾难性回溯", ex);
        }
        catch (ArgumentException ex)
        {
            throw new ActionException($"正则写法不对：{ex.Message}", ex);
        }

        ctx.SetOutput("list", Value.FromList(matches));
        ctx.SetOutput("count", Value.FromNumber(matches.Count));
        ctx.SetOutput("text", Value.FromText(matches.Count > 0 ? matches[0].AsText() : ""));
        ctx.SetOutput("found", Value.FromBool(matches.Count > 0));
    }
}
