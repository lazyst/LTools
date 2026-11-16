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

/// <summary>一条采集记录：一次截图的成品图 + 它是怎么完成的 + （识别时）认出来的文字。</summary>
/// <remarks>
/// 只存事实，显示用的派生文案放在 <see cref="CaptureCatalog"/> 的纯函数里（<see cref="KindLabel"/> 等），
/// 这样 <c>--selftest</c> 能直接断言、窗口也能共用同一份说法。
/// </remarks>
internal sealed class CaptureEntry
{
    /// <summary>ULID（见 <c>Core/Ids.cs</c>），同时是图片文件名。</summary>
    public string Id { get; set; } = "";

    /// <summary><c>shot</c>（截图）或 <c>recognize</c>（识别）。</summary>
    public string Kind { get; set; } = CaptureCatalog.KindShot;

    /// <summary>完成动作：复制 / 贴图 / 识别 / 另存为。</summary>
    public string Source { get; set; } = "";

    /// <summary>本地时间 <c>yyyy-MM-ddTHH:mm:ss</c>；固定宽度、按字符串比较就是时间序。</summary>
    public string CreatedAt { get; set; } = "";

    /// <summary>成品图的像素宽高（不是 DIP）。</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>识别出来的文字；截图条目恒为空串，识别条目在识别完成后回填。</summary>
    public string Text { get; set; } = "";

    /// <summary>识别状态：<c>pending</c>（还没有结果）/ <c>done</c>；截图条目恒为 done。</summary>
    public string State { get; set; } = "";

    /// <summary>
    /// 录屏条目的成品视频（相对 <c>captures/</c> 的路径，如 <c>records/&lt;id&gt;.mp4</c>）；截图 / 识别条目为空。
    /// </summary>
    public string File { get; set; } = "";

    /// <summary>录屏时长（毫秒）；其它条目为 0。</summary>
    public int DurationMs { get; set; }

    /// <summary>识别还没跑完（给列表显示「识别中…」用）。</summary>
    [JsonIgnore] public bool Pending => State == "pending";

    [JsonIgnore] public string KindLabel => CaptureCatalog.KindLabel(Kind);

    [JsonIgnore] public string SizeText => CaptureCatalog.FormatSize(Width, Height);

    [JsonIgnore] public string TimeText => CaptureCatalog.FormatTime(CreatedAt);

    [JsonIgnore] public string SourceText => Source.Length > 0 ? Source : "—";

    [JsonIgnore] public bool IsRecognize => Kind == CaptureCatalog.KindRecognize;

    [JsonIgnore] public bool IsRecord => Kind == CaptureCatalog.KindRecord;

    /// <summary>录屏时长文案（<c>mm:ss</c>）。</summary>
    [JsonIgnore] public string DurationText => CaptureCatalog.FormatClock(DurationMs);

    /// <summary>成品格式（<c>MP4</c> / <c>GIF</c>）；不是录屏就是空串。</summary>
    [JsonIgnore] public string FormatText => IsRecord ? CaptureCatalog.FormatLabel(CaptureCatalog.ExtensionOf(File)) : "";
}

/// <summary><c>captures/catalog.json</c> 的磁盘形状（只有一个列表）。</summary>
internal sealed class CaptureCatalogFile
{
    public List<CaptureEntry> Items { get; set; } = new();
}

