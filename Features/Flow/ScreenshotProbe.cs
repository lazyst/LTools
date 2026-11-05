using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows;
using LTools.Core;
using LTools.Native;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 「截图」取值器：取一张屏幕截图交给流程（交互框选 / 前台窗口 / 整个屏幕 / 固定区域）。
/// </summary>
/// <remarks>
/// <para>
/// 取图与识字是分开的两步：这里只负责拿图（png 文件 + 区域坐标），
/// 要识别文字就把结果交给「文字识别」。
/// </para>
/// <para>
/// 输出（<c>${变量}</c> = <c>x,y,w,h</c>）：
/// <c>.x</c> / <c>.y</c> 左上角、<c>.w</c> / <c>.h</c> 尺寸、<c>.cx</c> / <c>.cy</c> 中心点、
/// <c>.rect</c> 同文本、<c>.file</c> 存下来的 png 路径、<c>.found</c> 是否取到图。
/// </para>
/// <para>
/// 交互框选时用户按 Esc 取消：返回空值（<c>found=false</c>），**不中止流程**——
/// 后续步骤可以用「条件：found 为真」决定要不要继续。其余三种方式取不到图会直接报错。
/// </para>
/// <para>
/// 图片存进 <c>resources/images</c>（与模板截图同一目录）：本次运行期间一直可用（后续「文字识别」、
/// 「打开文件」、写进剪贴板的路径都还能读），下一次运行开始时连同更早的没人引用的截图一起清掉，
/// 所以反复运行不会越堆越多。
/// </para>
/// <para>
/// 线程：框选窗口是 WPF 窗口，必须建在 UI 线程；流程在后台线程，这里用 <see cref="Dispatcher.Invoke(Delegate)"/>
/// 同步等结果（这一步本来就是等用户操作，同步不会拖慢别的步骤）。
/// </para>
/// </remarks>
internal static class ScreenshotProbe
{
    /// <summary>按 <see cref="Probe.ScreenshotMode"/> 取图；交互框选取消时返回 <c>found=false</c>。</summary>
    public static VarValue Run(Probe probe, RunContext ctx)
    {
        if (ctx.IsCancelled) throw new FlowAbortException("已停止", userCancelled: true);

        return probe.ScreenshotMode switch
        {
            ScreenshotMode.foreground => CaptureForeground(ctx),
            ScreenshotMode.screen => Capture(
                ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
                ScreenCapture.VirtualWidth, ScreenCapture.VirtualHeight, ctx, "整个屏幕"),
            ScreenshotMode.rect => CaptureRect(probe, ctx),
            _ => PickInteractive(ctx),
        };
    }

    // ============ 交互框选 ============

    private static VarValue PickInteractive(RunContext ctx)
    {
        var app = Application.Current ?? throw new FlowException("界面未初始化，无法截图");

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
                AppLog.Info("Probe", "截图：用户取消");
                return new VarValue { Text = "", Flag = false };
            }

            string file = ImageLibrary.SaveGenerated(image);
            AppLog.Info("Probe", $"截图 · 交互框选：{area.X},{area.Y} · {area.Width} × {area.Height} → {file}");
            return VarValue.FromRegion(area.X, area.Y, area.Width, area.Height, file);
        }
        finally
        {
            RecordingIndicatorWindow.RestoreAutomation(indicator);
        }
    }

    // ============ 自动取图 ============

    private static VarValue CaptureForeground(RunContext ctx)
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) throw new FlowException("当前没有前台窗口可截");

        // 先取范围再收指示器：万一指示器正好是前台窗口，也不会把它的坐标截进来。
        var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
        string title = WindowFinder.GetText(hwnd);
        return Capture(x, y, w, h, ctx, title.Length > 0 ? title : "前台窗口");
    }

    private static VarValue CaptureRect(Probe probe, RunContext ctx)
    {
        string spec = Template.Render(probe.ScreenshotRect ?? "", ctx).Trim();
        if (!TryParseRect(spec, out int x, out int y, out int w, out int h))
            throw new FlowException(
                $"截图区域要写成 x,y,w,h（可直接填区域变量，如 ${{截图}}），当前是「{spec}」");

        return Capture(x, y, w, h, ctx, $"区域 {x},{y} · {w}×{h}");
    }

    private static VarValue Capture(int x, int y, int w, int h, RunContext ctx, string label)
    {
        if (w <= 0 || h <= 0)
            throw new FlowException($"截图区域无效：{x},{y} · {w}×{h}");

        // 自动化指示器是置顶窗口，会被截进画面：截图前收起，结束再恢复。
        string? indicator = RecordingIndicatorWindow.HideAutomationForCapture();
        try
        {
            if (indicator != null && !ctx.Wait(80))
                throw new FlowAbortException("已停止", userCancelled: true);

            using Bitmap bmp = ScreenCapture.Capture(x, y, w, h);
            string file = ImageLibrary.SaveGenerated(bmp);
            AppLog.Info("Probe", $"截图 · {label}：{x},{y} · {w} × {h} → {file}");
            return VarValue.FromRegion(x, y, w, h, file);
        }
        finally
        {
            RecordingIndicatorWindow.RestoreAutomation(indicator);
        }
    }

    /// <summary>
    /// 解析区域文本：<c>x,y,w,h</c>（逗号 / 空白分隔都行）。
    /// 变量渲染后也是这个形态，所以 <c>${截图}</c> 可以直接当区域用。
    /// </summary>
    public static bool TryParseRect(string text, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        string[] parts = text.Split(
            new[] { ',', '，', ';', '；', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4) return false;

        bool ok = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)
               && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out w)
               && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out h);

        return ok && w > 0 && h > 0;
    }
}
