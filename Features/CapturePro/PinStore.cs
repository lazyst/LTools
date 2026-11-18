using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>一条贴图记录：贴的是哪块屏幕区域、最后落在哪、什么时候关的。</summary>
/// <remarks>
/// 字段对照上游 <c>storage::PinnedWindowRecord</c> / <c>PinnedWindowSummary</c>：
/// 来源（截图 / 历史 / 剪贴板 / 选中的文件 / 其它）、分组、创建与关闭时间、图像尺寸、
/// 源区域（物理像素）、窗口落点（DIP）、是不是实时贴图。显示用的派生文案放在
/// <see cref="PinStore"/> 的纯函数里，<c>--selftest</c> 能直接断言。
/// </remarks>
internal sealed class PinRecord
{
    /// <summary>ULID（<c>Core/Ids.cs</c>），同时是图片 / 缩略图的文件名。</summary>
    public string Id { get; set; } = "";

    /// <summary>分组名（默认 <see cref="PinGroups.Default"/>；组本身不落盘，空了就消失）。</summary>
    public string Group { get; set; } = PinGroups.Default;

    /// <summary>来源：<c>screenshot</c> / <c>history</c> / <c>clipboard</c> / <c>files</c> / <c>other</c>。</summary>
    public string Source { get; set; } = PinStore.SourceOther;

    /// <summary>本地时间 <c>yyyy-MM-ddTHH:mm:ss</c>；固定宽度，按字符串比较就是时间序。</summary>
    public string CreatedAt { get; set; } = "";

    /// <summary>关闭时间（同一个格式）；空 = 还开着（启动时由 <see cref="PinStore.Maintain"/> 补成上次运行的时间）。</summary>
    public string ClosedAt { get; set; } = "";

    /// <summary>贴的图有多大（像素，不是 DIP）。</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>源区域（虚拟桌面物理像素，与截图 / 找图 / 鼠标注入同一套坐标）。</summary>
    public int AreaX { get; set; }

    public int AreaY { get; set; }

    public int AreaW { get; set; }

    public int AreaH { get; set; }

    /// <summary>源区域所在显示器的缩放（截图区域 → 窗口 DIP 要除这个值）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>窗口最后的位置与尺寸（DIP）；换机 / 分辨率变了由放置逻辑夹回可见区。</summary>
    public double Left { get; set; }

    public double Top { get; set; }

    public double WinWidth { get; set; }

    public double WinHeight { get; set; }

    /// <summary>是不是实时贴图（跟住某个窗口）。</summary>
    public bool Live { get; set; }

    /// <summary>
    /// 贴的是什么：<c>image</c> / <c>text</c> / <c>color</c>（颜色取样板）。
    /// 文字与色卡会把原文留在 <see cref="Text"/> 里，贴回时按它重画（上游也是从文字重渲染）。
    /// </summary>
    public string Kind { get; set; } = PinStore.KindImage;

    /// <summary>文字 / 颜色原文（图片贴图是空串）。</summary>
    public string Text { get; set; } = "";

    /// <summary>色卡宽度（342 / 470；其它贴图是 0），贴回时按原尺寸重画。</summary>
    public int CardWidth { get; set; }

    /// <summary>还开着（没有关闭时间）。</summary>
    [JsonIgnore] public bool IsOpen => ClosedAt.Length == 0;

    [JsonIgnore] public string SizeText => CaptureCatalog.FormatSize(Width, Height);

    [JsonIgnore] public string TimeText => CaptureCatalog.FormatTime(CreatedAt);

    [JsonIgnore] public string ClosedText => ClosedAt.Length == 0 ? "—" : CaptureCatalog.FormatTime(ClosedAt);

    [JsonIgnore] public string SourceText => PinStore.SourceLabel(Source);

    [JsonIgnore] public string GroupText => Group.Length > 0 ? Group : PinGroups.Default;
}

/// <summary><c>pins/pins.json</c> 的磁盘形状（只有一个列表）。</summary>
internal sealed class PinStoreFile
{
    public List<PinRecord> Items { get; set; } = new();
}

