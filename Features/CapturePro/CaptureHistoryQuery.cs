using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LTools.Features.CapturePro;

/// <summary>
/// 采集历史的「来源」分组（照上游 <c>screenshothistorypagewidget.cpp</c> 的 <c>sourceKey</c>）。
/// </summary>
/// <remarks>
/// 上游的五个来源是剪贴板 / 另存为 / 贴到屏幕 / 当前显示器 / 前台窗口。本地多出两类成品
/// （识别、录屏）和一小撮「其它」（长截图 / 打印），它们同样需要有筛选入口，
/// 不然筛到「全部」之外就再也找不回来。**别把识别 / 录屏并进上面五个**：它们不是完成动作，
/// 是另一条采集路径。
/// </remarks>
internal static class CaptureHistorySource
{
    public const string Clipboard = "clipboard";
    public const string File = "file";
    public const string Pinned = "pinned";
    public const string CurrentMonitor = "current-monitor";
    public const string FocusedWindow = "focused-window";
    public const string Recognize = "recognize";
    public const string Record = "record";
    public const string Other = "other";

    /// <summary>筛选里能选的来源（顺序固定，第一条在最上）。</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> All = new (string, string)[]
    {
        (Clipboard, "复制到剪贴板"),
        (File, "另存为文件"),
        (Pinned, "贴到屏幕"),
        (CurrentMonitor, "截取当前显示器"),
        (FocusedWindow, "截取前台窗口"),
        (Recognize, "识别"),
        (Record, "录屏"),
        (Other, "其它"),
    };

    /// <summary>这条记录属于哪个来源（<see cref="CaptureEntry.Kind"/> 优先，再看完成动作）。</summary>
    public static string KeyOf(CaptureEntry entry) => KeyOf(entry.Kind, entry.Source);

    /// <summary>同 <see cref="KeyOf(CaptureEntry)"/>，只吃两个字段（自检直接喂字符串）。</summary>
    public static string KeyOf(string? kind, string? source)
    {
        if (kind == CaptureCatalog.KindRecord) return Record;
        if (kind == CaptureCatalog.KindRecognize) return Recognize;

        return (source ?? "").Trim() switch
        {
            "复制" => Clipboard,
            "识别并复制" => Clipboard,
            "另存为" => File,
            "快速保存" => File,
            "贴图" => Pinned,
            "实时贴图" => Pinned,
            "当前显示器" => CurrentMonitor,
            "前台窗口" => FocusedWindow,
            "录屏" => Record,
            _ => Other,
        };
    }

    /// <summary>来源的中文名；认不出的键原样返回（绝不返回空串）。</summary>
    public static string LabelOf(string? key)
    {
        foreach (var (k, label) in All)
        {
            if (k == key) return label;
        }
        return string.IsNullOrWhiteSpace(key) ? Other : key!;
    }
}

