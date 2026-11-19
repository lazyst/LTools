using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.Units.Files;

namespace LTools.Features.CapturePro;

/// <summary>文档类贴图文件的格式（照上游 <c>PinDocumentFormat</c>）。</summary>
internal enum PinDocumentFormat
{
    /// <summary>纯文本（<c>txt</c> / <c>css</c>）。</summary>
    PlainText,

    /// <summary>HTML（<c>html</c> / <c>htm</c>）。本地没有 HTML 排版引擎，按纯文本贴出（平台差异）。</summary>
    Html,

    /// <summary>Markdown（<c>md</c> / <c>markdown</c>）。本地不渲染 Markdown，按纯文本贴出（平台差异）。</summary>
    Markdown,
}

/// <summary>一个解码好的贴图文件：图片，或一段文本（文档）。</summary>
internal sealed class PinFileItem
{
    public PinFileItem(string path, BitmapSource? image, string text)
    {
        Path = path;
        Image = image;
        Text = text;
    }

    public string Path { get; }

    public BitmapSource? Image { get; }

    public string Text { get; }

    public bool IsText => Image == null;
}

/// <summary>
/// 「把文件贴到屏幕上」：图片文件贴图，文本 / HTML / Markdown / CSS 贴成文字卡片。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>src/app/supportedpinfiles.h</c> 的扩展名表与
/// <c>screenshotfilepinbatch.cpp</c> 的顺序贴出（先到先贴、重复的跳过、单个失败不影响其它），
/// 入口有三个：剪贴板里的文件列表、「贴选中的文件」内置功能、贴图上的拖放替换。
/// </para>
/// <para>
/// 解码放后台线程（大图解码几百毫秒，不能压在 UI 线程上——低级鼠标钩子与 UI 同线程），
/// 贴出仍按原顺序投递回 UI 线程（<see cref="Ui.Post"/> 是先入先出的 Dispatcher 队列）。
/// </para>
/// <para>
/// 平台差异：<c>webp</c> / <c>jxl</c> / <c>avif</c> 认扩展名，但能否解码取决于系统装了哪个
/// WIC 编解码器；HTML / Markdown 不做富文本排版，贴成等宽原文（见 <c>docs/CAPTURE_PARITY.md</c> §5）。
/// </para>
/// </remarks>
internal static class PinFiles
{
    /// <summary>图片扩展名（上游 <c>kImageFileFormats</c>）。</summary>
    public static readonly string[] ImageExtensions =
    {
        "gif", "apng", "png", "jpg", "jpeg", "webp", "jxl", "avif", "bmp", "tif", "tiff",
    };

    /// <summary>文档扩展名（上游 <c>kPinDocumentFileFormats</c>）。</summary>
    public static readonly (string Suffix, PinDocumentFormat Format)[] DocumentExtensions =
    {
        ("txt", PinDocumentFormat.PlainText),
        ("css", PinDocumentFormat.PlainText),
        ("html", PinDocumentFormat.Html),
        ("htm", PinDocumentFormat.Html),
        ("md", PinDocumentFormat.Markdown),
        ("markdown", PinDocumentFormat.Markdown),
    };

    /// <summary>文本文件上限（上游 <c>snapshotLocalFiles</c> 对文本用 1 MB）。</summary>
    public const long MaximumTextBytes = 1024 * 1024;

    /// <summary>图片文件上限（上游 <c>maximumImageImportBytes</c>；超过它解码必炸，直接拒）。</summary>
    public const long MaximumImageBytes = 256L * 1024 * 1024;

    /// <summary>全部支持的扩展名（小写、无点）。</summary>
    public static IReadOnlyList<string> SupportedExtensions =>
        ImageExtensions.Concat(DocumentExtensions.Select(entry => entry.Suffix)).ToArray();

    /// <summary>文件选择框的筛选串（照上游「Supported files (*.png *.jpg …)」）。</summary>
    public static string DialogFilter =>
        "支持的文件|" + string.Join(";", SupportedExtensions.Select(ext => "*." + ext)) +
        "|图片|" + string.Join(";", ImageExtensions.Select(ext => "*." + ext)) +
        "|文本|" + string.Join(";", DocumentExtensions.Select(entry => "*." + entry.Suffix)) +
        "|所有文件|*.*";

    /// <summary>这个扩展名属于哪种文档格式；不是文档返回 <c>null</c>。</summary>
    public static PinDocumentFormat? DocumentFormatOf(string path)
    {
        string suffix = SuffixOf(path);
        foreach (var (key, format) in DocumentExtensions)
            if (suffix == key) return format;
        return null;
    }

    public static bool IsImage(string path)
    {
        string suffix = SuffixOf(path);
        return ImageExtensions.Any(ext => suffix == ext);
    }

    public static bool IsDocument(string path) => DocumentFormatOf(path) != null;

    public static bool IsSupported(string path) => IsImage(path) || IsDocument(path);

