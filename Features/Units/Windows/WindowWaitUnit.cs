using System.Diagnostics;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Windows;

/// <summary>
/// 等窗口出现（<c>window.wait</c>）：标题包含 + 类名，最多等 <c>timeoutMs</c>。
/// </summary>
/// <remarks>
/// <para>
/// 桌面自动化最常见的失败是「程序还没起来 / 对话框还没弹出来」：以前得写「查找窗口 → 如果没找到 →
/// 延迟 → 回到开头」这一套循环，还得自己计数防死循环。这一步把等待收进动作单元内部，
/// 匹配语义与「查找窗口」「窗口操作」完全一致（都走 <see cref="WindowFinder"/>）。
/// </para>
/// <para>
/// **超时不是失败**：到了上限还没出现就返回 <c>found = false</c>（与「查找窗口」「等待按键」同口径），
/// 后面用「如果」判断要不要报错 / 换条路走；要「等不到就停」就把这一步的「失败时」旁边的「如果」写在下一步。
/// 等待走 <see cref="StepContext.Wait"/>，按停止热键立刻返回。
/// </para>
/// <para>
/// 干跑（MCP <c>dryRun</c>）只查一次、不等待：等不到是正常结果，不该让草稿试运行卡满超时。
/// </para>
/// </remarks>
public sealed class WindowWaitUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "window.wait",
        Name = "等待窗口",
        Category = "窗口与程序",
        Icon = "window",
        DryRun = true,
        Kind = UnitKind.pure,
        Description = "等窗口出现（标题 / 类名）；超时返回 found = false，不是失败",
        Inputs = new[]
        {
            new InputSpec("title", "标题包含", ValueKind.text,
                Pickers: PickerKind.variable | PickerKind.window | PickerKind.inline | PickerKind.clear),
            new InputSpec("class", "类名", ValueKind.text,
                Pickers: PickerKind.variable | PickerKind.windowClass | PickerKind.inline | PickerKind.clear),
            new InputSpec("timeoutMs", "最多等", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(10_000),
                Description: "最多等多少毫秒；0 = 只查一次、不等待"),
            new InputSpec("intervalMs", "检查间隔", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(250), Advanced: true,
                Description: "多久查一次（毫秒），最短 20"),
        },
        Outputs = new[]
        {
            new OutputSpec("found", "出现了", ValueKind.@bool, "windowFound",
                Description: "超时前是否出现了匹配的窗口（超时为假）"),
            new OutputSpec("title", "标题", ValueKind.text, "windowTitle",
                Description: "匹配到的窗口标题；超时为空"),
            new OutputSpec("handle", "句柄", ValueKind.number, Description: "窗口句柄；超时为 0"),
            new OutputSpec("elapsed", "等了多久", ValueKind.number, "waitElapsed",
                Description: "从开始等到窗口出现（或超时）的毫秒数"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string title = args.Text("title").Trim();
        string cls = args.Text("class").Trim();
        if (title.Length == 0 && cls.Length == 0)
            throw ActionErrors.Precondition("等待窗口至少要填「标题包含」或「类名」");

        long started = Environment.TickCount64;
        int timeout = ctx.DryRun ? 0 : (int)Math.Clamp(args.Number("timeoutMs"), 0, 600_000);
        int interval = (int)Math.Clamp(args.Number("intervalMs"), 20, 60_000);

        while (true)
        {
            ctx.EnterIteration();

            IntPtr hwnd = WindowFinder.Find(title, cls, out string matched);
            bool found = hwnd != IntPtr.Zero;
            long elapsed = Environment.TickCount64 - started;
            long handle = found ? hwnd.ToInt64() : 0;
            bool active = found && hwnd == Win32.GetForegroundWindow();

            // 每轮都写输出：超时那一轮写的是「没找到」，配「失败继续」和「如果」都能直接看。
            ctx.SetOutput("found", Value.FromBool(found));
            ctx.SetOutput("title", Value.FromText(found ? matched : ""));
            ctx.SetOutput("handle", Value.FromNumber(handle));
            ctx.SetOutput("elapsed", Value.FromNumber(elapsed));
            ctx.SetOutput("value", UnitValues.Window(found ? matched : "", found, active, handle));

            if (found) return;

            // 超时只是 found = false，不抛错：要不要当失败由调用方用「如果」决定。
            if (elapsed >= timeout) return;

            int slice = (int)Math.Min(interval, Math.Max(1, timeout - elapsed));
            if (!ctx.Wait(slice))
                throw new ActionAbortedException("已停止", userCancelled: true);
        }
    }
}