/// <summary>
/// 采集历史：截图 / 识别的成品图与文字，落在数据根 <c>captures/</c>。
/// </summary>
/// <remarks>
/// <para>
/// 目录约定：<c>catalog.json</c>（元数据，只有一份）+ <c>assets/&lt;id&gt;.png</c>（成品原图）
/// + <c>thumbnails/&lt;id&gt;.png</c>（列表用的小图，长边 240）。
/// 进备份域（<c>BackupService</c> 的目录白名单），所以换机后历史还在。
/// </para>
/// <para>
/// <b>保留策略</b>：最多 <see cref="MaxEntries"/> 条、最多 <see cref="MaxAgeDays"/> 天，超出的
/// 连同两张图一起删。<see cref="PlanPrune"/> 是纯函数（自检直接断言），
/// <see cref="Maintain"/> / <see cref="Create"/> 负责真正落盘。
/// </para>
/// <para>
/// 写目录文件一律「先写 <c>.tmp</c> 再替换」；所有方法可从任意线程调用（外部锁 + 静态图片已 Freeze）。
/// </para>
/// </remarks>
internal static class CaptureCatalog
{
    public const string KindShot = "shot";
    public const string KindRecognize = "recognize";
    public const string KindRecord = "record";

    /// <summary>最多留多少条（按时间倒序）。</summary>
    public const int MaxEntries = 200;

    /// <summary>最多留多少天。</summary>
    public const int MaxAgeDays = 30;

    /// <summary>缩略图长边像素。</summary>
    public const int ThumbSide = 240;

    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss";

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>目录 / 条目变了（新增、回填文字、删除、清空）：窗口据此刷新列表。</summary>
    public static event Action? Changed;

    public static string Root => Path.Combine(AppPaths.DataRoot, "captures");

    public static string AssetsDir => Path.Combine(Root, "assets");

    public static string ThumbsDir => Path.Combine(Root, "thumbnails");

    /// <summary>录屏成品（mp4 / gif）放这里；元数据仍只在 <c>catalog.json</c>。</summary>
    public static string RecordsDir => Path.Combine(Root, "records");

    public static string CatalogPath => Path.Combine(Root, "catalog.json");

    public static string AssetPath(string id) => Path.Combine(AssetsDir, id + ".png");

    public static string ThumbPath(string id) => Path.Combine(ThumbsDir, id + ".png");

    public static string RecordPath(string id, string extension) =>
        Path.Combine(RecordsDir, id + "." + NormalizeExtension(extension));

    /// <summary><see cref="CaptureEntry.File"/> 里存的（相对 <c>captures/</c>、正斜杠）。</summary>
    public static string RecordRelative(string id, string extension) =>
        "records/" + id + "." + NormalizeExtension(extension);