    private static string SuffixOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string suffix = Path.GetExtension(path);
        return suffix.Length > 1 ? suffix[1..].ToLowerInvariant() : "";
    }

    /// <summary>
    /// 去重身份（照上游 <c>sourceIdentity</c> 的「同一个文件」口径）：
    /// 规范路径 + 字节数 + 最后写入时间。同一份文件改了就有新身份，不会被当成重复。
    /// </summary>
    public static string Identity(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            return info.Exists
                ? $"{full.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
                : full.ToLowerInvariant();
        }
        catch
        {
            return (path ?? "").ToLowerInvariant();
        }
    }

    /// <summary>
    /// 过滤出真正能贴的路径：支持的扩展名、是存在的文件、大小合规、同一文件只留第一次。
    /// </summary>
    /// <param name="rejected">不支持的扩展名（调用方提示用）。</param>
    public static List<string> Filter(IEnumerable<string> paths, out List<string> rejected)
    {
        var unique = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        rejected = new List<string>();

        foreach (string raw in paths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!IsSupported(raw))
            {
                rejected.Add(raw);
                continue;
            }

            if (!File.Exists(raw)) continue;

            long max = IsImage(raw) ? MaximumImageBytes : MaximumTextBytes;
            long size;
            try { size = new FileInfo(raw).Length; }
            catch { continue; }
            if (size <= 0 || size > max) continue;

            if (!seen.Add(Identity(raw))) continue;      // 同一份文件贴两次没有意义
            unique.Add(raw);
        }

        return unique;
    }

    /// <summary>
    /// 解码一个文件（可放后台线程）。读不出来返回 <c>null</c>（调用方按「这个文件失败」处理，
    /// 不打断整批）。
    /// </summary>
    public static PinFileItem? Decode(string path)
    {
        try
        {
            if (!File.Exists(path) || !IsSupported(path)) return null;

            if (IsImage(path))
            {
                var image = CaptureCatalog.LoadImage(path);
                return image == null ? null : new PinFileItem(path, image, "");
            }

            if (new FileInfo(path).Length > MaximumTextBytes) return null;
            string text = FileText.Read(path, "auto");
            return string.IsNullOrWhiteSpace(text) ? null : new PinFileItem(path, null, text);
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinFiles.Decode", $"读取 {path} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 依次把一批文件贴到屏幕上（照上游 <c>PinFilesToScreen</c>）：先解码、后按原顺序贴出，
    /// 单个失败只记一条，最后一条汇总提示。
    /// </summary>
    public static void PinPaths(IReadOnlyList<string> paths, string source = PinStore.SourceFiles,
        ClipboardMeta? meta = null)
    {
        var unique = Filter(paths, out var rejected);
        if (unique.Count == 0)
        {
            string why = rejected.Count > 0
                ? $"（{Path.GetFileName(rejected[0])} 不是支持的图片 / 文本格式）"
                : "";
            ToastService.Show("没有可以贴的文件" + why, "贴图", 2800, ToastKind.warning, key: "pin-files");
            return;
        }

        Task.Run(() =>
        {
            int ok = 0;
            var failed = rejected.Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name)).Select(name => name!).ToList();

            foreach (string path in unique)
            {
                var item = Decode(path);
                if (item == null)
                {
                    failed.Add(Path.GetFileName(path)!);
                    continue;
                }

                // 剪贴板元数据按「这张图 + 这个文件」各自校验：图像素尺寸与文件签名（大小 / 修改时间）
                // 都要对得上，才按原位置 + 原外观贴回（照上游 filePlacement / fileAppearance 的 matches）
                ClipboardAppearance? appearance = null;
                ClipboardPlacement? placement = null;
                if (item.Image is { } image && meta != null)
                {
                    appearance = meta.AppearanceFor(image.PixelWidth, image.PixelHeight, path);
                    placement = meta.PlacementFor(image.PixelWidth, image.PixelHeight, path);
                }

                ok++;
                Ui.Post(() => Present(item, source, appearance, placement));
            }

            int count = ok;
            var names = failed.Take(3).ToArray();
            Ui.Post(() =>
            {
                if (count == 0)
                {
                    ToastService.Show("这些文件都读不出来" + Suffix(names), "贴图", 0, ToastKind.error,
                        key: "pin-files");
                }
                else if (names.Length == 0)
                {
                    ToastService.Show($"已贴出 {count} 张", "贴图", 2400, ToastKind.success, key: "pin-files");
                }
                else
                {
                    ToastService.Show($"已贴出 {count} 张，{names.Length} 个读不出来{Suffix(names)}",
                        "贴图", 4000, ToastKind.warning, key: "pin-files");
                }
            });
        });
    }

    private static string Suffix(IReadOnlyList<string> names) =>
        names.Count == 0 ? "" : "：" + string.Join("、", names);

    /// <summary>把一个解码好的文件贴出来（必须在 UI 线程调）。</summary>
    public static void Present(PinFileItem item, string source = PinStore.SourceFiles) =>
        Present(item, source, null, null);

    /// <summary>
    /// 同上，并带上剪贴板里记下的外观 / 落位（只对图片有效；文字直接走卡片）。
    /// </summary>
    /// <remarks>
    /// 照上游 <c>screenshotClipboardPinGeometry</c>：落位能解析（桌面没变 / 换屏后能算出位置）
    /// 就按原窗口矩形贴回，外观（圆角 / 阴影 / 棋盘格）一起还原；解析不出来才退回「居中贴」。
    /// </remarks>
    public static void Present(PinFileItem item, string source,
        ClipboardAppearance? appearance, ClipboardPlacement? placement)
    {
        double scale = PinClipboard.ScreenScale();
        var workArea = PinClipboard.WorkAreaDip(scale);

        if (item.Image is not { } image)
        {
            CapturePinManager.PinText(item.Text, scale, workArea, source);
            return;
        }

        var look = appearance?.Look ?? PinAppearance.None;
        if (placement != null && CaptureClipboard.ResolveRect(placement) is Int32Rect at)
        {
            CapturePinManager.PinRestored(image, at, look,
                appearance?.Checkerboard ?? look.HasShape, source);
            return;
        }

        CapturePinManager.PinClipboardImage(image, scale, workArea, source, look);
    }
}
