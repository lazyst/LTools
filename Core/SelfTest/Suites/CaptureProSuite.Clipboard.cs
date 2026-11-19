using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 剪贴板外观 / 落位元数据（P7-e）：帧编码、解码的健壮性、桌面变化后的落位解析、
/// 文件签名（陈旧保护）与「一次复制带哪几份载荷」。
/// </summary>
/// <remarks>
/// 照上游 <c>screenshotclipboardappearance.cpp</c> / <c>screenshotclipboardplacement.cpp</c>：
/// 帧是 <c>magic + version + length + JSON</c>，读写都要能挡住截断 / 版本不对 / 尺寸超限 / 几何无效。
/// 真剪贴板往返（写一遍、读回来、真贴回原位）由仓库外 <c>capture-overlay-smoke</c> 的
/// <c>clipboard-meta</c> 段验。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void ClipboardMetaCases()
    {
        // ============ 外观：编码 → 解码 ============
        var look = new ClipboardAppearance(800, 600, 12, 20, PinAppearance.DecorationShadow, true, null);
        byte[]? bytes = CaptureClipboard.EncodeAppearance(look);
        Check.True(bytes != null, "外观能编码");
        Check.True(bytes!.Length > 12, "带帧头");

        var back = CaptureClipboard.DecodeAppearance(bytes);
        Check.True(back != null, "外观能解码");
        Check.Equal(800, back!.RasterWidth, "宽度往返");
        Check.Equal(600, back.RasterHeight, "高度往返");
        Check.Equal(12, back.RadiusPx, "圆角往返");
        Check.Equal(20, back.EffectPx, "效果内边距往返");
        Check.Equal(PinAppearance.DecorationShadow, back.Decoration, "效果名称往返");
        Check.True(back.Checkerboard, "棋盘格标记往返");

        var plain = CaptureClipboard.DecodeAppearance(
            CaptureClipboard.EncodeAppearance(new ClipboardAppearance(10, 10, 0, 0,
                PinAppearance.DecorationNone, false, null)));
        Check.True(plain != null && !plain.Checkerboard && plain.RadiusPx == 0, "没有外观也能往返");

        // 编码时校验：尺寸为 0 / 负长度 / 认不出的效果名都编不出来
        Check.True(CaptureClipboard.EncodeAppearance(new ClipboardAppearance(0, 10, 0, 0,
            PinAppearance.DecorationNone, false, null)) == null, "宽度 0 编不出来");
        Check.True(CaptureClipboard.EncodeAppearance(new ClipboardAppearance(10, 10, -1, 0,
            PinAppearance.DecorationNone, false, null)) == null, "负圆角编不出来");
        Check.True(CaptureClipboard.EncodeAppearance(new ClipboardAppearance(10, 10, 0, 0,
            "blur", false, null)) == null, "认不出的效果名编不出来");

        // ============ 外观：坏帧一律当作「没有元数据」 ============
        Check.True(CaptureClipboard.DecodeAppearance(null) == null, "null 解不出来");
        Check.True(CaptureClipboard.DecodeAppearance(Array.Empty<byte>()) == null, "空数组解不出来");
        Check.True(CaptureClipboard.DecodeAppearance(bytes.Take(8).ToArray()) == null, "只有半个帧头解不出来");

        byte[] badMagic = bytes.ToArray();
        badMagic[0] ^= 0xFF;
        Check.True(CaptureClipboard.DecodeAppearance(badMagic) == null, "magic 不对解不出来");

        byte[] badVersion = bytes.ToArray();
        badVersion[4] = 9;
        Check.True(CaptureClipboard.DecodeAppearance(badVersion) == null, "版本不对解不出来");

        // 帧头声明的长度超出实际字节（截断）：不能把后面的垃圾当 JSON 解析
        byte[] truncated = bytes.Take(bytes.Length - 4).ToArray();
        Check.True(CaptureClipboard.DecodeAppearance(truncated) == null, "截断的帧解不出来");

        // 帧头声明长度比实际小：以帧头为准，多余的补齐字节忽略
        var padded = bytes.Concat(new byte[] { 0, 0, 0, 0 }).ToArray();
        Check.True(CaptureClipboard.DecodeAppearance(padded) != null, "尾部补齐字节不影响解码");

        Check.True(CaptureClipboard.DecodeAppearance(new byte[CaptureClipboard.MaxAppearanceBytes + 1]) == null,
            "超过字节上限直接拒");

        // 落位载荷不能被外观解码器认（两边 magic 不同）
        var display = new ClipboardDisplay(new Int32Rect(0, 0, 1920, 1080),
            new Int32Rect(0, 0, 1920, 1040), 1.25, true);
        var placement = new ClipboardPlacement(new Int32Rect(100, 120, 400, 300), 400, 300,
            new[] { display }, 0, null);
        byte[]? placementBytes = CaptureClipboard.EncodePlacement(placement);
        Check.True(placementBytes != null, "落位能编码");
        Check.True(CaptureClipboard.DecodeAppearance(placementBytes) == null, "落位载荷不当外观解");
        Check.True(CaptureClipboard.DecodePlacement(bytes) == null, "外观载荷不当落位解");

        // ============ 落位：编码 → 解码 ============
        var restored = CaptureClipboard.DecodePlacement(placementBytes);
        Check.True(restored != null, "落位能解码");
        Check.Equal(new Int32Rect(100, 120, 400, 300), restored!.WindowRect, "窗口矩形往返");
        Check.Equal(400, restored.RasterWidth, "图像素宽往返");
        Check.Equal(300, restored.RasterHeight, "图像素高往返");
        Check.Equal(1, restored.Displays.Count, "显示器数量往返");
        Check.Equal(0, restored.AnchorIndex, "锚点往返");
        Check.Equal(display.Desktop, restored.Displays[0].Desktop, "显示器物理矩形往返");
        Check.Equal(display.Usable, restored.Displays[0].Usable, "工作区往返");
        Check.Equal(1.25, restored.Displays[0].Scale, "缩放往返");
        Check.True(restored.Displays[0].Primary, "主屏标记往返");

        // 无效几何 / 锚点越界 / 屏数超限都编不出来
        Check.True(CaptureClipboard.EncodePlacement(new ClipboardPlacement(
            new Int32Rect(0, 0, 0, 10), 10, 10, new[] { display }, 0, null)) == null, "宽度 0 的窗口编不出来");
        Check.True(CaptureClipboard.EncodePlacement(new ClipboardPlacement(
            new Int32Rect(0, 0, 10, 10), 10, 10, new[] { display }, 1, null)) == null, "锚点越界编不出来");
        Check.True(CaptureClipboard.EncodePlacement(new ClipboardPlacement(
            new Int32Rect(0, 0, 10, 10), 10, 10, Array.Empty<ClipboardDisplay>(), 0, null)) == null,
            "没有显示器编不出来");

        // ============ 锚点：窗口中心落在哪块屏 ============
        var left = new ClipboardDisplay(new Int32Rect(-1920, 0, 1920, 1080),
            new Int32Rect(-1920, 0, 1920, 1040), 1.0, false);
        var right = new ClipboardDisplay(new Int32Rect(0, 0, 1920, 1080),
            new Int32Rect(0, 0, 1920, 1040), 1.0, true);
        var both = new[] { right, left };

        Check.Equal(0, CaptureClipboard.AnchorOf(new Int32Rect(900, 500, 100, 100), both),
            "右屏里的窗口锚到右屏");
        Check.Equal(1, CaptureClipboard.AnchorOf(new Int32Rect(-1800, 100, 100, 100), both),
            "左屏里的窗口锚到左屏");
        Check.Equal(1, CaptureClipboard.AnchorOf(new Int32Rect(-60, 500, 100, 100), both),
            "跨屏时取交集更大的那块");
        Check.Equal(0, CaptureClipboard.AnchorOf(new Int32Rect(5000, 5000, 100, 100), both),
            "完全在桌面外也退回第一块");

        // ============ 落位解析：桌面没变就原样 ============
        var snapshot = new ClipboardPlacement(new Int32Rect(200, 160, 640, 480), 640, 480,
            new[] { right, left }, 0, null);
        Check.Equal(new Int32Rect(200, 160, 640, 480),
            CaptureClipboard.ResolveRect(snapshot, both, true), "桌面没变 → 原窗口矩形");

        // ============ 落位解析：换屏 + 缩放 ============
        // 复制时在主屏（缩放 1.0），现在同一块屏变成 1.5 倍：按比例放大，位置保持相对偏移
        var scaled = new ClipboardDisplay(new Int32Rect(0, 0, 1920, 1080),
            new Int32Rect(0, 0, 1920, 1040), 1.5, true);
        var resolved = CaptureClipboard.ResolveRect(snapshot, new[] { scaled }, true);
        Check.True(resolved is Int32Rect bigger, "换缩放后算得出落点");
        Check.Equal(960, bigger.Width, "宽度按缩放比放大");
        Check.Equal(720, bigger.Height, "高度按缩放比放大");
        Check.Equal(300, bigger.X, "相对偏移跟着放大");
        Check.Equal(240, bigger.Y, "相对偏移跟着放大（纵向）");

        // 装不下工作时按比例缩进工作区（autoResize 打开）
        var tiny = new ClipboardDisplay(new Int32Rect(0, 0, 400, 300),
            new Int32Rect(0, 0, 400, 260), 1.0, true);
        Check.Equal(new Int32Rect(0, 0, 400, 300), CaptureClipboard.ResolveRect(
            new ClipboardPlacement(new Int32Rect(0, 0, 400, 300), 400, 300, new[] { tiny }, 0, null),
            new[] { tiny }, true), "桌面没变时不做二次缩放");

        var moved = new ClipboardDisplay(new Int32Rect(0, 0, 300, 200),
            new Int32Rect(0, 0, 300, 180), 1.0, true);
        var shrunk = CaptureClipboard.ResolveRect(
            new ClipboardPlacement(new Int32Rect(0, 0, 300, 200), 300, 200,
                new[] { new ClipboardDisplay(new Int32Rect(5000, 0, 300, 200),
                    new Int32Rect(5000, 0, 300, 180), 1.0, false) }, 0, null),
            new[] { moved }, true);
        Check.True(shrunk is Int32Rect fit && fit.Width <= 300 && fit.Height <= 180,
            "装不下时缩进工作区");
        Check.True(fit.X >= 0 && fit.Y >= 0, "缩完仍落在屏幕里");

        // ============ 落位解析：拒绝的情形 ============
        Check.True(CaptureClipboard.ResolveRect(null!, both, true) == null, "没有落位就没有解析结果");
        Check.True(CaptureClipboard.ResolveRect(snapshot, Array.Empty<ClipboardDisplay>(), true) == null,
            "当前没有显示器就没有解析结果");

        var stale = new ClipboardPlacement(new Int32Rect(0, 0, 100, 100), 100, 100,
            new[] { right }, 2, null);
        Check.True(CaptureClipboard.ResolveRect(stale, both, true) == null, "锚点越界的落位不解析");

        // ============ 文件签名：陈旧保护 ============
        string dir = Path.Combine(Path.GetTempPath(),
            "ltools-selftest-clipboard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            string png = Path.Combine(dir, "a.png");
            File.WriteAllBytes(png, new byte[] { 1, 2, 3, 4 });

            var signature = ClipboardFile.Of(png);
            Check.True(signature != null, "存在的文件能取签名");
            Check.Equal(4L, signature!.Value.Size, "签名记下字节数");
            Check.True(signature.Value.Matches(png), "同一个文件签名对得上");

            Check.True(ClipboardFile.Of(Path.Combine(dir, "nope.png")) == null, "不存在的文件取不到签名");

            // 内容换了（大小 / 修改时间变了）：签名不再匹配
            File.WriteAllBytes(png, new byte[] { 9, 9, 9, 9, 9 });
            File.SetLastWriteTimeUtc(png, DateTime.UtcNow.AddMinutes(5));
            Check.False(signature.Value.Matches(png), "文件被改过就不再匹配");

            // 路径比较大小写不敏感，但换了文件不算同一个
            string upper = png.ToUpperInvariant();
            Check.True(ClipboardFile.SamePath(png, upper), "路径比较忽略大小写");
            Check.False(ClipboardFile.SamePath(png, Path.Combine(dir, "b.png")), "不同文件不是同一个");

            // ============ 载荷：一次复制带哪几份 ============
            var image = MakeBitmap(64, 48);
            var capture = new ClipboardCapture(new Int32Rect(500, 400, 64, 48),
                new PinAppearance { RadiusPx = 8, EffectPx = 0, Decoration = PinAppearance.DecorationNone },
                true);

            var payloads = CaptureClipboard.Payloads(image, capture, null);
            Check.Equal(2, payloads.Count, "带落位时写两份载荷");
            Check.True(payloads.Any(p => p.Name == CaptureClipboard.AppearanceFormat), "有外观载荷");
            Check.True(payloads.Any(p => p.Name == CaptureClipboard.PlacementFormat), "有落位载荷");

            var written = CaptureClipboard.DecodeAppearance(
                payloads.First(p => p.Name == CaptureClipboard.AppearanceFormat).Data);
            Check.True(written != null, "写进剪贴板的外观能解回来");
            Check.Equal(8, written!.RadiusPx, "外观圆角对得上");
            Check.True(written.Checkerboard, "显式棋盘格也写进去");

            var located = CaptureClipboard.DecodePlacement(
                payloads.First(p => p.Name == CaptureClipboard.PlacementFormat).Data);
            Check.True(located != null, "写进剪贴板的落位能解回来");
            Check.Equal(new Int32Rect(500, 400, 64, 48), located!.WindowRect, "落位是传进来的窗口矩形");

            Check.Equal(0, CaptureClipboard.Payloads(image, null, null).Count, "没有落位就不写载荷");

            // 文件签名：带着文件复制 → 载荷里有签名；换一张图（尺寸不同）就匹配不上
            string shot = Path.Combine(dir, "shot.png");
            File.WriteAllBytes(shot, new byte[] { 7, 7, 7 });
            var withFile = CaptureClipboard.Payloads(image, capture, shot);
            var meta = new ClipboardMeta(
                withFile.First(p => p.Name == CaptureClipboard.AppearanceFormat).Data,
                withFile.First(p => p.Name == CaptureClipboard.PlacementFormat).Data);

            Check.True(meta.AppearanceFor(64, 48, shot) != null, "同尺寸 + 同文件 → 外观可用");
            Check.True(meta.PlacementFor(64, 48, shot) != null, "同尺寸 + 同文件 → 落位可用");
            Check.True(meta.AppearanceFor(65, 48, shot) == null, "图像素尺寸不同 → 外观不可用");
            Check.True(meta.PlacementFor(64, 47, shot) == null, "图像素尺寸不同 → 落位不可用");
            Check.True(meta.AppearanceFor(64, 48, null) == null, "元数据记了文件但没给文件 → 不用");
            Check.True(meta.PlacementFor(64, 48, null) == null, "元数据记了文件但没给文件 → 不用");

            File.WriteAllBytes(shot, new byte[] { 7, 7, 7, 7 });
            File.SetLastWriteTimeUtc(shot, DateTime.UtcNow.AddMinutes(6));
            Check.True(meta.AppearanceFor(64, 48, shot) == null, "文件被改过 → 外观不可用");
            Check.True(meta.PlacementFor(64, 48, shot) == null, "文件被改过 → 落位不可用");

            Check.True(new ClipboardMeta(null, null).IsEmpty, "没有载荷就是空");
            Check.True(meta.AppearanceFor(64, 48, shot) == null && !meta.IsEmpty, "有载荷不是空");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败不算失败 */ }
        }
    }

    /// <summary>造一张纯色位图（只用来当 <see cref="ClipboardCapture"/> 的尺寸来源）。</summary>
    private static BitmapSource MakeBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;
            pixels[i + 1] = 0x80;
            pixels[i + 2] = 0xC0;
            pixels[i + 3] = 0xFF;
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
