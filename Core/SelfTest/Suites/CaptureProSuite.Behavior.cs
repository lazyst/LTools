using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Config;
using LTools.Features.CapturePro;
using LTools.Native;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 截图行为设置（照上游 <c>screenshot/*</c>）：默认值 / 归一化 / HiDPI 缩放 /
/// PDF 纸张 / 光标混合 / 边框拖动 / PDF 格式。
/// </summary>
/// <remarks>
/// <para>
/// 全部是纯函数与不落盘的形态：这里**不读也不写** <c>configs/settings.json</c>
/// （设置页控件往返、真光标、真另存 PDF 由仓库外 <c>capture-overlay-smoke</c> 的
/// <c>behavior</c> 段验）。
/// </para>
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void BehaviorCases()
    {
        ScreenshotBehaviorDefaults();
        ScreenshotBehaviorNormalize();
        HiDpiScaling();
        ScreenshotPdfLayout();
        CursorBlend();
        FrameDragRules();
        PdfFormat();
    }

    /// <summary>浮点近似断言（PDF 版面里的点会带小数，逐位相等没意义）。</summary>
    private static void Near(double expected, double actual, string what, double tolerance = 1e-4) =>
        Check.True(Math.Abs(expected - actual) <= tolerance,
            $"{what}（期望 {expected:0.####}，实际 {actual:0.####}）");

    // ============ 默认值（上游 configurationschema.cpp） ============
    private static void ScreenshotBehaviorDefaults()
    {
        var file = new ScreenshotFile();

        Check.Equal(200, file.ScrollingAutoScrollIntervalMs ?? -1, "长截图自动滚动间隔默认 200ms");
        Check.False(file.CaptureCursor ?? true, "截图带指针默认关");
        Check.False(file.ConfirmBeforeExitViaShortcut ?? true, "Esc 退出确认默认关");
        Check.True(file.QuickSelectionModification ?? false, "非选择工具拖边框默认开");
        Check.Equal("follow_mouse_movement", file.SelectionResizeMode, "拖动改尺寸默认按位移");
        Check.Equal("disabled", file.HidpiImageScaling, "HiDPI 降采样默认关闭");
        Check.Equal("a4_portrait", file.PdfPageSize, "PDF 纸张默认 A4 竖版");

        Check.Equal(200, ScreenshotBehavior.DefaultScrollingAutoScrollIntervalMs, "间隔常量与落盘默认值同源");
        Check.Equal(128, ScreenshotBehavior.MinScrollingAutoScrollIntervalMs, "间隔下限 128");
        Check.Equal(1000, ScreenshotBehavior.MaxScrollingAutoScrollIntervalMs, "间隔上限 1000");
        Check.Equal("follow_mouse_movement", ScreenshotBehavior.DefaultSelectionResizeMode, "尺寸方式默认常量");
        Check.Equal("disabled", ScreenshotBehavior.DefaultHidpiImageScaling, "HiDPI 默认常量");
        Check.Equal("a4_portrait", ScreenshotBehavior.DefaultPdfPageSize, "PDF 纸张默认常量");

        // 固定档位 125…350，每 25 一档 = 10 档
        Check.Equal(10, ScreenshotBehavior.HidpiPercentages.Count, "HiDPI 固定档位 10 个");
        Check.Equal(125, ScreenshotBehavior.HidpiPercentages[0], "第一档 125%");
        Check.Equal(350, ScreenshotBehavior.HidpiPercentages[^1], "最后一档 350%");
    }

    // ============ 归一化 ============
    private static void ScreenshotBehaviorNormalize()
    {
        // 间隔
        Check.Equal(200, ScreenshotBehavior.NormalizeInterval(null), "空间隔按默认 200");
        Check.Equal(128, ScreenshotBehavior.NormalizeInterval(0), "0 夹到下限 128");
        Check.Equal(128, ScreenshotBehavior.NormalizeInterval(-40), "负数夹到下限 128");
        Check.Equal(1000, ScreenshotBehavior.NormalizeInterval(5000), "超大夹到上限 1000");
        Check.Equal(300, ScreenshotBehavior.NormalizeInterval(300), "范围内的值不动");
        Check.Equal(128, ScreenshotBehavior.NormalizeInterval(128), "正好在下限");
        Check.Equal(1000, ScreenshotBehavior.NormalizeInterval(1000), "正好在上限");

        // 尺寸方式
        Check.Equal("follow_mouse_movement", ScreenshotBehavior.NormalizeResizeMode(null), "空 = 按位移");
        Check.Equal("follow_mouse_movement", ScreenshotBehavior.NormalizeResizeMode(""), "空串 = 按位移");
        Check.Equal("follow_mouse_position", ScreenshotBehavior.NormalizeResizeMode("follow_mouse_position"),
            "认「贴到指针」");
        Check.Equal("follow_mouse_position",
            ScreenshotBehavior.NormalizeResizeMode("  FOLLOW_MOUSE_POSITION  "), "大小写 / 空白不影响");
        Check.Equal("follow_mouse_movement", ScreenshotBehavior.NormalizeResizeMode("nope"), "认不出的按位移");

        // HiDPI
        Check.Equal("disabled", ScreenshotBehavior.NormalizeHidpi(null), "空 = 关闭");
        Check.Equal("disabled", ScreenshotBehavior.NormalizeHidpi("disabled"), "disabled = 关闭");
        Check.Equal("auto", ScreenshotBehavior.NormalizeHidpi("auto"), "auto 保留");
        Check.Equal("auto", ScreenshotBehavior.NormalizeHidpi(" AUTO "), "auto 认大小写 / 空白");
        Check.Equal("125", ScreenshotBehavior.NormalizeHidpi("125"), "125 是合法档位");
        Check.Equal("350", ScreenshotBehavior.NormalizeHidpi("350"), "350 是合法档位");
        Check.Equal("disabled", ScreenshotBehavior.NormalizeHidpi("100"), "100 不是档位（= 不缩）");
        Check.Equal("disabled", ScreenshotBehavior.NormalizeHidpi("127"), "非 25 的倍数不认");
        Check.Equal("disabled", ScreenshotBehavior.NormalizeHidpi("375"), "超过 350 不认");
        Check.Equal("disabled", ScreenshotBehavior.NormalizeHidpi("abc"), "不是数字不认");

        Check.True(ScreenshotBehavior.TryParsePercentage("125", out int p125) && p125 == 125, "125 → 125");
        Check.True(ScreenshotBehavior.TryParsePercentage(" 200 ", out int p200) && p200 == 200, "带空白也认");
        Check.False(ScreenshotBehavior.TryParsePercentage("124", out _), "124 不是档位");
        Check.False(ScreenshotBehavior.TryParsePercentage("137.5", out _), "小数不认");
        Check.False(ScreenshotBehavior.TryParsePercentage(null, out _), "null 不认");

        // PDF 纸张
        Check.Equal("a4_portrait", ScreenshotBehavior.NormalizePdfPageSize(null), "空 = A4 竖版");
        Check.Equal("a4_portrait", ScreenshotBehavior.NormalizePdfPageSize("nope"), "认不出的按 A4 竖版");
        Check.Equal("a4_landscape", ScreenshotBehavior.NormalizePdfPageSize("A4_LANDSCAPE"), "A4 横版认大小写");
        Check.Equal("image_size", ScreenshotBehavior.NormalizePdfPageSize(" image_size "), "图片尺寸认空白");
    }

    // ============ HiDPI 缩放（上游 screenshothidpiimagescaling.h） ============
    private static void HiDpiScaling()
    {
        var full = new Int32Rect(0, 0, 1920, 1080);
        var displays150 = new[] { new HiDpiImageScaling.Display(full, 1.5) };
        var displays100 = new[] { new HiDpiImageScaling.Display(full, 1.0) };

        Check.Equal(1.0, HiDpiImageScaling.ScaleFor("disabled", full, displays150), "关闭就不缩");
        Check.True(Math.Abs(HiDpiImageScaling.ScaleFor("auto", full, displays150) - 2.0 / 3.0) < 1e-12,
            "auto + 150% → 2/3（150% 屏幕上 1 个物理像素 = 2/3 DIP）");
        Check.Equal(1.0, HiDpiImageScaling.ScaleFor("auto", full, displays100), "100% 的屏不缩");
        Check.Equal(1.0, HiDpiImageScaling.ScaleFor("auto", full, Array.Empty<HiDpiImageScaling.Display>()),
            "一块屏都不相交 = 不缩");

        var mixed = new[]
        {
            new HiDpiImageScaling.Display(new Int32Rect(0, 0, 960, 1080), 1.5),
            new HiDpiImageScaling.Display(new Int32Rect(960, 0, 960, 1080), 1.25),
        };
        Check.Equal(1.0, HiDpiImageScaling.ScaleFor("auto", full, mixed), "跨不同缩放的屏不缩（一半清晰一半糊）");

        var same = new[]
        {
            new HiDpiImageScaling.Display(new Int32Rect(0, 0, 960, 1080), 1.5),
            new HiDpiImageScaling.Display(new Int32Rect(960, 0, 960, 1080), 1.5),
        };
        Check.True(Math.Abs(HiDpiImageScaling.ScaleFor("auto", full, same) - 2.0 / 3.0) < 1e-12,
            "两块同缩放 → 照样缩");

        // 贴边不算相交（QRectF::intersects）
        var right = new Int32Rect(1920, 0, 1920, 1080);
        Check.Equal(1.0, HiDpiImageScaling.ScaleFor("auto", new Int32Rect(0, 0, 1920, 1080),
            new[] { new HiDpiImageScaling.Display(right, 2.0) }), "只贴边不算相交");
        Check.True(Math.Abs(HiDpiImageScaling.ScaleFor("auto", new Int32Rect(1000, 0, 1000, 1080),
            new[] { new HiDpiImageScaling.Display(right, 2.0) }) - 0.5) < 1e-12, "真的压过去就缩");

        // 固定档位
        Check.True(Math.Abs(HiDpiImageScaling.ScaleFor("200", full, displays150) - 0.5) < 1e-12, "200% 档 = 0.5");
        Check.True(Math.Abs(HiDpiImageScaling.ScaleFor("125", full, displays150) - 0.8) < 1e-12, "125% 档 = 0.8");

        // 尺寸（qRound：半数远离零、至少 1）
        Check.Equal((50, 25), HiDpiImageScaling.OutputSize(100, 50, 0.5), "整数结果");
        Check.Equal((51, 26), HiDpiImageScaling.OutputSize(101, 51, 0.5), "50.5 / 25.5 都远离零进位");
        Check.Equal((1, 1), HiDpiImageScaling.OutputSize(1, 1, 0.5), "最小 1 像素");
        Check.Equal((0, 0), HiDpiImageScaling.OutputSize(0, 10, 1), "空图 = 0");
        Check.Equal((0, 0), HiDpiImageScaling.OutputSize(10, 10, 1.5), "比 1 大的缩放不收（不放大）");
        Check.Equal((0, 0), HiDpiImageScaling.OutputSize(10, 10, double.NaN), "NaN 收缩放不收");

        // 相交判定
        Check.True(HiDpiImageScaling.Intersects(new Int32Rect(0, 0, 10, 10), new Int32Rect(5, 5, 10, 10)),
            "重叠 = 相交");
        Check.False(HiDpiImageScaling.Intersects(new Int32Rect(0, 0, 10, 10), new Int32Rect(10, 0, 10, 10)),
            "贴边 = 不相交");
        Check.False(HiDpiImageScaling.Intersects(new Int32Rect(0, 0, 10, 10), new Int32Rect(20, 0, 10, 10)),
            "离得远 = 不相交");
        Check.False(HiDpiImageScaling.Intersects(new Int32Rect(0, 0, 0, 10), new Int32Rect(0, 0, 10, 10)),
            "空矩形 = 不相交");
    }

    // ============ PDF 布局（上游 screenshotpdfexport.cpp） ============
    private static void ScreenshotPdfLayout()
    {
        const double a4w = 210.0 * 72.0 / 25.4;
        const double a4h = 297.0 * 72.0 / 25.4;

        // 图片尺寸：页面 = 像素 × 72/96
        var own = ScreenshotPdf.Compute(200, 100, "image_size");
        Near(150, own.PageWidth, "image_size 页宽 = 像素 × 0.75");
        Near(75, own.PageHeight, "image_size 页高 = 像素 × 0.75");
        Near(0, own.ImageX, "image_size 图贴着左边");
        Near(0, own.ImageY, "image_size 图贴着上边");
        Near(150, own.ImageWidth, "image_size 图铺满页宽");
        Near(75, own.ImageHeight, "image_size 图铺满页高");

        // A4 竖版
        var portrait = ScreenshotPdf.Compute(1000, 500, "a4_portrait");
        Near(a4w, portrait.PageWidth, "A4 竖版页宽 210mm");
        Near(a4h, portrait.PageHeight, "A4 竖版页高 297mm");
        Near(595.2756, portrait.ImageWidth, "宽图先顶满页宽");
        Near(297.6378, portrait.ImageHeight, "高度按比例");
        Near(0, portrait.ImageX, "水平居中（宽图正好铺满）");
        Near(272.1260, portrait.ImageY, "垂直居中");

        // A4 横版 = 竖版转置
        var landscape = ScreenshotPdf.Compute(1000, 500, "a4_landscape");
        Near(a4h, landscape.PageWidth, "A4 横版页宽 = 竖版页高");
        Near(a4w, landscape.PageHeight, "A4 横版页高 = 竖版页宽");
        Near(841.8898, landscape.ImageWidth, "横版里宽图顶满页宽");
        Near(420.9449, landscape.ImageHeight, "高度按比例");
        Near(87.1654, landscape.ImageY, "垂直居中");

        // 竖图：先顶满页高
        var tall = ScreenshotPdf.Compute(500, 1000, "a4_portrait");
        Near(420.9449, tall.ImageWidth, "竖图先顶满页高（宽度跟着缩放）");
        Near(841.8898, tall.ImageHeight, "高度正好铺满");
        Near(87.1654, tall.ImageX, "水平居中");

        // 空图 / 认不出的键
        var empty = ScreenshotPdf.Compute(0, 0, "a4_portrait");
        Near(0, empty.PageWidth, "空图 = 全 0（调用方据此报错）");
        Check.Equal("a4_portrait", ScreenshotPdf.PageSizeForKey("nope"), "认不出的纸张按 A4 竖版");
        Check.Equal("image_size", ScreenshotPdf.PageSizeForKey("image_size"), "图片尺寸键保留");

        // 真实字节：文件头是 %PDF-1.
        var bytes = new byte[40 * 20 * 4];
        for (int index = 0; index < bytes.Length; index += 4)
        {
            bytes[index] = 0x20;
            bytes[index + 1] = 0x90;
            bytes[index + 2] = 0xE0;
            bytes[index + 3] = 0xFF;
        }

        var frame = BitmapSource.Create(40, 20, 96, 96, PixelFormats.Bgra32, null, bytes, 40 * 4);
        frame.Freeze();

        using var stream = new MemoryStream();
        ScreenshotPdf.Save(stream, frame, 100);
        Check.True(stream.Length > 200, "PDF 至少写出几百字节");
        Check.Equal("%PDF-1.", System.Text.Encoding.ASCII.GetString(stream.ToArray(), 0, 7),
            "文件头是 %PDF-1.");
    }

    // ============ 光标混合（上游 cursor_compositor.cpp 的 AlphaBlend 分支） ============
    private static void CursorBlend()
    {
        // 4×4 黑底画布
        var canvas = new byte[4 * 4 * 4];
        for (int index = 3; index < canvas.Length; index += 4) canvas[index] = 255;

        // 全不透明的 2×2 红色光标画到 (1,1)
        var opaque = new CursorCapture.CursorFrame
        {
            Width = 2,
            Height = 2,
            Pixels = new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 },
        };

        Check.True(CursorCapture.Composite(canvas, 16, 4, 4, opaque, 1, 1), "画上了点东西");
        Check.Equal((byte)255, canvas[(1 * 4 + 1) * 4 + 2], "不透明像素直接覆盖（R = 255）");
        Check.Equal((byte)255, canvas[(1 * 4 + 1) * 4 + 3], "画过的地方 alpha 变 255");
        Check.Equal((byte)0, canvas[0], "左上角没被碰到");

        // 半透明：黑底上叠红 a=128 → 128
        var blend = new byte[1 * 1 * 4];
        blend[3] = 255;
        var half = new CursorCapture.CursorFrame
        {
            Width = 1,
            Height = 1,
            Pixels = new byte[] { 0, 0, 255, 128 },
        };
        Check.True(CursorCapture.Composite(blend, 4, 1, 1, half, 0, 0), "半透明也画上了");
        Check.Equal((byte)128, blend[2], "(255×128 + 0×127) / 255 = 128");

        // 全透明：什么都不画
        var empty = new CursorCapture.CursorFrame
        {
            Width = 1,
            Height = 1,
            Pixels = new byte[] { 0, 0, 255, 0 },
        };
        Check.False(CursorCapture.Composite(new byte[4], 4, 1, 1, empty, 0, 0), "alpha 0 不算画上");

        // 外面 / 完全出界
        Check.False(CursorCapture.Composite(new byte[4 * 4 * 4], 16, 4, 4, opaque, -5, -5), "整个在画布外");
        Check.False(CursorCapture.Composite(new byte[4 * 4 * 4], 16, 4, 4, opaque, 10, 10), "右边出界");
        Check.True(CursorCapture.Composite(new byte[4 * 4 * 4], 16, 4, 4, opaque, 3, 3), "压住右下角一个像素也算");

        // 像素长度不对的形状直接拒
        var broken = new CursorCapture.CursorFrame
        {
            Width = 2,
            Height = 2,
            Pixels = new byte[] { 0, 0, 255, 255 },
        };
        Check.False(CursorCapture.Composite(new byte[4 * 4 * 4], 16, 4, 4, broken, 0, 0), "像素数对不上不画");
    }

    // ============ 边框拖动（上游 dragModeForVirtualPosition / grabAdjustedScreenshotSelectionRect） ============
    private static void FrameDragRules()
    {
        // 「快速选择修改」：选择工具永远能抓，其它工具看设置
        Check.True(CaptureOverlayWindow.CanDragFrameHandle(FrameHandle.Right, CaptureTool.Select, false),
            "选择工具 + 关口也照样能拖边框");
        Check.True(CaptureOverlayWindow.CanDragFrameHandle(FrameHandle.TopLeft, CaptureTool.Pen, true),
            "开着时画笔也能拖边框");
        Check.False(CaptureOverlayWindow.CanDragFrameHandle(FrameHandle.TopLeft, CaptureTool.Pen, false),
            "关掉时画笔碰边框只是画");
        Check.False(CaptureOverlayWindow.CanDragFrameHandle(FrameHandle.Move, CaptureTool.Select, true),
            "挪整块不是改尺寸");
        Check.False(CaptureOverlayWindow.CanDragFrameHandle(FrameHandle.None, CaptureTool.Select, true),
            "空白处不是改尺寸");

        // 起拖贴到指针
        var sel = new Rect(10, 10, 50, 50);
        var bounds = new Rect(0, 0, 1920, 1080);

        var right = CaptureOverlayWindow.GrabAdjusted(sel, FrameHandle.Right, new Point(100, 30), bounds, 8);
        Check.Equal(new Rect(10, 10, 91, 50), right, "右边界贴到指针右侧一格（100 → 101）");
        Check.Equal(new Rect(10, 10, 50, 50),
            CaptureOverlayWindow.GrabAdjusted(sel, FrameHandle.Move, new Point(100, 30), bounds, 8),
            "挪整块不动几何");

        var left = CaptureOverlayWindow.GrabAdjusted(sel, FrameHandle.Left, new Point(5, 30), bounds, 8);
        Check.Equal(new Rect(5, 10, 55, 50), left, "左边界贴到指针");

        var flip = CaptureOverlayWindow.GrabAdjusted(sel, FrameHandle.Right, new Point(0, 30), bounds, 8);
        Check.Equal(new Rect(0, 10, 10, 50), flip, "拖过对边会翻面");

        var half = CaptureOverlayWindow.GrabAdjusted(sel, FrameHandle.Bottom, new Point(30, 10.5), bounds, 8);
        Check.Equal(new Rect(10, 10, 50, 8), half, "太薄补最小尺寸（Math.Round 半数远离零 → 10，贴边 +1）");

        var clamped = CaptureOverlayWindow.GrabAdjusted(sel, FrameHandle.Right, new Point(5000, 30), bounds, 8);
        Check.Equal(new Rect(10, 10, 1910, 50), clamped, "拖出屏幕被夹回来");

        var corner = CaptureOverlayWindow.GrabAdjusted(sel, FrameHandle.TopLeft, new Point(5.4, 9.4), bounds, 8);
        Check.Equal(new Rect(5, 9, 55, 51), corner, "左上角两个方向一起贴（小数四舍五入）");
    }

    // ============ PDF 格式 ============
    private static void PdfFormat()
    {
        Check.Equal(SaveFormat.Pdf, CaptureSave.FormatOfExtension(".pdf"), "扩展名 .pdf 认成 PDF");
        Check.Equal(SaveFormat.Pdf, CaptureSave.FormatOfExtension(" PDF "), "带空白 / 不带点也认");
        Check.Equal(".pdf", CaptureSave.Extension(SaveFormat.Pdf), "PDF 扩展名 .pdf");
        Check.Equal("PDF", CaptureSave.Label(SaveFormat.Pdf), "格式名 PDF");
        Check.Equal(SaveFormat.Pdf, ExportSettings.ParseFormat("pdf"), "设置里的 pdf 键认成 PDF");
        Check.Equal("pdf", ExportSettings.FormatKey(SaveFormat.Pdf), "枚举写回 pdf 键");
    }
}
