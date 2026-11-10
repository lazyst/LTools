using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Features.Units.Input;

/// <summary>
/// 等用户按一个键 / 组合键（<c>input.waitKey</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 「按任意键继续」「等用户按 Esc 退出」这类交互的地基。等待期间流程一直等，
/// 停止热键照常有效；超时没等到只是 <c>found = false</c>，不是失败。
/// </para>
/// <para>
/// 只吞命中的那个键（默认），用户在别的窗口打字不受影响；<c>keys</c> 留空 = 任意键，
/// 写法与「模拟按键」一致（<c>f2</c> / <c>ctrl+s</c> / <c>esc</c>）。
/// </para>
/// </remarks>
public sealed class WaitKeyUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "input.waitKey",
        Name = "等待按键",
        Category = "键鼠与输入",
        Icon = "keyboard",
        Description = "等用户按一个键或组合键；留空 = 任意键，超时返回 found = false（不是失败）",
        Inputs = new[]
        {
            new InputSpec("keys", "按键", ValueKind.text, InputEditor.text,
                Pickers: PickerKind.variable | PickerKind.keys | PickerKind.inline | PickerKind.clear,
                Description: "留空 = 任意键；写法同「模拟按键」：f2 / ctrl+s / esc / enter"),
            new InputSpec("swallow", "吞掉这个键", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(true), Advanced: true,
                Description: "勾上时目标程序收不到这个键（「按任意键继续」通常是想要的）；不勾则照常传给它"),
            new InputSpec("timeoutMs", "超时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), Advanced: true,
                Description: "0 = 一直等（按停止热键可中断）；到点没按就返回 found = false"),
        },
        Outputs = new[]
        {
            new OutputSpec("key", "按下的键", ValueKind.text, "key",
                Description: "canonical 写法（ctrl+s）；超时为空"),
            new OutputSpec("found", "等到了", ValueKind.@bool,
                Description: "用户是否按下了期望的键（超时为假）"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string keys = args.Text("keys").Trim();
        bool swallow = args.Bool("swallow");
        int timeoutMs = (int)Math.Clamp(args.Number("timeoutMs"), 0, 3_600_000);

        var waiter = KeyWaiter.Arm(keys, swallow);
        try
        {
            DateTime? deadline = timeoutMs > 0 ? DateTime.UtcNow.AddMilliseconds(timeoutMs) : null;
            while (!waiter.Done)
            {
                if (deadline.HasValue && DateTime.UtcNow >= deadline.Value) break;
                if (!ctx.Wait(50)) throw new ActionAbortedException("已停止", userCancelled: true);
            }
        }
        finally
        {
            KeyWaiter.Disarm(waiter);
        }

        ctx.SetOutput("key", Value.FromText(waiter.Chord));
        ctx.SetOutput("found", Value.FromBool(waiter.Done));
    }
}
