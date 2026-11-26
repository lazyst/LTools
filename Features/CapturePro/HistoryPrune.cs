using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LTools.Features.CapturePro;

/// <summary>
/// 采集历史 / 贴图历史的保留策略：按「条数 + 天数」与「磁盘上限」挑出该留的（纯函数）。
/// </summary>
/// <remarks>
/// 两处的实现原来逐行相同（连 <c>maxBytes &gt; 0 &amp;&amp; keep.Count &gt; 0</c> 这种边界都一样），
/// 差别只有记录类型与「一条占多少字节」。**默认值留在各自的 store 里**（7 天 vs 30 天是刻意的），
/// 这里只管算法；冒烟直接喂假数据。
/// </remarks>
internal static class HistoryPrune
{
    /// <summary>记录创建时间的存盘格式（与两个 store 的 <c>TimeFormat</c> 一致，按字符串比较即时间序）。</summary>
    public const string TimeFormat = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>按条数 / 天数裁：先按创建时间倒序，超出条数或早于天数的进 <c>Drop</c>。</summary>
    public static (List<T> Keep, List<T> Drop) ByCountAndAge<T>(IEnumerable<T> items,
        DateTimeOffset now, int maxCount, int maxAgeDays, Func<T, string> createdAt)
    {
        var keep = new List<T>();
        var drop = new List<T>();
        string cutoff = maxAgeDays > 0
            ? now.AddDays(-maxAgeDays).ToString(TimeFormat, CultureInfo.InvariantCulture)
            : "";

        foreach (T item in items.OrderByDescending(createdAt, StringComparer.Ordinal))
        {
            bool tooOld = cutoff.Length > 0 && string.CompareOrdinal(createdAt(item), cutoff) < 0;
            if (tooOld || keep.Count >= maxCount) drop.Add(item);
            else keep.Add(item);
        }

        return (keep, drop);
    }

    /// <summary>按磁盘上限裁：从最新的开始累加，超了就从最旧的丢（至少留一条）。</summary>
    public static (List<T> Keep, List<T> Drop) ByDisk<T>(IEnumerable<T> items,
        long maxBytes, Func<T, long> sizeOf, Func<T, string> createdAt)
    {
        var keep = new List<T>();
        var drop = new List<T>();
        long total = 0;

        foreach (T item in items.OrderByDescending(createdAt, StringComparer.Ordinal))
        {
            long size = Math.Max(0, sizeOf(item));
            if (maxBytes > 0 && keep.Count > 0 && total + size > maxBytes)
            {
                drop.Add(item);
                continue;
            }

            keep.Add(item);
            total += size;
        }

        return (keep, drop);
    }

    /// <summary>文件有多少字节；不存在 / 读不到都算 0（剪枝不能被一个坏文件卡住）。</summary>
    public static long FileBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}