    /// <summary>录屏成品的绝对路径（相对 <see cref="Root"/>，即 <c>captures/records/x.mp4</c>）；非录屏条目返回空串。</summary>
    public static string FilePath(CaptureEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.File)) return "";
        return Path.Combine(Root, entry.File.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>小写扩展名（不含点）；没有扩展名返回空串。</summary>
    public static string ExtensionOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string ext = Path.GetExtension(path);
        return ext.Length > 1 ? ext[1..].ToLowerInvariant() : "";
    }

    /// <summary>
    /// 读一张采集图（<paramref name="decodeWidth"/> = 0 表示原分辨率）；文件不存在 / 读不动返回 null。
    /// 覆盖层翻历史与采集历史窗共用这一份——别各自再写一遍 <c>BitmapImage</c> 的开档参数。
    /// </summary>
    public static BitmapSource? LoadImage(string path, int decodeWidth = 0)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CaptureCatalog.LoadImage", "读图失败：" + ex.Message);
            return null;
        }
    }

    private static string NormalizeExtension(string? extension) =>
        string.Equals(extension?.Trim(), "gif", StringComparison.OrdinalIgnoreCase) ? "gif" : "mp4";

    /// <summary>时长文案（录屏用；与录屏参数共用同一份实现）。</summary>
    public static string FormatClock(int totalMs) => RecordPlan.FormatClock(totalMs);

    /// <summary>成品格式的中文名（MP4 / GIF）。</summary>
    public static string FormatLabel(string extension) => RecordPlan.FormatLabel(extension);

    /// <summary>文件大小文案（<c>820 KB</c> / <c>3.4 MB</c>）；0 或负数返回空串。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "";

        double kb = bytes / 1024.0;
        if (kb < 1024) return kb < 10 ? $"{kb:0.0} KB" : $"{Math.Round(kb):0} KB";

        double mb = kb / 1024.0;
        return mb < 10 ? $"{mb:0.0} MB" : $"{Math.Round(mb):0} MB";
    }

    // ============ 读写 ============

    /// <summary>读全部条目（时间倒序）；文件不存在 / 读坏了返回空表，不抛。</summary>
    public static List<CaptureEntry> Load()
    {
        lock (Gate) return LoadUnsafe();
    }

    private static List<CaptureEntry> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(CatalogPath)) return new List<CaptureEntry>();
            var file = JsonSerializer.Deserialize<CaptureCatalogFile>(
                File.ReadAllText(CatalogPath, Encoding.UTF8), Opt);
            var items = file?.Items ?? new List<CaptureEntry>();
            items.RemoveAll(e => e == null || string.IsNullOrWhiteSpace(e.Id));

            // 识别中途关掉程序 / 崩溃：pending 永远等不到回填，十分钟后按「没有文字」显示
            var stale = DateTime.Now.AddMinutes(-10);
            foreach (var entry in items)
            {
                if (entry.State != "pending") continue;
                if (!DateTime.TryParseExact(entry.CreatedAt, TimeFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var time) || time < stale)
                    entry.State = "done";
            }

            return items.OrderByDescending(e => e.CreatedAt, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CaptureCatalog.Load", "读取采集历史失败：" + ex.Message);
            return new List<CaptureEntry>();
        }
    }

    /// <summary>
    /// 先记一条（只写 <c>catalog.json</c>，很快），图片由调用方随后
    /// <see cref="SaveAssets"/> 丢后台写。顺带按保留策略剪枝。
    /// </summary>
    public static CaptureEntry Create(int width, int height, string kind, string source)
    {
        var now = DateTime.Now;

        lock (Gate)
        {
            var items = LoadUnsafe();
            var entry = new CaptureEntry
            {
                Id = IdFactory.NewId(id => items.Any(e => e.Id == id) || File.Exists(AssetPath(id))),
                Kind = kind == KindRecognize ? KindRecognize : kind == KindRecord ? KindRecord : KindShot,
                Source = source ?? "",
                CreatedAt = now.ToString(TimeFormat, CultureInfo.InvariantCulture),
                Width = Math.Max(0, width),
                Height = Math.Max(0, height),
                State = kind == KindRecognize ? "pending" : "done",
            };

            items.Insert(0, entry);
            var (keep, drop) = PlanPrune(items, now, MaxEntries, MaxAgeDays);
            SaveUnsafe(keep);
            DeleteFiles(drop);
            Changed?.Invoke();
            return entry;
        }
    }

    /// <summary>
    /// 录屏成品落盘并记一条记录：先把文件搬进 <c>captures/records/</c>，再写 <c>catalog.json</c>。
    /// 文件搬不过去就返回 null（调用方报错），绝不留下「有条目、没文件」的坏记录。
    /// </summary>
    /// <param name="poster">第一帧（封面 / 缩略图）；没抓到画面时传 null。</param>
    public static CaptureEntry? CreateRecord(int width, int height, string source, string extension,
        int durationMs, string tempFile, BitmapSource? poster)
    {
        string ext = NormalizeExtension(extension);
        if (!File.Exists(tempFile)) return null;

        var now = DateTime.Now;
        CaptureEntry entry;

        lock (Gate)
        {
            var items = LoadUnsafe();
            string id = IdFactory.NewId(x =>
                items.Any(e => e.Id == x) || File.Exists(AssetPath(x)) || File.Exists(RecordPath(x, ext)));

            entry = new CaptureEntry
            {
                Id = id,
                Kind = KindRecord,
                Source = source ?? "",
                CreatedAt = now.ToString(TimeFormat, CultureInfo.InvariantCulture),
                Width = Math.Max(0, width),
                Height = Math.Max(0, height),
                DurationMs = Math.Max(0, durationMs),
                File = RecordRelative(id, ext),
                State = "done",
            };

            try
            {
                Directory.CreateDirectory(RecordsDir);
                File.Move(tempFile, RecordPath(id, ext), true);
            }
            catch (Exception ex)
            {
                AppLog.Warn("CaptureCatalog.CreateRecord", "录制文件落盘失败：" + ex.Message);
                return null;
            }

            items.Insert(0, entry);
            var (keep, drop) = PlanPrune(items, now, MaxEntries, MaxAgeDays);
            SaveUnsafe(keep);
            DeleteFiles(drop);
            Changed?.Invoke();
        }

        if (poster != null) SaveAssets(entry, poster);
        return entry;
    }

    /// <summary>把成品图与缩略图落盘（可放后台线程；<paramref name="image"/> 必须已 Freeze）。</summary>
    public static void SaveAssets(CaptureEntry entry, BitmapSource image)
    {
        try
        {
            Directory.CreateDirectory(AssetsDir);
            Directory.CreateDirectory(ThumbsDir);

            WritePng(AssetPath(entry.Id), image);

            var (tw, th) = ThumbnailSize(image.PixelWidth, image.PixelHeight, ThumbSide);
            if (tw > 0 && th > 0)
            {
                var thumb = new TransformedBitmap(image, new ScaleTransform(
                    (double)tw / image.PixelWidth, (double)th / image.PixelHeight));
                thumb.Freeze();
                WritePng(ThumbPath(entry.Id), thumb);
            }

            // 写图期间这条被删了：别留下孤儿文件
            lock (Gate)
            {
                if (LoadUnsafe().All(e => e.Id != entry.Id))
                {
                    DeleteFile(AssetPath(entry.Id));
                    DeleteFile(ThumbPath(entry.Id));
                    return;
                }
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CaptureCatalog.SaveAssets", "写采集图片失败：" + ex.Message);
        }
    }

    /// <summary>识别完成后把文字回填到那条记录（找不到就什么也不做）。</summary>
    public static void SetText(string id, string text)
    {
        if (string.IsNullOrEmpty(id)) return;

        lock (Gate)
        {
            var items = LoadUnsafe();
            var entry = items.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;

            string value = text ?? "";
            if (entry.Text == value && entry.State != "pending") return;

            entry.Text = value;
            entry.State = "done";
            SaveUnsafe(items);
            Changed?.Invoke();
        }
    }

    /// <summary>删一条（连同两张图与录屏文件）。</summary>
    public static void Remove(string id)
    {
        if (string.IsNullOrEmpty(id)) return;

        lock (Gate)
        {
            var items = LoadUnsafe();
            var entry = items.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;

            items.Remove(entry);
            SaveUnsafe(items);
            DeleteFile(AssetPath(id));
            DeleteFile(ThumbPath(id));

            string file = FilePath(entry);
            if (file.Length > 0) DeleteFile(file);

            Changed?.Invoke();
        }
    }

    /// <summary>清空历史（连同全部图片）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            var items = LoadUnsafe();
            SaveUnsafe(new List<CaptureEntry>());
            DeleteFiles(items);
            Changed?.Invoke();
        }
    }

    /// <summary>按保留策略剪掉超量 / 过期的条目（启动时调一次；目录不存在就什么也不做）。</summary>
    public static void Maintain()
    {
        try
        {
            if (!Directory.Exists(Root)) return;

            lock (Gate)
            {
                var items = LoadUnsafe();
                var (keep, drop) = PlanPrune(items, DateTime.Now, MaxEntries, MaxAgeDays);
                if (drop.Count == 0 && keep.Count == items.Count) return;

                SaveUnsafe(keep);
                DeleteFiles(drop);
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("CaptureCatalog.Maintain", "清理采集历史失败：" + ex.Message);
        }
    }

    private static void SaveUnsafe(List<CaptureEntry> items)
    {
        try
        {
            Directory.CreateDirectory(Root);
            string tmp = CatalogPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CaptureCatalogFile { Items = items }, Opt),
                new UTF8Encoding(false));
            File.Move(tmp, CatalogPath, true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CaptureCatalog.Save", "写采集历史失败：" + ex.Message);
        }
    }

    private static void WritePng(string path, BitmapSource image)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }

    private static void DeleteFiles(IEnumerable<CaptureEntry> entries)
    {
        foreach (var entry in entries)
        {
            DeleteFile(AssetPath(entry.Id));
            DeleteFile(ThumbPath(entry.Id));

            string file = FilePath(entry);
            if (file.Length > 0) DeleteFile(file);
        }
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Warn("CaptureCatalog.Delete", "删图失败：" + ex.Message); }
    }

    // ============ 纯函数（自检直接断言） ============

    /// <summary>保留 / 丢弃怎么分：先按时间倒序，超龄或超量的进 discard。</summary>
    public static (List<CaptureEntry> Keep, List<CaptureEntry> Drop) PlanPrune(
        IEnumerable<CaptureEntry> items, DateTimeOffset now, int maxCount, int maxAgeDays)
    {
        var ordered = items
            .OrderByDescending(e => e.CreatedAt, StringComparer.Ordinal)
            .ToList();

        var keep = new List<CaptureEntry>();
        var drop = new List<CaptureEntry>();
        string cutoff = maxAgeDays > 0
            ? now.AddDays(-maxAgeDays).ToString(TimeFormat, CultureInfo.InvariantCulture)
            : "";

        foreach (var entry in ordered)
        {
            bool tooOld = cutoff.Length > 0 && string.CompareOrdinal(entry.CreatedAt, cutoff) < 0;
            if (tooOld || keep.Count >= maxCount) drop.Add(entry);
            else keep.Add(entry);
        }

        return (keep, drop);
    }

    /// <summary>缩略图尺寸：按长边缩到 <paramref name="maxSide"/>，不放大小图。</summary>
    public static (int Width, int Height) ThumbnailSize(int width, int height, int maxSide)
    {
        if (width <= 0 || height <= 0 || maxSide <= 0) return (0, 0);

        int longest = Math.Max(width, height);
        if (longest <= maxSide) return (width, height);

        double scale = (double)maxSide / longest;
        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    public static string KindLabel(string kind) => kind switch
    {
        KindRecognize => "识别",
        KindRecord => "录屏",
        _ => "截图",
    };

    public static string FormatSize(int width, int height) => $"{width} × {height}";

    /// <summary>时间文案：今年只写月日时分，跨年补上年份。</summary>
    public static string FormatTime(string createdAt, DateTime now)
    {
        if (DateTime.TryParseExact(createdAt, TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
        {
            return time.Year == now.Year
                ? time.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture)
                : time.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        }
        return createdAt ?? "";
    }

    public static string FormatTime(string createdAt) => FormatTime(createdAt, DateTime.Now);

    /// <summary>列表里的摘要：认出文字的第一行（长文本截断）；录屏写格式 + 完成动作；其余按类型给一句。</summary>
    public static string SummaryOf(CaptureEntry entry)
    {
        if (entry.IsRecord)
        {
            string format = FormatLabel(ExtensionOf(entry.File));
            return entry.Source.Length > 0 ? $"{format} · {entry.Source}" : format;
        }

        if (entry.IsRecognize)
        {
            if (entry.Pending) return "识别中…";

            string line = FirstLine(entry.Text);
            if (line.Length == 0) return "（没有识别到文字）";
            return line.Length <= 40 ? line : line[..40] + "…";
        }
        return entry.Source;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        foreach (string line in text.Replace("\r", "").Split('\n'))
            if (line.Trim().Length > 0) return line.Trim();
        return "";
    }
}
