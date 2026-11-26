using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
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

    /// <summary>
    /// 当初框的选区（虚拟桌面物理像素，与截图 / 找图 / 鼠标注入同一套坐标）。
    /// 老记录没有落位信息，<see cref="SelectionW"/> / <see cref="SelectionH"/> 是 0。
    /// </summary>
    public int SelectionX { get; set; }

    public int SelectionY { get; set; }

    public int SelectionW { get; set; }

    public int SelectionH { get; set; }

    /// <summary>成品图里的圆角半径（图像素；0 = 直角，见 <see cref="PinAppearance"/>）。</summary>
    public int RadiusPx { get; set; }

    /// <summary>成品图里烘进的效果内边距（阴影 / 外扩边框，图像素；0 = 没有）。</summary>
    public int EffectPx { get; set; }

    /// <summary>烘进图里的效果：<c>""</c> / <c>shadow</c> / <c>border</c>（见 <see cref="PinAppearance"/>）。</summary>
    public string Decoration { get; set; } = "";

    /// <summary>是不是长截图（拼出来的长图不按原选区贴回，也没有那一圈效果）。</summary>
    public bool Scrolling { get; set; }

    /// <summary>
    /// 这块截图压到了几块显示器（照上游 <c>record.displays.size()</c>）；直接截图 / 老记录是 1。
    /// </summary>
    public int Displays { get; set; } = 1;

    /// <summary>
    /// 当时**整块屏幕**的冻结画面（相对 <c>captures/</c> 的路径，如 <c>displays/&lt;id&gt;.png</c>）。
    /// 采集历史据此能看到「当时整个屏幕 + 选框」，并重新裁选；录屏 / 老记录为空。
    /// </summary>
    public string Display { get; set; } = "";

    /// <summary>整屏底图覆盖的虚拟桌面矩形（物理像素，<c>Display</c> 的左上角就是它的左上角）。</summary>
    public int DisplayX { get; set; }

    public int DisplayY { get; set; }

    public int DisplayW { get; set; }

    public int DisplayH { get; set; }

    /// <summary>
    /// 当时每一块屏幕的位置（虚拟桌面物理像素）：历史里「一块屏一张预览」照它从
    /// <see cref="Display"/> 那张整屏图上裁。老记录为空 = 整屏只有一张。
    /// </summary>
    public List<CaptureScreenDto> Screens { get; set; } = new();

    /// <summary>
    /// 当初框的**每一块**选区（物理像素，已含加 / 减与点列）：重新裁选时照它还原，
    /// 多块 / 自由 / 折线只有靠这个才还原得出来（<see cref="SelectionX"/> 只是外接框）。
    /// 老记录为空。
    /// </summary>
    public List<CaptureRegionDto> Regions { get; set; } = new();

    /// <summary>
    /// 当时画在成品上的标注（顺序即绘制顺序）。「重新编辑截图」照它把标注摆回覆盖层；
    /// 没画过标注 / 老记录为空。
    /// </summary>
    public JsonArray? Annotations { get; set; }

    /// <summary>标注的撤销栈（每份是一整份标注列表快照）。「重新编辑截图」后 Ctrl+Z 还能退回去。</summary>
    public JsonArray? UndoMarks { get; set; }

    /// <summary>标注的重做栈。</summary>
    public JsonArray? RedoMarks { get; set; }

    /// <summary>当时选区的外观（圆角 / 阴影 / 边框 / 背景不透明度）；老记录为空。</summary>
    public CaptureStyleDto? Style { get; set; }

    /// <summary>当时画过标注（能恢复的不只是底图与选区）。</summary>
    [JsonIgnore] public bool HasAnnotations => Annotations is { Count: > 0 };

    /// <summary>有没有整屏底图（能看整屏 / 重新裁选）。</summary>
    [JsonIgnore] public bool HasDisplay => Display.Length > 0 && DisplayW > 0 && DisplayH > 0;

    /// <summary>整屏底图的屏幕矩形（物理像素）；没有底图时是 <see cref="Int32Rect.Empty"/>。</summary>
    [JsonIgnore] public Int32Rect DisplayRect => HasDisplay
        ? new Int32Rect(DisplayX, DisplayY, DisplayW, DisplayH)
        : Int32Rect.Empty;

    /// <summary>识别还没跑完（给列表显示「识别中…」用）。</summary>
    [JsonIgnore] public bool Pending => State == "pending";

    [JsonIgnore] public string KindLabel => CaptureCatalog.KindLabel(Kind);

    [JsonIgnore] public string SizeText => CaptureCatalog.FormatSize(Width, Height);

    [JsonIgnore] public string TimeText => CaptureCatalog.FormatTime(CreatedAt);

    [JsonIgnore] public string SourceText => Source.Length > 0 ? Source : "—";

    [JsonIgnore] public bool IsRecognize => Kind == CaptureCatalog.KindRecognize;

    [JsonIgnore] public bool IsRecord => Kind == CaptureCatalog.KindRecord;

    /// <summary>能不能在采集历史里「按原选区贴回屏幕」（见 <see cref="HistoryPinPlacement"/>）。</summary>
    [JsonIgnore] public bool CanPin => HistoryPinPlacement.SupportsPin(this);

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

    public static string Root => AppPaths.CapturesDir;

    public static string AssetsDir => Path.Combine(Root, "assets");

    public static string ThumbsDir => Path.Combine(Root, "thumbnails");

    /// <summary>录屏成品（mp4 / gif）放这里；元数据仍只在 <c>catalog.json</c>。</summary>
    public static string RecordsDir => Path.Combine(Root, "records");

    /// <summary>整屏冻结画面（采集历史「看整屏 / 重新裁选」用，一条一张）。</summary>
    public static string DisplaysDir => Path.Combine(Root, "displays");

    public static string CatalogPath => Path.Combine(Root, "catalog.json");

    public static string AssetPath(string id) => Path.Combine(AssetsDir, id + ".png");

    public static string ThumbPath(string id) => Path.Combine(ThumbsDir, id + ".png");

    public static string DisplayPath(string id) => Path.Combine(DisplaysDir, id + ".png");

    /// <summary><see cref="CaptureEntry.Display"/> 里存的（相对 <c>captures/</c>、正斜杠）。</summary>
    public static string DisplayRelative(string id) => "displays/" + id + ".png";

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

    /// <summary>整屏底图的绝对路径；这条记录没有底图时返回空串。</summary>
    public static string DisplayFilePath(CaptureEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Display)) return "";
        return Path.Combine(Root, entry.Display.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>读整屏底图（<paramref name="decodeWidth"/> = 0 表示原分辨率）；没有 / 读不动返回 null。</summary>
    public static BitmapSource? LoadDisplay(CaptureEntry entry, int decodeWidth = 0) =>
        LoadImage(DisplayFilePath(entry), decodeWidth);

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

    private static string NormalizeExtension(string? extension)
    {
        string ext = (extension ?? "").Trim().ToLowerInvariant();
        return ext is "gif" or "apng" or "webp" or "mp4" ? ext : "mp4";
    }

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
    /// <param name="display">这次抓的整块屏幕（虚拟桌面物理像素）；记下来历史才能看整屏 / 重新裁选。</param>
    /// <param name="screens">当时每块屏幕的位置（历史里一块屏一张预览）。</param>
    /// <param name="edit">当时画在上面的标注 / 撤销栈 / 选区外观（「重新编辑截图」照它还原）。</param>
    public static CaptureEntry Create(int width, int height, string kind, string source,
        CapturePlacement? placement = null, Int32Rect? display = null,
        IEnumerable<Int32Rect>? screens = null, CaptureEditState? edit = null)
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

            if (placement != null)
            {
                entry.SelectionX = placement.Selection.X;
                entry.SelectionY = placement.Selection.Y;
                entry.SelectionW = Math.Max(0, placement.Selection.Width);
                entry.SelectionH = Math.Max(0, placement.Selection.Height);
                entry.RadiusPx = Math.Max(0, placement.Appearance.RadiusPx);
                entry.EffectPx = Math.Max(0, placement.Appearance.EffectPx);
                entry.Decoration = PinAppearance.NormalizeDecoration(placement.Appearance.Decoration);
                entry.Scrolling = placement.Scrolling;
                entry.Displays = CountDisplays(placement.Selection.X, placement.Selection.Y,
                    placement.Selection.Width, placement.Selection.Height,
                    Native.Screens.All().Select(s => (s.Left, s.Top, s.Width, s.Height)));

                // 每一块选区（物理像素）：多块 / 自由 / 折线只有靠这份才还原得出来。
                // 选区的图像像素相对**底图左上角**，加 display 的原点就是虚拟桌面坐标。
                if (placement.Regions is { Count: > 0 } regions && display is Int32Rect screen)
                    entry.Regions = CaptureRegionDto.Of(regions, screen.X, screen.Y);
            }

            if (display is Int32Rect area && area.Width > 0 && area.Height > 0)
            {
                entry.Display = DisplayRelative(entry.Id);
                entry.DisplayX = area.X;
                entry.DisplayY = area.Y;
                entry.DisplayW = area.Width;
                entry.DisplayH = area.Height;

                if (screens != null)
                    entry.Screens = CaptureScreenDto.Of(screens);
            }

            if (edit != null)
            {
                // 「重新编辑截图」要的三样：标注、撤销 / 重做栈、选区外观。
                // 没画过标注时只留外观（省得每条都挂一个空数组）。
                if (edit.Annotations is { Count: > 0 })
                {
                    entry.Annotations = edit.Annotations;
                    entry.UndoMarks = edit.Undo;
                    entry.RedoMarks = edit.Redo;
                }

                entry.Style = edit.Style;
            }

            items.Insert(0, entry);
            if (CaptureHistoryStore.Enabled)
            {
                var (keep, drop) = PlanPrune(items, now, CaptureHistoryStore.Current);
                SaveUnsafe(keep);
                DeleteFiles(drop);
                Changed?.Invoke();
            }

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
            if (CaptureHistoryStore.Enabled)
            {
                var (keep, drop) = PlanPrune(items, now, CaptureHistoryStore.Current);
                SaveUnsafe(keep);
                DeleteFiles(drop);
                Changed?.Invoke();
            }
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

    /// <summary>把整屏冻结画面落盘（可放后台线程；<paramref name="image"/> 必须已 Freeze）。</summary>
    public static void SaveDisplay(CaptureEntry entry, BitmapSource image)
    {
        if (string.IsNullOrWhiteSpace(entry.Display) || image == null) return;

        try
        {
            Directory.CreateDirectory(DisplaysDir);
            string path = DisplayPath(entry.Id);
            WritePng(path, image);

            // 写图期间这条被删了：别留下孤儿文件
            lock (Gate)
            {
                if (LoadUnsafe().All(e => e.Id != entry.Id))
                {
                    DeleteFile(path);
                    return;
                }
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CaptureCatalog.SaveDisplay", "写整屏底图失败：" + ex.Message);
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
            DeleteFile(DisplayPath(id));

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
                CaptureHistoryPolicy policy = CaptureHistoryStore.Current;

                // 「永久保留」= 不按天数 / 条数 / 磁盘裁剪，但孤儿文件仍然扫掉
                if (policy.KeepPermanently)
                {
                    SweepOrphans(items);
                    return;
                }

                var (keep, drop) = PlanPrune(items, DateTime.Now, policy);

                // 再按磁盘上限从最旧的开始丢（上游 max_disk_mib）
                var (withinDisk, overDisk) = PlanDiskPrune(keep, policy.MaxDiskBytes, EntryBytes);
                keep = withinDisk;
                drop.AddRange(overDisk);

                SweepOrphans(keep);

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
            AtomicFile.WriteJson(CatalogPath, new CaptureCatalogFile { Items = items }, Opt);
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
            DeleteFile(DisplayPath(entry.Id));

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

    /// <summary>按策略裁剪：「永久保留」= 一条都不丢，否则按条数 + 天数。</summary>
    public static (List<CaptureEntry> Keep, List<CaptureEntry> Drop) PlanPrune(
        IEnumerable<CaptureEntry> items, DateTimeOffset now, CaptureHistoryPolicy policy)
    {
        if (policy.KeepPermanently)
            return (items.OrderByDescending(e => e.CreatedAt, StringComparer.Ordinal).ToList(),
                new List<CaptureEntry>());

        return PlanPrune(items, now, policy.MaxEntries, policy.RetentionDays);
    }

    /// <summary>
    /// 磁盘上限（照上游 <c>capture_history/max_disk_mib</c>）：从最旧的开始丢，直到总量不超上限。
    /// </summary>
    /// <param name="sizeOf">一条记录占多少字节（调用方给，纯函数好断言）。</param>
    public static (List<CaptureEntry> Keep, List<CaptureEntry> Drop) PlanDiskPrune(
        IEnumerable<CaptureEntry> items, long maxBytes, Func<CaptureEntry, long> sizeOf) =>
        HistoryPrune.ByDisk(items, maxBytes, sizeOf, e => e.CreatedAt);

    /// <summary>一条记录占的字节（成品图 + 缩略图 + 整屏底图 + 录屏文件）。</summary>
    public static long EntryBytes(CaptureEntry entry) =>
        HistoryPrune.FileBytes(AssetPath(entry.Id)) + HistoryPrune.FileBytes(ThumbPath(entry.Id))
        + HistoryPrune.FileBytes(DisplayPath(entry.Id)) + HistoryPrune.FileBytes(FilePath(entry));

    /// <summary>
    /// 扫掉没有条目的孤儿文件（关掉采集历史后留下的图 / 缩略图）：只碰 <c>assets/</c> 与
    /// <c>thumbnails/</c>，录屏成品不动；文件写入不到 1 小时的不碰（可能刚写还没入库）。
    /// </summary>
    private static void SweepOrphans(IReadOnlyList<CaptureEntry> keep)
    {
        var ids = new HashSet<string>(keep.Select(e => e.Id), StringComparer.Ordinal);
        DateTime cutoff = DateTime.Now.AddHours(-1);

        foreach (string dir in new[] { AssetsDir, ThumbsDir, DisplaysDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.png"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (ids.Contains(id)) continue;

                try
                {
                    if (File.GetLastWriteTime(file) > cutoff) continue;
                }
                catch
                {
                    continue;
                }

                DeleteFile(file);
            }
        }
    }

    /// <summary>保留 / 丢弃怎么分：先按时间倒序，超龄或超量的进 discard。</summary>
    public static (List<CaptureEntry> Keep, List<CaptureEntry> Drop) PlanPrune(
        IEnumerable<CaptureEntry> items, DateTimeOffset now, int maxCount, int maxAgeDays) =>
        HistoryPrune.ByCountAndAge(items, now, maxCount, maxAgeDays, e => e.CreatedAt);

    /// <summary>
    /// 这块选区压到了几块屏（照上游 <c>displays</c> 的数量）：按交集判定，一块都碰不上也算 1。
    /// </summary>
    /// <remarks>
    /// 纯函数：矩形形状用元组传，自检可以直接喂几块「假屏」，不必去枚举真显示器。
    /// </remarks>
    public static int CountDisplays(int x, int y, int width, int height,
        IEnumerable<(int Left, int Top, int Width, int Height)> screens)
    {
        if (width <= 0 || height <= 0) return 1;

        int count = screens.Count(s =>
            x < s.Left + s.Width && s.Left < x + width &&
            y < s.Top + s.Height && s.Top < y + height);

        return count > 0 ? count : 1;
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
