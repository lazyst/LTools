using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Units;
using LTools.Native;

namespace LTools.Core.Ai;

/// <summary>一次屏幕截取：位图 + 它在虚拟桌面上的位置（物理像素）。</summary>
internal sealed record ScreenFrame(Bitmap Bitmap, int Left, int Top) : IDisposable
{
    public int Width => Bitmap.Width;

    public int Height => Bitmap.Height;

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// MCP「截图」「控件树」两个工具的底层取图 / 存图。
/// </summary>
/// <remarks>
/// agent 看不见用户的屏幕（A4）：找图、图片点击、按控件点击这类动作，它没法自己产出模板图或锚点。
/// 坐标系与截图 / 找图 / 鼠标注入**完全一致**（虚拟桌面物理像素，多屏时 <c>Left</c> / <c>Top</c> 可能是负数），
/// 回执里一并给出截取矩形，agent 才能把「图片上看到的点」换算成屏幕坐标。
/// </remarks>
internal static class AiScreen
{
    /// <summary>取图来源：整块虚拟桌面 / 前台窗口 / 固定区域。</summary>
    public static readonly string[] Sources = { "screen", "foreground", "region" };

    /// <summary>
    /// 截一张图。调用方负责 <c>Dispose</c> 返回的位图。
    /// </summary>
    /// <param name="source">screen / foreground / region（空 = screen）。</param>
    /// <param name="region">固定区域 <c>x,y,w,h</c>，只在 source = region 时必填。</param>
    public static ScreenFrame Capture(string? source, string? region)
    {
        string kind = (source ?? "").Trim().ToLowerInvariant();
        if (kind.Length == 0) kind = "screen";

        // 提示卡片是置顶窗口，会落进给 agent 看的截图：抓帧前收起，抓完放回来。
        using var suppressed = ToastService.SuppressForCapture();

        switch (kind)
        {
            case "screen":
            {
                var (x, y, w, h) = Virtual();
                return new ScreenFrame(ScreenCapture.Capture(x, y, w, h), x, y);
            }

            case "foreground":
            {
                IntPtr hwnd = Win32.GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
                    if (w > 0 && h > 0) return new ScreenFrame(ScreenCapture.Capture(x, y, w, h), x, y);
                }

                // 拿不到前台窗口就退回整块屏幕：截错粒度比失败好，agent 至少能看到屏幕。
                var (vx, vy, vw, vh) = Virtual();
                return new ScreenFrame(ScreenCapture.Capture(vx, vy, vw, vh), vx, vy);
            }

            case "region":
                return CaptureRegion(region);

            default:
                throw new AiToolException("invalid_source",
                    $"source 只能是 {string.Join(" / ", Sources)}（收到「{source}」）");
        }
    }

    private static ScreenFrame CaptureRegion(string? region)
    {
        int[]? rect = UnitValues.ParseRect(Value.FromText(region ?? ""));
        if (rect == null || rect.Length < 4 || rect[2] <= 0 || rect[3] <= 0)
            throw new AiToolException("invalid_region",
                "source = region 时 region 要写 x,y,w,h（虚拟桌面物理像素，宽高必须大于 0），" +
                "例如 100,200,800,600；不知道坐标就先 source = screen 截一张看。");

        return new ScreenFrame(ScreenCapture.Capture(rect[0], rect[1], rect[2], rect[3]), rect[0], rect[1]);
    }

    private static (int x, int y, int w, int h) Virtual() =>
        (ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
         ScreenCapture.VirtualWidth, ScreenCapture.VirtualHeight);

    /// <summary>把位图编码成 png（原分辨率，写文件用）。</summary>
    public static byte[] EncodePng(Bitmap bitmap)
    {
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }

    /// <summary>
    /// 附带在回执里的图片：按 <paramref name="maxWidth"/> 等比缩小（0 = 不缩）。
    /// 返回的 <c>scale = 图片宽 / 截图宽</c>，agent 用「图片像素 ÷ scale + 左上角」就能换回屏幕坐标。
    /// </summary>
    public static (byte[] Png, int Width, int Height) Thumbnail(Bitmap bitmap, int maxWidth)
    {
        if (maxWidth <= 0 || bitmap.Width <= maxWidth) return (EncodePng(bitmap), bitmap.Width, bitmap.Height);

        int width = maxWidth;
        int height = Math.Max(1, (int)Math.Round(bitmap.Height * (double)maxWidth / bitmap.Width));

        using var small = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.None;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(bitmap, new Rectangle(0, 0, width, height));
        }

        return (EncodePng(small), width, height);
    }

    /// <summary>
    /// 把 png 存进 <c>resources/images/</c>（没有草稿时的落点），返回绝对路径。
    /// 重名加序号，不覆盖已有文件。
    /// </summary>
    public static string SaveToImages(byte[] png, string fileName)
    {
        ImageLibrary.EnsureDir();
        string dir = ImageLibrary.Dir;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string path = Path.Combine(dir, fileName);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stem}-{n}{ext}");

        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>把用户给的文件名收拾成安全的 png 文件名（空 = <c>shot-时间戳.png</c>）。</summary>
    public static string SafeFileName(string? name)
    {
        string stem = (name ?? "").Trim();
        if (stem.Length == 0) stem = $"shot-{DateTime.Now:yyyyMMdd-HHmmss}";

        foreach (char bad in Path.GetInvalidFileNameChars()) stem = stem.Replace(bad, '-');
        stem = stem.Replace('/', '-').Replace('\\', '-').Trim('.', ' ', '-');
        if (stem.Length == 0) stem = $"shot-{DateTime.Now:yyyyMMdd-HHmmss}";
        if (stem.Length > 80) stem = stem[..80];

        if (!stem.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) stem += ".png";
        return stem;
    }
}
