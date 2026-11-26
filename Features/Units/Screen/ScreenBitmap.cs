using System;
using System.Drawing;
using System.IO;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Native;
using LTools.Views;

namespace LTools.Features.Units.Screen;

/// <summary>
/// 屏幕类取值单元共用的「拿一张位图」：图片文件（来自「截图」或参数）或现截一块屏幕区域。
/// </summary>
/// <remarks>
/// <para>
/// 「文字识别」与「表格识别」共用：区域语义（前台窗口 / 整个屏幕 / 固定矩形）、图片路径解析
/// （<c>files/x.png</c> / <c>resources/images</c> / 绝对路径，见 <see cref="ImageLibrary.Resolve"/>）
/// 与「截图时收起自动化指示器」必须完全一致，别再各写一份。
/// </para>
/// <para>
/// 截出来的图**不落盘**：要留下 png 就用「截图」那一步。反过来说，「文字识别」读文件那条路仍走
/// <c>OcrProbe.Run</c>（它自己的变量诊断更细），这里只服务需要原始位图的调用方（表格识别）。
/// </para>
/// </remarks>
internal static class ScreenBitmap
{
    /// <summary>按 <paramref name="source"/>（<c>image</c> / <c>foreground</c> / <c>screen</c> / <c>rect</c>）取位图。</summary>
    public static Bitmap Load(StepContext ctx, StepArgs args, string source) =>
        source == "image" ? FromFile(ctx, args) : Capture(ctx, args, source);

    /// <summary>读「图片」参数指向的文件（整段变量带 <c>file</c> 时用它，见 <see cref="ScreenArgs.ImagePath"/>）。</summary>
    public static Bitmap FromFile(StepContext ctx, StepArgs args)
    {
        string spec = ScreenArgs.ImagePath(args);
        if (spec.Length == 0)
            throw new ActionException(
                "没有指定图片：先用「截图」取一张图，再把它的变量填进来；" +
                "也可以把「图片来源」切成前台窗口 / 整个屏幕 / 固定区域，运行时直接截取");

        string path = ImageLibrary.Resolve(spec, ctx.ResourceRoot);
        if (!File.Exists(path)) throw new ActionException($"要识别的图片不存在：{path}");

        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex)
        {
            throw new ActionException($"读不到要识别的图片「{path}」：{ex.Message}");
        }
    }

    /// <summary>现截一块屏幕区域（前台窗口 / 整个屏幕 / 固定矩形），识别期间收起自动化指示器。</summary>
    public static Bitmap Capture(StepContext ctx, StepArgs args, string source)
    {
        if (ctx.Token.IsCancellationRequested)
            throw new ActionAbortedException("已停止", userCancelled: true);

        ImageRegion region = ScreenArgs.ParseRegion(source);
        int[]? rect = ScreenArgs.ParseRect(args);
        if (region == ImageRegion.rect && (rect == null || rect.Length < 4 || rect[2] <= 0 || rect[3] <= 0))
            throw new ActionException("选了固定区域，但区域还是空的：在「固定区域」那一行框选一下");

        var (x, y, w, h) = ScreenRegion.Resolve(region, rect);
        if (w <= 0 || h <= 0) throw new ActionException($"识别区域无效：{x},{y} · {w}×{h}");

        // 自动化指示器是置顶窗口，会被截进画面：截图期间收起，结束后恢复。
        using var _ = RecordingIndicatorWindow.SuppressForCapture();
        return ScreenCapture.Capture(x, y, w, h);
    }
}
