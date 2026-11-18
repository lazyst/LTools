using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.Split;

/// <summary>一次批量导出的结果。<see cref="Ok"/> 为 false 时 <see cref="Error"/> 是给人看的一句话。</summary>
internal sealed record SplitExportResult(bool Ok, string Directory, long Saved, long Total,
    string Error);

/// <summary>
/// 按布局把图切成一组 png。照上游 <c>exportSplitImages</c>：目录是
/// <c>切图_&lt;图片名&gt;</c>（重名加 <c> (2)</c>），文件从 <c>1.png</c> 起按格子顺序编号。
/// </summary>
/// <remarks>
/// 单张走 <see cref="CroppedBitmap"/>（只包一层视图，不复制整张图），因此密宫格也不会先把
/// 几十份像素全塞进内存；编码仍是一张一张写盘，导出中途可以按取消。
/// </remarks>
internal static class SplitExport
{
    /// <summary>Windows 文件名里不能出现的字符（照上游 <c>sanitizeExportFileName</c>）。</summary>
    private static readonly char[] Forbidden = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>图片名 → 安全的目录名（去非法字符、去首尾空白与结尾的点）。</summary>
    public static string SanitizeName(string? name)
    {
        var builder = new StringBuilder(name ?? "");
        for (int i = 0; i < builder.Length; i++)
        {
            char c = builder[i];
            if (c < 0x20 || Array.IndexOf(Forbidden, c) >= 0) builder[i] = '_';
        }

        string result = builder.ToString().Trim();
        result = result.TrimEnd('.', ' ');
        return result.Length == 0 ? "截图" : result;
    }

    /// <summary>导出目录名：<c>切图_&lt;名称&gt;</c>。</summary>
    public static string DirectoryName(string? name) => "切图_" + SanitizeName(name);

    /// <summary>
    /// 在 <paramref name="parentDirectory"/> 下建一个新的导出目录；重名就依次试
    /// <c> (2)</c>、<c> (3)</c>…，返回 null = 一个也建不出来。
    /// </summary>
    public static string? CreateDirectory(string parentDirectory, string? name)
    {
        try
        {
            if (!Directory.Exists(parentDirectory)) return null;

            string baseName = DirectoryName(name);
            for (int suffix = 1; suffix <= 999; suffix++)
            {
                string candidate = suffix == 1 ? baseName : $"{baseName} ({suffix})";
                string path = Path.Combine(parentDirectory, candidate);
                if (Directory.Exists(path)) continue;

                Directory.CreateDirectory(path);
                return path;
            }
        }
        catch (Exception)
        {
            // 权限 / 路径过长 / 磁盘满：交给调用方统一提示
        }

        return null;
    }

    /// <summary>
    /// 把 <see cref="SplitLayout.OutputCount"/> 张图写进 <paramref name="parentDirectory"/>
    /// 下新建的子目录。<paramref name="cancelled"/> 为真时中途停下并返回「已取消」。
    /// </summary>
    public static SplitExportResult Export(BitmapSource image, SplitLayout layout,
        string parentDirectory, string? imageName, Func<bool>? cancelled = null,
        Action<long, long>? progress = null)
    {
        long total = layout.OutputCount;
        if (cancelled?.Invoke() == true)
        {
            return new SplitExportResult(false, "", 0, total, "导出已取消");
        }

        if (image.PixelWidth != layout.Width || image.PixelHeight != layout.Height ||
            !layout.IsValid)
        {
            return new SplitExportResult(false, "", 0, total, "图片或切分配置无效");
        }

        string? directory = CreateDirectory(parentDirectory, imageName);
        if (directory == null)
        {
            return new SplitExportResult(false, "", 0, total, "导出文件夹创建失败");
        }

        try
        {
            var source = Prepare(image);
            progress?.Invoke(0, total);

            for (long index = 0; index < total; index++)
            {
                if (cancelled?.Invoke() == true)
                {
                    return new SplitExportResult(false, directory, index, total, "导出已取消");
                }

                var region = layout.RegionAt(index);
                var crop = new CroppedBitmap(source,
                    new Int32Rect(region.X, region.Y, region.Width, region.Height));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(crop));

                string path = Path.Combine(directory, $"{index + 1}.png");
                try
                {
                    using var stream = File.Create(path);
                    encoder.Save(stream);
                }
                catch (Exception ex)
                {
                    return new SplitExportResult(false, directory, index, total,
                        $"第 {index + 1} 张保存失败：{ex.Message}");
                }

                progress?.Invoke(index + 1, total);
            }

            return new SplitExportResult(true, directory, total, total, "");
        }
        catch (OutOfMemoryException)
        {
            return new SplitExportResult(false, directory, 0, total, "内存不足，无法导出");
        }
        catch (Exception ex)
        {
            return new SplitExportResult(false, directory, 0, total, "导出失败：" + ex.Message);
        }
    }

    /// <summary>裁切只认 32 位格式；别的格式先转一层（不改变像素）。</summary>
    private static BitmapSource Prepare(BitmapSource image)
    {
        if (image.Format == PixelFormats.Bgr32 || image.Format == PixelFormats.Bgra32 ||
            image.Format == PixelFormats.Pbgra32)
        {
            return image;
        }

        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>导出后给「打开文件夹」用的路径列表（当前只有一个）。</summary>
    public static IReadOnlyList<string> Files(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*.png") : Array.Empty<string>();
}
