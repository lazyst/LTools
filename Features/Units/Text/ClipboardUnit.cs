using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Text;

/// <summary>
/// 剪贴板：<c>operation = read | write</c>，<c>format = text | html | rtf</c>。
/// 读不到内容不是错误（ok=false）。
/// </summary>
public sealed class ClipboardUnit : IStepUnit
{
    private static readonly string[] Operations = { "read", "write" };
    private static readonly string[] Formats = { "text", "html", "rtf" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "text.clipboard",
        Name = "剪贴板",
        Category = "剪贴板",
        Icon = "clipboard",
        Description = "读取或写入系统剪贴板（纯文本 / HTML / RTF）",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("read"), Options: Operations),
            new InputSpec("format", "格式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("text"), Options: Formats,
                Description: "纯文本 / HTML / RTF；读写都按这个格式"),
            new InputSpec("text", "文本", ValueKind.text, InputEditor.multiline,
                VisibleWhen: "operation == 'write'",
                Description: "要写进剪贴板的内容；HTML 写片段即可，RTF 写完整的 {\\rtf1…}"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "文本", ValueKind.text, "text", Description: "剪贴板文本；写入时是刚写进去的内容"),
            new OutputSpec("ok", "成功", ValueKind.@bool, Description: "是否成功"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string operation = args.Text("operation").Trim().ToLowerInvariant();
        string format = args.Text("format").Trim().ToLowerInvariant();
        if (format.Length == 0) format = "text";
        if (Array.IndexOf(Formats, format) < 0)
            throw new ActionException($"剪贴板格式只能是 text / html / rtf（当前 {format}）");

        if (operation == "write")
        {
            string text = args.Text("text");
            bool ok = format switch
            {
                "html" => NativeClipboard.SetHtml(text),
                "rtf" => NativeClipboard.SetRtf(text),
                _ => NativeClipboard.SetText(text),
            };

            if (!ok)
                throw new ActionException("写入剪贴板失败（可能被其他程序占用）");

            ctx.SetOutput("text", Value.FromText(text));
            ctx.SetOutput("ok", Value.FromBool(true));
            ctx.SetOutput("value", Value.Object(
                ("text", Value.FromText(text)),
                ("format", Value.FromText(format)),
                ("found", Value.FromBool(true))));
            return;
        }

        if (operation != "read")
            throw new ActionException($"剪贴板操作只能是 read / write（当前 {operation}）");

        bool read;
        string clipboard = "";
        switch (format)
        {
            case "html":
                read = NativeClipboard.TryGetHtml(out clipboard);
                break;
            case "rtf":
                read = NativeClipboard.TryGetRtf(out clipboard);
                break;
            default:
                read = NativeClipboard.TryGetText(out clipboard);
                break;
        }

        ctx.SetOutput("text", Value.FromText(clipboard));
        ctx.SetOutput("ok", Value.FromBool(read));
        ctx.SetOutput("value", Value.Object(
            ("text", Value.FromText(clipboard)),
            ("format", Value.FromText(format)),
            ("found", Value.FromBool(read))));
    }
}
