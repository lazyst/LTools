using System;
using System.Linq;
using System.Windows;
using LTools.Core;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 「实时截图」取值器：流程跑到这一步时，先把 LTools 的窗口让开、冻结屏幕，
/// 让你自己框一块（可吸附窗口 / 控件），然后把这块区域和裁下来的图片交给流程。
/// </summary>
/// <remarks>
/// <para>
/// 输出（<c>${变量}</c> = <c>x,y,w,h</c>）：
/// <c>.x</c> / <c>.y</c> 左上角、<c>.w</c> / <c>.h</c> 尺寸、<c>.cx</c> / <c>.cy</c> 中心点、
/// <c>.rect</c> 同文本、<c>.file</c> 存下来的 png 路径、<c>.found</c> 是否框选成功。
/// </para>
/// <para>
/// 用户右键 / Esc 取消时返回空值（<c>found=false</c>），**不中止流程**——和「输入文本」的取消一致，
/// 后续步骤可以用「条件：found 为真」决定要不要继续。
/// </para>
/// <para>
/// 图片存进 <c>resources/images</c>（与模板截图同一目录，按引用回收），所以运行几次不会越堆越多。
/// </para>
/// <para>
/// 线程：框选窗口是 WPF 窗口，必须建在 UI 线程；流程在后台线程，这里用 <see cref="Dispatcher.Invoke(Delegate)"/>
/// 同步等结果（这一步本来就是等用户操作，同步不会拖慢别的步骤）。
/// </para>
/// </remarks>
internal static class ScreenshotProbe
{
    /// <summary>让用户框一块屏幕区域。取消时返回 <c>found=false</c> 的空值。</summary>
    public static VarValue Run(Probe probe, RunContext ctx)
    {
        if (ctx.IsCancelled) throw new FlowAbortException("已停止", userCancelled: true);

        var app = Application.Current ?? throw new FlowException("界面未初始化，无法实时截图");

        // 自动化指示器是置顶窗口，会被截进画面：截图前收起，结束再恢复。
        string? indicator = RecordingIndicatorWindow.HideAutomationForCapture();
        try
        {
            var (ok, area, image) = app.Dispatcher.Invoke(() =>
            {
                // owner 必须在 UI 线程上找：Application.Windows 属于 UI 线程。
                Window? owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
                bool picked = ScreenPicker.TryPickRegion(owner, out var a, out var img);
                return (picked, a, picked ? img : null);
            });

            if (!ok || image == null)
            {
                AppLog.Info("Probe", "实时截图：用户取消");
                return new VarValue { Text = "", Flag = false };
            }

            string file = ImageLibrary.SaveGenerated(image);
            AppLog.Info("Probe", $"实时截图：{area.X},{area.Y} · {area.Width} × {area.Height} → {file}");
            return VarValue.FromRegion(area.X, area.Y, area.Width, area.Height, file);
        }
        finally
        {
            RecordingIndicatorWindow.RestoreAutomation(indicator);
        }
    }
}