/// <summary>
/// 贴图仓库：把「贴到屏幕上的东西」落盘，供贴图管理页 / 历史恢复使用。
/// </summary>
/// <remarks>
/// <para>
/// 目录约定（照 <see cref="CaptureCatalog"/> 同一套写法）：<c>pins.json</c>（元数据）
/// + <c>assets/&lt;id&gt;.png</c>（原图）+ <c>thumbnails/&lt;id&gt;.png</c>（列表小图）。
/// 进备份域（<c>BackupService</c> 的目录白名单里加一行 <c>pins</c>），换机后还能在管理页看到。
/// </para>
/// <para>
/// <b>贴图本身仍然是「关掉就从屏幕消失」</b>：这里记的是历史，不是「重启后自动恢复到桌面」——
/// 恢复由用户在管理页点（P5 的口径与上游一致：管理页负责「再贴一次」）。
/// </para>
/// <para>
/// 写文件一律「先写 <c>.tmp</c> 再替换」；所有方法可从任意线程调用（外部锁 + 静态图片已 Freeze）。
/// 图片落盘丢后台（<see cref="SaveAssets"/>），别在 UI 线程 / 钩子线程上等。
/// </para>
/// </remarks>
internal static class PinStore
{
    public const string SourceScreenshot = "screenshot";
    public const string SourceHistory = "history";
    public const string SourceClipboard = "clipboard";
    public const string SourceFiles = "files";
    public const string SourceOther = "other";

    /// <summary>贴的是一张图。</summary>
    public const string KindImage = "image";

    /// <summary>贴的是一段文字（贴图历史里留原文，贴回时重画）。</summary>
    public const string KindText = "text";

    /// <summary>贴的是颜色取样板（原文 = 那段颜色文本）。</summary>
    public const string KindColor = "color";

    /// <summary>最多留多少条（按创建时间倒序）。</summary>
    public const int MaxEntries = 200;

    /// <summary>最多留多少天。</summary>
    public const int MaxAgeDays = 30;

    /// <summary>缩略图长边像素（与管理页卡片同规格）。</summary>
    public const int ThumbSide = 240;

    /// <summary>分组上限（照上游 <c>PinnedWindowGroup</c> 的 128）。</summary>
    public const int MaxGroups = 128;

    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss";

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>记录 / 图片变了（新增、关闭、删除、清空）：管理页据此刷新。</summary>
    public static event Action? Changed;

    public static string Root => Path.Combine(AppPaths.DataRoot, "pins");

    public static string AssetsDir => Path.Combine(Root, "assets");

    public static string ThumbsDir => Path.Combine(Root, "thumbnails");

    public static string IndexPath => Path.Combine(Root, "pins.json");

    public static string AssetPath(string id) => Path.Combine(AssetsDir, id + ".png");

    public static string ThumbPath(string id) => Path.Combine(ThumbsDir, id + ".png");

    // ============ 读写 ============

    /// <summary>读全部记录（创建时间倒序）；文件不存在 / 读坏了返回空表，不抛。</summary>
    public static List<PinRecord> Load()
    {
        lock (Gate) return LoadUnsafe();
    }

