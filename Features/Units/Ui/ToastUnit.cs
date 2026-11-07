using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Units.Ui;

/// <summary>提示弹窗：右下角显示一段文字，投递到 UI 线程即返回，不阻塞后续步骤。</summary>
public sealed class ToastUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "ui.toast",
        Name = "提示弹窗",
        Category = "界面",
        Icon = "toast",
        Description = "右下角显示一条提示，不阻塞后续步骤、不抢焦点",
        Inputs = new[]
        {
            new InputSpec("text", "内容", ValueKind.text, InputEditor.multiline, Required: true),
            new InputSpec("title", "标题", ValueKind.text),
            new InputSpec("durationMs", "停留毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(ToastService.DefaultDurationMs)),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string text = args.Text("text");
        if (text.Length == 0) return;   // 空内容没什么可看的，和旧实现一致

        string title = args.Text("title").Trim();
        int duration = (int)Math.Clamp(args.Number("durationMs"), 0, 600_000);

        ToastService.Show(text, title.Length == 0 ? null : title, duration);
    }
}
