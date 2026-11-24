using System;
using System.Linq;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Link;
using LTools.Native;
using LTools.Views;

namespace LTools.Features.Units.Screen;

/// <summary>屏幕取点：弹一次取点遮罩，把用户点的那一处坐标交给流程。</summary>
/// <remarks>
/// <para>
/// 交互与「截图」的框选同源（<see cref="ScreenPicker"/>）：先把 LTools 自己的窗口最小化、冻结光标所在显示器、
/// 只留十字准线；单击确定，右键 / Esc 取消。取点窗口内部不看 DPI——坐标是虚拟桌面物理像素，
/// 与截图 / 找图 / 鼠标注入同一套。
/// </para>
/// <para>
/// **取消不是失败**：返回 <c>found = false</c>，由调用方用「如果」判断（与截图同口径）。
/// 干跑（MCP <c>dryRun</c>）不弹遮罩，直接用当前光标位置，免得把 agent 卡在一个全屏遮罩上。
/// </para>
/// <para>
/// 线程：取点窗口是 WPF 窗口，必须建在 UI 线程；流程在后台线程，这里用 <c>Dispatcher.Invoke</c>
/// 同步等用户点完（这一步本来就在等用户，同步不会拖慢别的步骤）。
/// 已知限制：遮罩弹出期间 UI 线程被占，按停止热键要等用户点完才生效——与截图一致，别在这一层另做一套。
/// </para>
/// </remarks>
public sealed class PickPointUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "screen.pickPoint",
        Name = "屏幕取点",
        Category = "屏幕",
        Icon = "pickPoint",
        Description = "弹一次取点遮罩，把用户点的那一处坐标作为 x / y 输出；取消时 found = false，不报错",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("hint", "提示文字", ValueKind.text, InputEditor.text,
                Description: "取点遮罩上给人看的一句话（如「点一下要定位的按钮」）；留空用默认提示"),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "pickX", Description: "取到的屏幕绝对坐标 X"),
            new OutputSpec("y", "Y", ValueKind.number, "pickY", Description: "取到的屏幕绝对坐标 Y"),
            new OutputSpec("found", "取到点", ValueKind.@bool, Description: "是否取到点；用户取消时为 false，不是错误"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        if (ctx.DryRun)
        {
            // 干跑不弹遮罩：用当前光标位置顶上，后面的步骤照样能拿到一个合法坐标。
            if (!Win32.GetCursorPos(out var pt))
                throw ActionErrors.Precondition("取不到当前光标位置，屏幕取点无法在干跑里给出坐标");
            Write(ctx, pt.X, pt.Y);
            return;
        }

        var app = Application.Current ?? throw new ActionException("界面未初始化，无法在屏幕上取点");
        string hint = args.Text("hint").Trim();

        // 自动化指示器是置顶窗口，会被拍进取点画面：取点前收起，结束再恢复。
        // 遮罩现在跑在宿主里（普通权限），冻结画面由它拍——所以执行器这边的卡片也得先藏起来。
        string? indicator = RecordingIndicatorWindow.HideAutomationForCapture();
        using var suppressed = ToastService.SuppressForCapture();
        try
        {
            // 界面归宿主：转过去弹遮罩；没连上再本地弹（体验降级，不卡流程）。
            UiPickPointReply? remote = UiBridge.TryPickPoint(hint, ctx.Token);
            bool ok;
            int x;
            int y;
            if (remote != null)
            {
                ok = remote.Ok;
                x = remote.X;
                y = remote.Y;
            }
            else
            {
                (ok, x, y) = app.Dispatcher.Invoke(() =>
                {
                    // owner 必须在 UI 线程上找：Application.Windows 属于 UI 线程。
                    Window? owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
                    bool picked = ScreenPicker.TryPickPoint(owner, hint, out int px, out int py);
                    return (picked, px, py);
                });
            }

            if (!ok)
            {
                AppLog.Info("PickPoint", "屏幕取点：用户取消");
                ctx.SetOutput("x", Value.FromNumber(0));
                ctx.SetOutput("y", Value.FromNumber(0));
                ctx.SetOutput("found", Value.FromBool(false));
                ctx.SetOutput("value", UnitValues.NotFound());
                return;
            }

            AppLog.Info("PickPoint", $"屏幕取点：{x},{y}");
            Write(ctx, x, y);
        }
        finally
        {
            RecordingIndicatorWindow.RestoreAutomation(indicator);
        }
    }

    private static void Write(StepContext ctx, int x, int y)
    {
        ctx.SetOutput("x", Value.FromNumber(x));
        ctx.SetOutput("y", Value.FromNumber(y));
        ctx.SetOutput("found", Value.FromBool(true));
        ctx.SetOutput("value", UnitValues.Point(x, y));
    }
}
