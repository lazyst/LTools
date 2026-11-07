using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Text;

/// <summary>
/// 剪贴板：<c>operation = read | write</c>。读不到文本不是错误（ok=false）。
/// </summary>
public sealed class ClipboardUnit : IStepUnit
{
    private static readonly string[] Operations = { "read", "write" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "text.clipboard",
        Name = "剪贴板",
        Category = "文本",
        Icon = "clipboard",
        Description = "读取或写入系统剪贴板文本",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("read"), Options: Operations),
            new InputSpec("text", "文本", ValueKind.text, InputEditor.multiline,
                VisibleWhen: "operation == 'write'"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "文本", ValueKind.text, "text"),
            new OutputSpec("ok", "成功", ValueKind.@bool),
            new OutputSpec("value", "完整结果", ValueKind.@object),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string operation = args.Text("operation").Trim().ToLowerInvariant();

        if (operation == "write")
        {
            string text = args.Text("text");
            if (!NativeClipboard.SetText(text))
                throw new ActionException("写入剪贴板失败（可能被其他程序占用）");

            ctx.SetOutput("text", Value.FromText(text));
            ctx.SetOutput("ok", Value.FromBool(true));
            ctx.SetOutput("value", Value.Object(
                ("text", Value.FromText(text)),
                ("found", Value.FromBool(true))));
            return;
        }

        if (operation != "read")
            throw new ActionException($"剪贴板操作只能是 read / write（当前 {operation}）");

        bool ok = NativeClipboard.TryGetText(out string clipboard);
        ctx.SetOutput("text", Value.FromText(clipboard));
        ctx.SetOutput("ok", Value.FromBool(ok));
        ctx.SetOutput("value", Value.Object(
            ("text", Value.FromText(clipboard)),
            ("found", Value.FromBool(ok))));
    }
}
