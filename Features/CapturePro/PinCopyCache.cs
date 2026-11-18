using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 「复制所选为文件」用的一次性缓存目录（照上游 <c>storage/clipboardfilecache.h</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 剪贴板里放的是**文件路径**（<c>CF_HDROP</c>），所以每贴一张图就得先落一个 png。
/// 文件放在数据根 <c>cache/pin-copy/&lt;时间戳&gt;/</c> 下，30 分钟后由启动清理与下一次复制顺手删掉——
/// 接收方（资源管理器、聊天窗口）可能过一会儿才去读，删早了会拿到空文件。
/// </para>
/// <para>
/// 与拖出用的临时文件（<see cref="PinDragRetention"/>）分开：那个在 <c>%TEMP%</c> 且按拖拽生命周期结账，
/// 这个只在应用自己的缓存目录里，<c>cache/</c> 本来就不进备份。
/// </para>
/// </remarks>
internal static class PinCopyCache
{
    /// <summary>保留多久（上游 <c>ClipboardFileCache</c> 的 30 分钟）。</summary>
    public const int MaxAgeMinutes = 30;

    /// <summary>缓存根目录。</summary>
    public static string Root => Path.Combine(AppPaths.DataRoot, "cache", "pin-copy");

    private const string TimeFormat = "yyyyMMdd-HHmmss-fff";

    /// <summary>
    /// 把一批图写成文件并放进剪贴板（文件列表）。返回写成功的张数。
    /// </summary>
    /// <remarks>
    /// 空图 / 编码失败只跳过那一张；一张都没写成不动剪贴板（免得把用户原来的内容清掉）。
    /// </remarks>
    public static int Copy(IReadOnlyList<BitmapSource> images)
    {
        if (images == null || images.Count == 0) return 0;

        Sweep();

        string dir;
        var paths = new List<string>();
        try
        {
            dir = UniqueDirectory();
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinCopyCache", "建缓存目录失败：" + ex.Message);
            return 0;
        }

        for (int i = 0; i < images.Count; i++)
        {
            var image = images[i];
            if (image == null) continue;

            string name = images.Count == 1 ? "贴图.png" : $"贴图-{i + 1}.png";
            string file = Path.Combine(dir, name);
            try
            {
                using var stream = File.Create(file);
                CaptureSave.Encode(stream, image, SaveFormat.Png, 90, 100);
                paths.Add(file);
            }
            catch (Exception ex)
            {
                AppLog.Warn("PinCopyCache", $"写 {name} 失败：{ex.Message}");
            }
        }

        if (paths.Count == 0)
        {
            TryDelete(dir);
            return 0;
        }

        if (!NativeClipboard.SetFileList(paths, cut: false))
        {
            AppLog.Warn("PinCopyCache", "写入剪贴板文件列表失败");
            TryDelete(dir);
            return 0;
        }

        return paths.Count;
    }

    /// <summary>时间戳目录；同一毫秒连着复制两次时补一个序号，免得两批混在一个目录里。</summary>
    private static string UniqueDirectory()
    {
        string stamp = DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);
        string dir = Path.Combine(Root, stamp);
        int suffix = 1;
        while (Directory.Exists(dir))
            dir = Path.Combine(Root, $"{stamp}-{suffix++}");
        return dir;
    }

    /// <summary>
    /// 清掉过期目录（启动时与每次复制前各跑一次）。返回删掉的目录数。
    /// </summary>
    public static int Sweep()
    {
        if (!Directory.Exists(Root)) return 0;

        int removed = 0;
        DateTime cutoff = DateTime.UtcNow.AddMinutes(-MaxAgeMinutes);

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(Root))
            {
                try
                {
                    // 目录名就是时间戳（最后写入时间会被接收方读文件刷掉，不够稳）
                    string name = Path.GetFileName(dir);
                    int dash = name.IndexOf('-', 9);            // yyyyMMdd-HHmmss-fff
                    string stamp = dash > 0 ? name[..dash] : name;
                    if (!DateTime.TryParseExact(stamp, TimeFormat, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var created))
                    {
                        created = Directory.GetLastWriteTimeUtc(dir);
                    }

                    if (created.ToUniversalTime() > cutoff) continue;
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("PinCopyCache", "清理旧缓存失败：" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinCopyCache", "扫描缓存目录失败：" + ex.Message);
        }

        return removed;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinCopyCache", "删除缓存目录失败：" + ex.Message);
        }
    }
}
