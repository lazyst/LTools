using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Config;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 截图输出（P7-d，照 Snow Shot 的 <c>storage/exportfilename.cpp</c> /
/// <c>storage/timevariables.cpp</c> / <c>storage/screenshotexportvariables.cpp</c> 与
/// <c>screenshot/*</c> 那组设置）：文件名模板、清洗与封顶、设置读写、PNG 压缩力度。
/// </summary>
/// <remarks>
/// 只碰纯函数与内存位图：真写文件、真弹另存为、真把文件放进剪贴板在仓库外
/// <c>capture-overlay-smoke</c> 的 <c>export</c> 段。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void ExportCases()
    {
        // 设置是进程级静态缓存：这一组改之前先存一份，跑完原样还原，别影响别的用例
        var backup = new ExportFile();
        ExportSettings.Capture(backup);
        try
        {
            NameTemplateCases();
            NameSanitizeCases();
            SettingRulesCases();
            PngCases();
        }
        finally
        {
            ExportSettings.Apply(backup);
        }
    }

    /// <summary>时间变量与 <c>{AN}</c> / <c>{WN}</c> 展开。</summary>
    private static void NameTemplateCases()
    {
        var time = new DateTime(2026, 11, 19, 13, 5, 9, 123);

        Check.Equal("截图_2026-11-19_13-05-09",
            ExportNames.Expand("截图_{YYYY}-{MM}-{DD}_{HH}-{mm}-{ss}", time, ExportNameContext.Empty),
            "时间变量按 Qt 记号展开");
        Check.Equal("26", ExportNames.Expand("{YY}", time, ExportNameContext.Empty), "两位年");
        Check.Equal("123", ExportNames.Expand("{zzz}", time, ExportNameContext.Empty), "毫秒");
        Check.Equal("abc", ExportNames.Expand("  abc  ", time, ExportNameContext.Empty), "模板先去首尾空白");
        Check.Equal("", ExportNames.Expand("", time, ExportNameContext.Empty), "空模板展开成空");

        // AP / ap（上午 / 下午）在 .NET 里是 tt
        Check.Equal("PM", ExportNames.Expand("{AP}", time, ExportNameContext.Empty), "AP → 下午");
        Check.Equal("AM", ExportNames.Expand("{AP}", new DateTime(2026, 11, 19, 9, 0, 0),
            ExportNameContext.Empty), "AP → 上午");

        var names = ExportNameContext.Of("Code.exe", "README.md - Visual Studio Code");
        Check.Equal("Code.exe", ExportNames.Expand("{AN}", time, names), "{AN} 是进程名");
        Check.Contains(ExportNames.Expand("{WN}", time, names), "README.md", "{WN} 是窗口标题");
        Check.Equal("Unknown", ExportNames.Expand("{AN}", time, ExportNameContext.Empty),
            "取不到进程名回落 Unknown");
        Check.Equal("Unknown", ExportNames.Expand("{WN}", time, ExportNameContext.Empty),
            "取不到窗口标题回落 Unknown");

        // 文件名的完整流程：展开 → 清洗 → 保留设备名补 _
        Check.Equal("截图_2026-11-19_13-05-09",
            ExportNames.FileStem("截图_{YYYY}-{MM}-{DD}_{HH}-{mm}-{ss}", time, ExportNameContext.Empty),
            "FileStem = 展开 + 清洗");
        Check.Equal("_CON", ExportNames.FileStem("CON", time, ExportNameContext.Empty),
            "保留设备名前面补 _");
        Check.Equal("_com1", ExportNames.FileStem("com1", time, ExportNameContext.Empty),
            "保留设备名不分大小写");
        Check.Equal("console", ExportNames.FileStem("console", time, ExportNameContext.Empty),
            "只是前缀相同的普通名字不动");
    }

    /// <summary>清洗 / UTF-8 封顶 / 组合扩展名。</summary>
    private static void NameSanitizeCases()
    {
        Check.Equal("a_b_c_d_e_f_g_h_i_j",
            ExportNames.Sanitize("a<b>c:d\"e/f\\g|h?i*j", "x"),
            "文件名里不能出现的字符换 _");
        Check.Equal("a_b", ExportNames.Sanitize("a\tb", "x"), "控制字符换 _");
        Check.Equal("abc", ExportNames.Sanitize("abc...", "x"), "末尾的点去掉");
        Check.Equal("abc", ExportNames.Sanitize("abc   ", "x"), "末尾的空格去掉");
        Check.Equal("截图", ExportNames.Sanitize("", "截图"), "空名字回落");

        // 248 字节封顶（给原子写留 7 字节），中文一个字 3 字节
        string longName = new string('名', 200);
        string bounded = ExportNames.Sanitize(longName, "x");
        Check.True(Encoding.UTF8.GetByteCount(bounded) <= ExportNames.MaxFileBytes,
            $"长中文名封顶 {ExportNames.MaxFileBytes} 字节（实际 {Encoding.UTF8.GetByteCount(bounded)}）");
        Check.True(bounded.Length > 0 && !char.IsHighSurrogate(bounded[^1]),
            "截断不会留下半个字符");

        // emoji（代理对）不被切一半
        string emoji = string.Concat(Enumerable.Repeat("😀", 100));
        string cutEmoji = ExportNames.Truncate(emoji, 40);
        Check.True(Encoding.UTF8.GetByteCount(cutEmoji) <= 40, "emoji 截断按字节");
        Check.True(cutEmoji.Length % 2 == 0, "emoji 截断不切代理对");

        // 组合扩展名：只截主名，后缀完整保留
        string composed = ExportNames.Compose(new string('名', 200), ".png");
        Check.True(composed.EndsWith(".png", StringComparison.Ordinal), "后缀不截断");
        Check.True(Encoding.UTF8.GetByteCount(composed) <= ExportNames.MaxFileBytes,
            "组合后仍不超过 248 字节");

        // 模板展开出来的目录片段按 255 字节封顶
        string dir = ExportNames.Expand("{WN}/x", DateTime.Now,
            ExportNameContext.Of("", new string('窗', 300)));
        string first = dir.Split('/')[0];
        Check.True(Encoding.UTF8.GetByteCount(first) <= ExportNames.MaxComponentBytes,
            "目录片段按 255 字节封顶");

        // 变量表给设置页的说明用
        Check.True(ExportNames.Tokens.Any(t => t.Token == "{AN}"), "变量表列了 {AN}");
        Check.True(ExportNames.Tokens.Any(t => t.Token == "{WN}"), "变量表列了 {WN}");
    }

    /// <summary>格式 / 质量 / 压缩的解析与设置读写。</summary>
    private static void SettingRulesCases()
    {
        Check.Equal(SaveFormat.Jpeg, ExportSettings.ParseFormat("jpeg"), "jpeg");
        Check.Equal(SaveFormat.Jpeg, ExportSettings.ParseFormat("JPG"), "jpg 也认");
        Check.Equal(SaveFormat.Tiff, ExportSettings.ParseFormat("tif"), "tif 也认");
        Check.Equal(SaveFormat.Png, ExportSettings.ParseFormat("webp"), "不支持的格式回落 PNG");
        Check.Equal("jpeg", ExportSettings.FormatKey(SaveFormat.Jpeg), "枚举回英文键");
        Check.Equal(ExportCompression.High, ExportSettings.ParseCompression("HIGH"), "压缩力度不分大小写");
        Check.Equal(ExportCompression.Medium, ExportSettings.ParseCompression(""), "空值回落中");
        Check.Equal(100, ExportSettings.NormalizeQuality(null), "质量缺省 100");
        Check.Equal(0, ExportSettings.NormalizeQuality(-5), "质量夹到 0");
        Check.Equal(100, ExportSettings.NormalizeQuality(300), "质量夹到 100");
        Check.Equal(ExportSettings.DefaultNameFormat, ExportSettings.NormalizeNameFormat("  "),
            "空模板回落默认");

        var file = new ExportFile
        {
            Format = "webp",
            Quality = 240,
            Compression = "high",
            SaveDirectory = @"  C:\Temp\shots  ",
            ManualFileNameFormat = "M_{YYYY}",
            AutoFileNameFormat = "A_{YYYY}",
            SaveAsDialog = "SYSTEM",
            ShowSaveResult = true,
            CopyImageFileToClipboard = true,
            AutoSaveAfterExecutionActions = new List<string>
            {
                "copy_screenshot", "pin_to_screen", "不认识的场景", "COPY_PINNED_IMAGE",
            },
        };

        ExportSettings.Apply(file);
        Check.Equal("png", ExportSettings.Format, "不支持的格式回落 png");
        Check.Equal(100, ExportSettings.Quality, "质量夹到 100");
        Check.Equal(ExportCompression.High, ExportSettings.Compression, "压缩力度读进来");
        Check.Equal(@"C:\Temp\shots", ExportSettings.SaveDirectory, "目录去首尾空白");
        Check.Equal("M_{YYYY}", ExportSettings.ManualNameFormat, "手动模板");
        Check.Equal("A_{YYYY}", ExportSettings.AutoNameFormat, "自动模板");
        Check.Equal("system", ExportSettings.SaveAsDialog, "另存为对话框不分大小写");
        Check.True(ExportSettings.ShowSaveResult, "保存后显示结果");
        Check.True(ExportSettings.CopyImageFile, "复制图片文件");
        Check.True(ExportSettings.ShouldAutoSave("copy_screenshot"), "勾了的场景命中");
        Check.True(ExportSettings.ShouldAutoSave("COPY_PINNED_IMAGE"), "场景键不分大小写");
        Check.False(ExportSettings.ShouldAutoSave("不认识的场景"), "不认识的场景被丢掉");
        Check.Equal(3, ExportSettings.AutoSaveScenarios.Count, "只剩三个有效场景");

        // 写回磁盘形状：顺序固定、非法场景不回流
        var roundtrip = new ExportFile();
        ExportSettings.Capture(roundtrip);
        Check.Equal("png", roundtrip.Format, "写回格式");
        Check.Equal("high", roundtrip.Compression, "写回压缩力度");
        Check.Equal("system", roundtrip.SaveAsDialog, "写回对话框");
        Check.True(roundtrip.AutoSaveAfterExecutionActions!
                .SequenceEqual(new[] { "copy_screenshot", "copy_pinned_image", "pin_to_screen" }),
            "自动保存场景按清单顺序写回");

        // 场景清单就是设置页里的勾选顺序
        Check.Equal(6, ExportSettings.Scenarios.Count, "六个自动保存场景");
        Check.Equal(ExportSettings.ScenarioCopy, ExportSettings.Scenarios[0].Key, "第一个是复制截图");

        // 修改后还原成默认
        ExportSettings.Apply(new ExportFile());
        Check.Equal(ExportSettings.DefaultDirectory, ExportSettings.Directory, "默认目录 = 图片文件夹");
        Check.Equal("builtin", ExportSettings.SaveAsDialog, "默认自绘对话框");
        Check.False(ExportSettings.UseSystemDialog, "默认不走 Windows 对话框");
        Check.False(ExportSettings.ShouldAutoSave("copy_screenshot"), "默认不自动保存");
    }

    /// <summary>PNG：文件头、三档压缩、解回来逐像素相同。</summary>
    private static void PngCases()
    {
        const int width = 24;
        const int height = 16;
        var source = ExportGradient(width, height);

        foreach (var level in new[] { ExportCompression.Low, ExportCompression.Medium, ExportCompression.High })
        {
            byte[] png = PngWriter.Encode(source, level);
            Check.True(png.Length > 8 &&
                       png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47,
                $"{level} PNG 文件头");

            var decoded = ExportDecode(png);
            Check.Equal(width, decoded.PixelWidth, $"{level} 宽");
            Check.Equal(height, decoded.PixelHeight, $"{level} 高");
            Check.True(ExportSamePixels(source, decoded), $"{level} 解回来逐像素相同");
        }

        // 压缩力度真的起作用：同一张「大片同色」的图，高压缩不该比低压缩更大
        var flat = ExportSolid(64, 64, Colors.CornflowerBlue);
        int low = PngWriter.Encode(flat, ExportCompression.Low).Length;
        int high = PngWriter.Encode(flat, ExportCompression.High).Length;
        Check.True(high <= low, $"高压缩不比低压缩大（低 {low} / 高 {high}）");

        // 带 alpha 的像素也要原样回来（选区圆角 / 阴影靠它）
        var alpha = ExportSolid(4, 4, Color.FromArgb(60, 200, 10, 10));
        Check.True(ExportSamePixels(alpha, ExportDecode(PngWriter.Encode(alpha, ExportCompression.Medium))),
            "半透明像素往返不变");

        // 非 Bgra32 的输入先转格式（Pbgra32 是截图的常见格式）
        var premultiplied = ExportGradient(width, height).Format == PixelFormats.Pbgra32
            ? ExportGradient(width, height)
            : new FormatConvertedBitmap(ExportGradient(width, height), PixelFormats.Pbgra32, null, 0);
        Check.True(ExportSamePixels(ExportGradient(width, height), ExportDecode(PngWriter.Encode(premultiplied,
            ExportCompression.Medium))), "预乘格式也能编出同样的像素");
    }

    // ============ 夹具 ============

    private static BitmapSource ExportGradient(int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = y * stride + x * 4;
                pixels[at] = (byte)(x * 8);               // B
                pixels[at + 1] = (byte)(y * 12);          // G
                pixels[at + 2] = (byte)((x + y) * 4);     // R
                pixels[at + 3] = 255;                     // A
            }
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    private static BitmapSource ExportSolid(int width, int height, Color color)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 4] = color.B;
            pixels[i * 4 + 1] = color.G;
            pixels[i * 4 + 2] = color.R;
            pixels[i * 4 + 3] = color.A;
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    private static BitmapSource ExportDecode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }

    private static bool ExportSamePixels(BitmapSource expected, BitmapSource actual)
    {
        var left = expected.Format == PixelFormats.Bgra32
            ? expected
            : new FormatConvertedBitmap(expected, PixelFormats.Bgra32, null, 0);
        var right = actual.Format == PixelFormats.Bgra32
            ? actual
            : new FormatConvertedBitmap(actual, PixelFormats.Bgra32, null, 0);

        if (left.PixelWidth != right.PixelWidth || left.PixelHeight != right.PixelHeight) return false;

        int stride = left.PixelWidth * 4;
        var a = new byte[stride * left.PixelHeight];
        var b = new byte[stride * right.PixelHeight];
        left.CopyPixels(a, stride, 0);
        right.CopyPixels(b, stride, 0);

        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

