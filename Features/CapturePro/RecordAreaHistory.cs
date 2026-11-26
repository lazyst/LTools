using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using LTools.Core;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 录屏区域历史（照 Snow Shot 的 <c>storage/recordingareahistory.h</c>：最多 100 条、
/// 坐标一律物理像素、录成了才记一条）。
/// </summary>
/// <remarks>
/// <para>
/// 面板上 R / Shift+R 就是在这份历史里前后翻（上游 <c>previous_recording_area</c> /
/// <c>next_recording_area</c>）。落 <c>state/recording-areas.json</c>——它属于「用户的状态」，
/// 跟着备份走，别塞进 <c>cache/</c>。
/// </para>
/// <para>
/// 与上游一致的两条：**同一块区域再录一次就把旧的挪到末尾**（不堆重复项）；
/// 翻历史时跳过「已经不在任何显示器上」的旧坐标（换过屏 / 改过分辨率）。
/// </para>
/// </remarks>
internal static class RecordAreaHistory
{
    /// <summary>上游 <c>recordingAreaHistoryLimit</c>。</summary>
    public const int Limit = 100;

    private static List<Int32Rect>? _cache;
    private static List<Int32Rect>? _screenCache;
    private static DateTime _screenStamp;
    private static readonly TimeSpan ScreenTtl = TimeSpan.FromSeconds(3);

    public static string FilePath => Path.Combine(AppPaths.StateDir, "recording-areas.json");

    /// <summary>旧 → 新（最新的在末尾）。</summary>
    public static IReadOnlyList<Int32Rect> Entries => Load();

    /// <summary>记一条（开始录制时调）；返回是否真的写盘了。</summary>
    public static bool Append(Int32Rect area)
    {
        if (area.Width < 1 || area.Height < 1) return false;

        var list = new List<Int32Rect>(Load());
        list = Normalize(list);
        list.RemoveAll(r => r == area);
        list.Add(area);
        list = Normalize(list);

        _cache = list;
        Save(list);
        return true;
    }

    /// <summary>历史里除了 <paramref name="current"/> 之外还有能用的（决定 R / Shift+R 能不能点）。</summary>
    public static bool AnyOther(Int32Rect current) =>
        AnyOther(Load(), current, ScreenRects());

    /// <summary>历史里除了 <paramref name="current"/> 之外还有落在 <paramref name="screens"/> 上的。</summary>
    internal static bool AnyOther(IReadOnlyList<Int32Rect> entries, Int32Rect current,
        IReadOnlyList<Int32Rect> screens)
    {
        foreach (var rect in entries)
        {
            if (rect == current) continue;
            if (OnSomeScreen(rect, screens)) return true;
        }

        return false;
    }

    /// <summary>
    /// 从 <paramref name="index"/>（-1 = 还没翻过）往前走 / 往后走一格，跳过不在屏幕上的与当前这块。
    /// 返回 false = 到头了（什么都不用改）。
    /// </summary>
    public static bool TryVisit(IReadOnlyList<Int32Rect> entries, Int32Rect current, bool previous,
        int index, out Int32Rect result, out int nextIndex) =>
        TryVisit(entries, current, previous, index, ScreenRects(), out result, out nextIndex);

    /// <summary>同上，显示器矩形由调用方给（自检用假屏，别依赖真机有几块屏）。</summary>
    internal static bool TryVisit(IReadOnlyList<Int32Rect> entries, Int32Rect current, bool previous,
        int index, IReadOnlyList<Int32Rect> screens, out Int32Rect result, out int nextIndex)
    {
        result = default;
        nextIndex = index;

        if (entries.Count == 0) return false;
        if (index >= entries.Count) index = -1;
        if (index < 0 && !previous) return false;

        int step = previous ? -1 : 1;
        int cursor = index < 0 ? entries.Count - 1 : index + step;

        for (; cursor >= 0 && cursor < entries.Count; cursor += step)
        {
            var rect = entries[cursor];
            if (rect == current) continue;
            if (!OnSomeScreen(rect, screens)) continue;

            result = rect;
            nextIndex = cursor;
            return true;
        }

        return false;
    }

    /// <summary>自检用：清洗 + 去重（保留最后一次出现的位置）+ 限长。</summary>
    public static List<Int32Rect> Normalize(IEnumerable<Int32Rect> entries)
    {
        var list = new List<Int32Rect>();

        foreach (var rect in entries)
        {
            if (rect.Width < 1 || rect.Height < 1) continue;
            list.RemoveAll(r => r == rect);
            list.Add(rect);
        }

        if (list.Count > Limit) list.RemoveRange(0, list.Count - Limit);
        return list;
    }

    public static void Invalidate() => _cache = null;

    // ============ 磁盘 ============

    private static List<Int32Rect> Load()
    {
        if (_cache != null) return _cache;

        var list = new List<Int32Rect>();

        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;

                        int x = Int(item, "x");
                        int y = Int(item, "y");
                        int w = Int(item, "width");
                        int h = Int(item, "height");
                        if (w < 1 || h < 1) continue;

                        list.Add(new Int32Rect(x, y, w, h));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "读录屏区域历史失败（按空表继续）：" + ex.Message);
            list.Clear();
        }

        _cache = Normalize(list);
        return _cache;
    }

    private static void Save(List<Int32Rect> list)
    {
        try
        {
            var items = new List<Dictionary<string, int>>(list.Count);
            foreach (var rect in list)
            {
                items.Add(new Dictionary<string, int>
                {
                    ["x"] = rect.X,
                    ["y"] = rect.Y,
                    ["width"] = rect.Width,
                    ["height"] = rect.Height,
                });
            }

            AtomicFile.WriteJson(FilePath, items);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "写录屏区域历史失败：" + ex.Message);
        }
    }

    private static int Int(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetInt32(out int number) ? number : 0;

    /// <summary>所有显示器的物理矩形（自检可换成假屏）。</summary>
    /// <remarks>
    /// 面板每拍都会问「历史里还有别的区域吗」，而 <c>EnumDisplayMonitors</c> 不算免费，
    /// 所以按 3 秒缓存一次：改分辨率 / 插拔屏之后最多 3 秒就反映回来。
    /// </remarks>
    internal static List<Int32Rect> ScreenRects()
    {
        var now = DateTime.UtcNow;
        if (_screenCache != null && now - _screenStamp < ScreenTtl) return _screenCache;

        var rects = new List<Int32Rect>();
        foreach (var screen in Screens.All())
            rects.Add(new Int32Rect(screen.Left, screen.Top, screen.Width, screen.Height));

        _screenCache = rects;
        _screenStamp = now;
        return rects;
    }

    private static bool OnSomeScreen(Int32Rect rect, IReadOnlyList<Int32Rect> screens)
    {
        foreach (var screen in screens)
        {
            bool hit = rect.X < screen.X + screen.Width && rect.X + rect.Width > screen.X
                       && rect.Y < screen.Y + screen.Height && rect.Y + rect.Height > screen.Y;
            if (hit) return true;
        }

        return false;
    }
}
