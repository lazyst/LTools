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
        Category = "文本",
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
            new OutputSpec("text", "文本", ValueKind.text, "selection"),
            new OutputSpec("found", "有内容", ValueKind.@bool),
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
        Category = "文本",
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
        Category = "文本",
        Icon = "text",
        Description = "去空白、改大小写、替换、截取、拆分、拼接、查找",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("trim"), Options: Operations),
            new InputSpec("text", "文本", ValueKind.text, InputEditor.multiline),
            new InputSpec("list", "列表", ValueKind.list),
            new InputSpec("find", "查找", ValueKind.text),
            new InputSpec("replace", "替换为", ValueKind.text),
            new InputSpec("separator", "分隔符", ValueKind.text, Default: Value.FromText(",")),
            new InputSpec("start", "起始位置", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), VisibleWhen: "operation == 'substring'"),
            new InputSpec("count", "长度", ValueKind.number, InputEditor.number,
                VisibleWhen: "operation == 'substring'"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "结果文本", ValueKind.text, "text"),
            new OutputSpec("list", "结果列表", ValueKind.list),
            new OutputSpec("number", "数值", ValueKind.number),
            new OutputSpec("found", "成立", ValueKind.@bool),
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
