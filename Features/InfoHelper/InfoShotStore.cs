using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using LTools.Core;

namespace LTools.Features.InfoHelper;

/// <summary>
/// 「辅助工具」截图的落盘目录：<c>cache/info-helper/</c>。
/// </summary>
/// <remarks>
/// <para>
/// 放在 <c>cache/</c> 下是有意的：这是给 agent 看的临时素材，**不进备份**
/// （<c>BackupService</c> 的 <c>BackupDomain</c> 表里没有 <c>cache/</c>），
/// 也不参与 <see cref="Features.ImageLibrary"/> 的按引用回收——动作里不引用这些图。
/// </para>
/// <para>
/// 与「截图」步骤（<c>cache/screenshots</c>，每次运行 / 启动整体清空）分开：
/// 那边是动作运行期的中间产物，这边是用户手动取的素材，攒着看得到历史；
/// 这里只保留最近 <see cref="Keep"/> 张，旧的自己滚掉。
/// </para>
/// </remarks>
internal static class InfoShotStore
{
    /// <summary>只留最近这么多张。</summary>
    public const int Keep = 30;

    /// <summary>落盘目录（数据根下的 <c>cache/info-helper</c>）。</summary>
    public static string Dir => Path.Combine(AppPaths.DataRoot, "cache", "info-helper");

    /// <summary>存一张 png，返回绝对路径（文件名带毫秒，不会覆盖）。</summary>
    public static string Save(BitmapSource image)
    {
        Directory.CreateDirectory(Dir);
        string path = Path.Combine(Dir, $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path))
            encoder.Save(stream);

        Cleanup();
        return path;
    }

    /// <summary>只保留最近 <see cref="Keep"/> 张；返回删掉的数量。</summary>
    public static int Cleanup()
    {
        try
        {
            if (!Directory.Exists(Dir)) return 0;
            var files = Directory.GetFiles(Dir, "shot-*.png")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(Keep)
                .ToList();

            int removed = 0;
            foreach (FileInfo file in files)
            {
                try { file.Delete(); removed++; }
                catch (Exception ex) { AppLog.Warn("InfoHelper", $"清理旧截图失败：{ex.Message}"); }
            }
            return removed;
        }
        catch (Exception ex)
        {
            AppLog.Warn("InfoHelper", $"清理旧截图失败：{ex.Message}");
            return 0;
        }
    }

    /// <summary>删一张自己的截图（删卡片时）；只动 <see cref="Dir"/> 下的文件，别的路径一律不碰。</summary>
    public static void Delete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = Path.GetFullPath(path);
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Dir));
            if (!string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception ex)
        {
            AppLog.Warn("InfoHelper", $"删除截图失败：{ex.Message}");
        }
    }
}