    private static List<PinRecord> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new List<PinRecord>();
            var file = JsonSerializer.Deserialize<PinStoreFile>(
                File.ReadAllText(IndexPath, Encoding.UTF8), Opt);
            var items = file?.Items ?? new List<PinRecord>();
            items.RemoveAll(e => e == null || string.IsNullOrWhiteSpace(e.Id));
            return items.OrderByDescending(e => e.CreatedAt, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinStore.Load", "读取贴图历史失败：" + ex.Message);
            return new List<PinRecord>();
        }
    }

    /// <summary>
    /// 新建一条贴图记录（只写 <c>pins.json</c>，很快）；图片由调用方随后
    /// <see cref="SaveAssets"/> 丢后台写。顺带按保留策略剪枝。
    /// </summary>
    public static PinRecord Create(int width, int height, int areaX, int areaY, int areaW, int areaH,
        double scale, string source, string group, bool live,
        string kind = KindImage, string text = "", int cardWidth = 0)
    {
        var now = DateTime.Now;

        lock (Gate)
        {
            var items = LoadUnsafe();
            var record = new PinRecord
            {
                Id = IdFactory.NewId(x => items.Any(e => e.Id == x) || File.Exists(AssetPath(x))),
                Group = string.IsNullOrWhiteSpace(group) ? PinGroups.Default : group,
                Source = NormalizeSource(source),
                Kind = NormalizeKind(kind),
                Text = text ?? "",
                CardWidth = Math.Max(0, cardWidth),
                CreatedAt = now.ToString(TimeFormat, CultureInfo.InvariantCulture),
                Width = Math.Max(0, width),
                Height = Math.Max(0, height),
                AreaX = areaX,
                AreaY = areaY,
                AreaW = Math.Max(0, areaW),
                AreaH = Math.Max(0, areaH),
                Scale = scale > 0 ? scale : 1.0,
                WinWidth = areaW / (scale > 0 ? scale : 1.0),
                WinHeight = areaH / (scale > 0 ? scale : 1.0),
                Live = live,
            };

            items.Insert(0, record);
            var (keep, drop) = PlanPrune(items, now, MaxEntries, MaxAgeDays);
            SaveUnsafe(keep);
            DeleteFiles(drop);
            Changed?.Invoke();
            return record;
        }
    }

    /// <summary>
    /// 更新窗口状态（拖动 / 缩放 / 换分组 / 实时开关）。记录不存在就什么也不做。
    /// </summary>
    public static void UpdateState(string id, string? group, double left, double top,
        double width, double height, bool live)
    {
        if (string.IsNullOrEmpty(id)) return;

        lock (Gate)
        {
            var items = LoadUnsafe();
            var record = items.FirstOrDefault(e => e.Id == id);
            if (record == null) return;

            bool dirty = false;
            if (group != null && record.Group != group) { record.Group = group; dirty = true; }
            if (!Nearly(record.Left, left)) { record.Left = left; dirty = true; }
            if (!Nearly(record.Top, top)) { record.Top = top; dirty = true; }
            if (!Nearly(record.WinWidth, width)) { record.WinWidth = width; dirty = true; }
            if (!Nearly(record.WinHeight, height)) { record.WinHeight = height; dirty = true; }
            if (record.Live != live) { record.Live = live; dirty = true; }

            if (!dirty) return;
            SaveUnsafe(items);
            Changed?.Invoke();
        }
    }

    /// <summary>关掉一张：记关闭时间与最后落点（再贴出来时按它复位）。</summary>
    public static void MarkClosed(string id, double left, double top, double width, double height)
    {
        if (string.IsNullOrEmpty(id)) return;

        lock (Gate)
        {
            var items = LoadUnsafe();
            var record = items.FirstOrDefault(e => e.Id == id);
            if (record == null) return;
            if (record.ClosedAt.Length > 0) return;

            record.ClosedAt = DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);
            record.Left = left;
            record.Top = top;
            record.WinWidth = width;
            record.WinHeight = height;
            record.Live = false;
            SaveUnsafe(items);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// 重新贴出来（管理页的「贴回屏幕」）：清掉关闭时间，记录回到「还开着」。
    /// 窗口本身由 <see cref="CapturePinManager"/> 用记录里的图与落点建；找不到记录返回 false。
    /// </summary>
    public static bool Reopen(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        lock (Gate)
        {
            var items = LoadUnsafe();
            var record = items.FirstOrDefault(e => e.Id == id);
            if (record == null) return false;
            if (record.ClosedAt.Length == 0) return true;

            record.ClosedAt = "";
            record.Live = false;
            SaveUnsafe(items);
            Changed?.Invoke();
            return true;
        }
    }

    /// <summary>删一条（连同两张图）。</summary>
    public static void Remove(string id)
    {
        if (string.IsNullOrEmpty(id)) return;

        lock (Gate)
        {
            var items = LoadUnsafe();
            var record = items.FirstOrDefault(e => e.Id == id);
            if (record == null) return;

            items.Remove(record);
            SaveUnsafe(items);
            DeleteFile(AssetPath(id));
            DeleteFile(ThumbPath(id));
            Changed?.Invoke();
        }
    }

    /// <summary>清空（连同全部图片）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            var items = LoadUnsafe();
            SaveUnsafe(new List<PinRecord>());
            DeleteFiles(items);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// 启动时调一次：上次没关掉就退出的记录补上关闭时间（否则管理页会一直显示「还开着」），
    /// 再按保留策略剪枝。目录不存在就什么也不做。
    /// </summary>
    public static void Maintain()
    {
        try
        {
            if (!Directory.Exists(Root)) return;

            lock (Gate)
            {
                var items = LoadUnsafe();
                bool dirty = false;
                string now = DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);
                foreach (var record in items)
                {
                    if (record.ClosedAt.Length > 0) continue;
                    record.ClosedAt = now;      // 程序已经不在了，屏幕上什么都没有
                    record.Live = false;
                    dirty = true;
                }

                var (keep, drop) = PlanPrune(items, DateTime.Now, MaxEntries, MaxAgeDays);
                if (!dirty && drop.Count == 0 && keep.Count == items.Count) return;

                SaveUnsafe(keep);
                DeleteFiles(drop);
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinStore.Maintain", "清理贴图历史失败：" + ex.Message);
        }
    }

    /// <summary>把原图与缩略图落盘（可放后台线程；<paramref name="image"/> 必须已 Freeze）。</summary>
    public static void SaveAssets(PinRecord record, BitmapSource image)
    {
        try
        {
            Directory.CreateDirectory(AssetsDir);
            Directory.CreateDirectory(ThumbsDir);

            WritePng(AssetPath(record.Id), image);

            var (tw, th) = ThumbnailSize(image.PixelWidth, image.PixelHeight, ThumbSide);
            if (tw > 0 && th > 0)
            {
                var thumb = new TransformedBitmap(image, new ScaleTransform(
                    (double)tw / image.PixelWidth, (double)th / image.PixelHeight));
                thumb.Freeze();
                WritePng(ThumbPath(record.Id), thumb);
            }

            // 写图期间这条被删了：别留下孤儿文件
            lock (Gate)
            {
                if (LoadUnsafe().All(e => e.Id != record.Id))
                {
                    DeleteFile(AssetPath(record.Id));
                    DeleteFile(ThumbPath(record.Id));
                    return;
                }
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinStore.SaveAssets", "写贴图图片失败：" + ex.Message);
        }
    }

    /// <summary>读一张贴图原图（<paramref name="decodeWidth"/> = 0 表示原分辨率）；读不动返回 null。</summary>
    public static BitmapSource? LoadImage(string path, int decodeWidth = 0) =>
        CaptureCatalog.LoadImage(path, decodeWidth);

    /// <summary>读某条记录的原图。</summary>
    public static BitmapSource? LoadAsset(string id, int decodeWidth = 0) =>
        LoadImage(AssetPath(id), decodeWidth);

    /// <summary>读某条记录的缩略图（没有小图时退回原图，decodeWidth 限宽）。</summary>
    public static BitmapSource? LoadThumb(string id, int decodeWidth = 0)
    {
        string thumb = ThumbPath(id);
        return File.Exists(thumb)
            ? LoadImage(thumb, decodeWidth)
            : LoadAsset(id, decodeWidth);
    }

    // ============ 纯函数（自检直接断言） ============

    /// <summary>保留 / 丢弃怎么分：先按创建时间倒序，超龄或超量的进 discard。</summary>
    public static (List<PinRecord> Keep, List<PinRecord> Drop) PlanPrune(
        IEnumerable<PinRecord> items, DateTimeOffset now, int maxCount, int maxAgeDays)
    {
        var ordered = items
            .OrderByDescending(e => e.CreatedAt, StringComparer.Ordinal)
            .ToList();

        var keep = new List<PinRecord>();
        var drop = new List<PinRecord>();
        string cutoff = maxAgeDays > 0
            ? now.AddDays(-maxAgeDays).ToString(TimeFormat, CultureInfo.InvariantCulture)
            : "";

        foreach (var record in ordered)
        {
            bool tooOld = cutoff.Length > 0 && string.CompareOrdinal(record.CreatedAt, cutoff) < 0;
            if (tooOld || keep.Count >= maxCount) drop.Add(record);
            else keep.Add(record);
        }

        return (keep, drop);
    }

    /// <summary>缩略图尺寸：按长边缩到 <paramref name="maxSide"/>，不放大小图。</summary>
    public static (int Width, int Height) ThumbnailSize(int width, int height, int maxSide) =>
        CaptureCatalog.ThumbnailSize(width, height, maxSide);

    /// <summary>来源的中文名（管理页的「来源」列与筛选项共用）。</summary>
    public static string SourceLabel(string? source) => source switch
    {
        SourceHistory => "采集历史",
        SourceClipboard => "剪贴板",
        SourceFiles => "选中的文件",
        SourceScreenshot => "截图",
        _ => "其它",
    };

    /// <summary>把来源归一成五个已知值（认不出的当「其它」，别让脏数据进筛选）。</summary>
    public static string NormalizeSource(string? source) => source switch
    {
        SourceHistory => SourceHistory,
        SourceClipboard => SourceClipboard,
        SourceFiles => SourceFiles,
        SourceScreenshot => SourceScreenshot,
        _ => SourceOther,
    };

    /// <summary>把贴图种类归一（认不出的当图片：老记录没有这个字段）。</summary>
    public static string NormalizeKind(string? kind) => kind switch
    {
        KindText => KindText,
        KindColor => KindColor,
        _ => KindImage,
    };

    /// <summary>是不是「从原文重画」的贴图（文字卡 / 色卡，不依赖 <c>assets/</c> 里的 png）。</summary>
    public static bool IsTextual(PinRecord record) => NormalizeKind(record.Kind) != KindImage;

    /// <summary>管理页的筛选项（照上游「全部 + 五个来源」的顺序；空值 = 全部）。</summary>
    public static IReadOnlyList<(string Key, string Label)> SourceOptions() => new[]
    {
        (SourceScreenshot, SourceLabel(SourceScreenshot)),
        (SourceHistory, SourceLabel(SourceHistory)),
        (SourceClipboard, SourceLabel(SourceClipboard)),
        (SourceFiles, SourceLabel(SourceFiles)),
        (SourceOther, SourceLabel(SourceOther)),
    };

    /// <summary>
    /// 管理页的筛选（照上游 <c>RecordFilter::matches</c>）：来源多选为空 = 全部来源；
    /// 日期两端都可空，按**本地日期**比较（记录里存的是本地时间）。
    /// </summary>
    public static bool Matches(PinRecord record, IReadOnlyCollection<string>? sources,
        DateTime? start, DateTime? end)
    {
        if (sources != null && sources.Count > 0 && !sources.Contains(NormalizeSource(record.Source)))
            return false;

        if (start == null && end == null) return true;

        if (!DateTime.TryParseExact(record.CreatedAt, TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var created))
        {
            return false;
        }

        DateTime date = created.Date;
        return (start == null || date >= start.Value.Date) && (end == null || date <= end.Value.Date);
    }

    /// <summary>当前页覆盖的区间（左闭右开）；没有记录时是 <c>(0, 0)</c>。</summary>
    public static (int First, int Last) PageRange(int page, int pageSize, int total)
    {
        if (pageSize <= 0 || total <= 0) return (0, 0);

        int first = Math.Max(0, (Math.Max(1, page) - 1) * pageSize);
        return (first, Math.Min(first + pageSize, total));
    }

    /// <summary>总页数（没有记录时也算 1 页，界面不留「第 0 / 0 页」）。</summary>
    public static int PageCount(int total, int pageSize)
    {
        if (pageSize <= 0) return 1;
        return Math.Max(1, (Math.Max(0, total) + pageSize - 1) / pageSize);
    }

    private static bool Nearly(double a, double b) => Math.Abs(a - b) < 0.01;

    private static void SaveUnsafe(List<PinRecord> items)
    {
        try
        {
            Directory.CreateDirectory(Root);
            string tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new PinStoreFile { Items = items }, Opt),
                new UTF8Encoding(false));
            File.Move(tmp, IndexPath, true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinStore.Save", "写贴图历史失败：" + ex.Message);
        }
    }

    private static void WritePng(string path, BitmapSource image)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }

    private static void DeleteFiles(IEnumerable<PinRecord> records)
    {
        foreach (var record in records)
        {
            DeleteFile(AssetPath(record.Id));
            DeleteFile(ThumbPath(record.Id));
        }
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Warn("PinStore.Delete", "删贴图文件失败：" + ex.Message); }
    }
}