/// <summary>筛选用的日期范围（含两端；任一为空表示那一头不设限）。</summary>
internal readonly record struct CaptureDateRange(DateTime? Start, DateTime? End)
{
    /// <summary>不设日期限制。</summary>
    public static CaptureDateRange Empty => new(null, null);

    public bool IsEmpty => Start == null && End == null;

    /// <summary>起点晚于终点就交换过来（用户先点结束再点开始不该得到空表）。</summary>
    public CaptureDateRange Ordered()
    {
        if (Start.HasValue && End.HasValue && Start.Value.Date > End.Value.Date)
            return new CaptureDateRange(End.Value.Date, Start.Value.Date);

        return new CaptureDateRange(Start?.Date, End?.Date);
    }

    /// <summary>某一天（按本地日期比较，两端含）在不在范围内。</summary>
    public bool Contains(DateTime day)
    {
        var value = day.Date;
        if (Start.HasValue && value < Start.Value.Date) return false;
        if (End.HasValue && value > End.Value.Date) return false;
        return true;
    }

    /// <summary>界面上的说法：<c>2026-11-01 ~ 2026-11-19</c> / <c>2026-11-01 起</c> / <c>截至 …</c>。</summary>
    public string Describe()
    {
        var range = Ordered();
        if (range.Start.HasValue && range.End.HasValue)
            return $"{Text(range.Start.Value)} ~ {Text(range.End.Value)}";
        if (range.Start.HasValue) return $"{Text(range.Start.Value)} 起";
        if (range.End.HasValue) return $"截至 {Text(range.End.Value)}";
        return "";
    }

    private static string Text(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// 采集历史的筛选条件（来源多选 + 日期范围 + 关键词）与分页规则。
/// </summary>
/// <remarks>
/// 全是纯函数 / 纯数据：窗口只负责把控件状态搬进来、把结果搬出去，
/// 自检直接断言「怎么筛、怎么分页、批量按钮什么时候可用」。
/// </remarks>
internal sealed class CaptureHistoryQuery
{
    /// <summary>什么都不筛。</summary>
    public static CaptureHistoryQuery All { get; } = new();

    /// <summary>选中的来源键；空 = 全部来源。</summary>
    public IReadOnlyCollection<string> Sources { get; init; } = Array.Empty<string>();

    /// <summary>日期范围（含两端）。</summary>
    public CaptureDateRange Range { get; init; } = CaptureDateRange.Empty;

    /// <summary>关键词（空 = 不按文字筛）。</summary>
    public string Text { get; init; } = "";

    public bool IsFiltered => Sources.Count > 0 || !Range.IsEmpty || Text.Length > 0;

    public bool Matches(CaptureEntry entry)
    {
        if (Sources.Count > 0 && !Sources.Contains(CaptureHistorySource.KeyOf(entry))) return false;

        if (!Range.IsEmpty)
        {
            if (!TryParseTime(entry.CreatedAt, out var time)) return false;
            if (!Range.Ordered().Contains(time)) return false;
        }

        return MatchesText(entry, Text);
    }

    /// <summary>按筛选条件过滤（保持输入顺序，即时间倒序）。</summary>
    public List<CaptureEntry> Apply(IEnumerable<CaptureEntry> entries) =>
        entries.Where(Matches).ToList();

    /// <summary>关键词命中：类型 / 完成动作 / 时间文案 / 识别文字（忽略大小写）。</summary>
    public static bool MatchesText(CaptureEntry entry, string? query)
    {
        string text = (query ?? "").Trim();
        if (text.Length == 0) return true;

        return Contains(entry.Text, text)
            || Contains(entry.Source, text)
            || Contains(entry.KindLabel, text)
            || Contains(entry.TimeText, text)
            || Contains(entry.CreatedAt, text);
    }

    /// <summary>把 <c>catalog.json</c> 里的时间戳（本地时间 <c>yyyy-MM-ddTHH:mm:ss</c>）解析成 <see cref="DateTime"/>。</summary>
    public static bool TryParseTime(string? createdAt, out DateTime time) =>
        DateTime.TryParseExact(createdAt ?? "", "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out time);

    private static bool Contains(string? haystack, string needle) =>
        haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>分页（照上游 <c>AdPagination</c>：10 / 20 / 50，页码从 1 起）。</summary>
internal static class CaptureHistoryPaging
{
    public const int DefaultPageSize = 20;

    /// <summary>每页条数的可选项（顺序即下拉顺序）。</summary>
    public static readonly IReadOnlyList<int> PageSizes = new[] { 10, 20, 50 };

    /// <summary>认不出的每页条数（含 0 / 负数）回到默认值。</summary>
    public static int NormalizePageSize(int size) =>
        PageSizes.Contains(size) ? size : DefaultPageSize;

    /// <summary>总页数；空表也算 1 页（界面停在第 1 页，而不是第 0 页）。</summary>
    public static int PageCount(int total, int pageSize)
    {
        int size = NormalizePageSize(pageSize);
        if (total <= 0) return 1;
        return (total + size - 1) / size;
    }

    /// <summary>把页码夹进 <c>1..PageCount</c>。</summary>
    public static int ClampPage(int page, int total, int pageSize)
    {
        int count = PageCount(total, pageSize);
        if (page < 1) return 1;
        return page > count ? count : page;
    }

    /// <summary>本页的 <c>[First, Last)</c> 下标区间（空表返回 <c>(0, 0)</c>）。</summary>
    public static (int First, int Last) Range(int page, int total, int pageSize)
    {
        int size = NormalizePageSize(pageSize);
        if (total <= 0) return (0, 0);

        int current = ClampPage(page, total, pageSize);
        int first = (current - 1) * size;
        if (first >= total) first = Math.Max(0, total - size);
        return (first, Math.Min(first + size, total));
    }

    /// <summary>底栏文案：<c>1-20 / 共 37 条</c>；空表写 <c>还没有记录</c>。</summary>
    public static string RangeText(int page, int total, int pageSize)
    {
        if (total <= 0) return "还没有记录";

        var (first, last) = Range(page, total, pageSize);
        return $"{first + 1}-{last} / 共 {total} 条";
    }

    /// <summary>
    /// 分页按钮上显示哪几页（照上游 <c>AdPagination</c> 的窗口式页码）：最多 <paramref name="window"/> 个，
    /// 以当前页居中、两边贴住 1 / 末页。页数装得下就全列，不分页时不列。
    /// </summary>
    public static IReadOnlyList<int> PageNumbers(int page, int totalPages, int window = 7)
    {
        if (totalPages <= 1) return Array.Empty<int>();

        int span = Math.Max(1, Math.Min(window, totalPages));
        int current = Math.Clamp(page, 1, totalPages);
        int start = current - (span - 1) / 2;
        if (start < 1) start = 1;
        if (start + span - 1 > totalPages) start = totalPages - span + 1;

        var pages = new List<int>(span);
        for (int index = 0; index < span; index++) pages.Add(start + index);
        return pages;
    }

    /// <summary>本页是不是全选中了（「全选本页」据此置灰）。</summary>
    public static bool AllSelected(IEnumerable<string> pageIds, ICollection<string> selected)
    {
        bool any = false;
        foreach (string id in pageIds)
        {
            any = true;
            if (!selected.Contains(id)) return false;
        }
        return any;
    }

    /// <summary>选中项里有没有能贴回屏幕的（没有「结果图」的条目不能贴）。</summary>
    public static bool AnyPinnable(IEnumerable<CaptureEntry> entries, ICollection<string> selected) =>
        entries.Any(entry => selected.Contains(entry.Id) && !entry.IsRecord);

    /// <summary>选中项里有没有能进拼接的（拼接只吃图片，录屏不行）。</summary>
    public static bool AnyStitchable(IEnumerable<CaptureEntry> entries, ICollection<string> selected) =>
        entries.Any(entry => selected.Contains(entry.Id) && !entry.IsRecord);

    /// <summary>按时间倒序取出选中的记录（批量动作要按这个顺序做）。</summary>
    public static List<CaptureEntry> Selected(IEnumerable<CaptureEntry> entries,
        ICollection<string> selected) =>
        entries.Where(entry => selected.Contains(entry.Id)).ToList();
}
