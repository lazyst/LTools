using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>一条自定义剪贴板载荷（格式名 + 原始字节）。</summary>
internal readonly record struct ClipboardPayload(string Name, byte[] Data);

/// <summary>写进剪贴板的一条落位：窗口矩形 + 当时烘进去的外观（见 <see cref="CaptureClipboard"/>）。</summary>
/// <param name="Checkerboard">
/// 贴图窗口当时铺没铺透明棋盘格（照上游 <c>checkerboardEnabled</c>）。本地「有形状就有棋盘格」，
/// 显式传只是为了把「用户手动开过」的情形也带上。
/// </param>
internal readonly record struct ClipboardCapture(Int32Rect WindowRect, PinAppearance Look,
    bool Checkerboard = false);

/// <summary>一个文件的时间戳签名（路径 + 字节数 + 修改时间），用来判断剪贴板里的文件还是不是当初那一份。</summary>
/// <remarks>
/// 照上游 <c>ScreenshotClipboardAppearance::matchesFile</c> / <c>ScreenshotClipboardPlacement::matchesFile</c>：
/// 文件名相同但内容被换掉时不能按原位置 / 原外观贴回，否则贴出来的是另一张图。
/// 时间取 <see cref="DateTimeOffset.ToUnixTimeMilliseconds"/>（UTC 毫秒），与上游 <c>toMSecsSinceEpoch</c> 同口径。
/// </remarks>
internal readonly record struct ClipboardFile(string Path, long Size, long ModifiedMs)
{
    /// <summary>取某个文件当前的签名；文件不存在返回 <c>null</c>（那就别写进元数据）。</summary>
    public static ClipboardFile? Of(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return new ClipboardFile(info.FullName, info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>剪贴板里这个路径现在还是不是当初那一份（路径比对大小写不敏感）。</summary>
    public bool Matches(string path)
    {
        var now = Of(path);
        return now is ClipboardFile current
            && SamePath(current.Path, Path)
            && current.Size == Size
            && current.ModifiedMs == ModifiedMs;
    }

    /// <summary>两个路径是不是同一个文件（绝对化后忽略大小写，Windows 口径）。</summary>
    public static bool SamePath(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

        try
        {
            string left = System.IO.Path.GetFullPath(a);
            string right = System.IO.Path.GetFullPath(b);
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>一条屏幕几何（物理像素 + 缩放），落进剪贴板元数据用来判「桌面变没变」。</summary>
/// <remarks>
/// 上游记的是显示器 <c>name</c> / <c>serial</c>；本地拿不到稳定序列号，改按
/// 「物理矩形 + 工作区 + 缩放 + 主屏标记」认同一块屏——换分辨率 / 拔掉一块屏 / 改缩放都会让它对不上，
/// 那时就不再按原位置硬摆（与上游「unchanged 才用原窗口矩形」同口径）。
/// </remarks>
internal sealed record ClipboardDisplay(Int32Rect Desktop, Int32Rect Usable, double Scale, bool Primary)
{
    public static ClipboardDisplay Of(ScreenGeometry screen) => new(
        new Int32Rect(screen.Left, screen.Top, Math.Max(1, screen.Width), Math.Max(1, screen.Height)),
        new Int32Rect(screen.WorkLeft, screen.WorkTop, Math.Max(1, screen.WorkWidth), Math.Max(1, screen.WorkHeight)),
        screen.Scale > 0 ? screen.Scale : 1,
        screen.Primary);

    /// <summary>当前桌面（显示器枚举不到时至少一块）。</summary>
    public static List<ClipboardDisplay> Current()
    {
        var list = new List<ClipboardDisplay>();
        foreach (var screen in Screens.All()) list.Add(Of(screen));
        if (list.Count == 0)
        {
            var bounds = ScreenShot.Bounds();
            list.Add(new ClipboardDisplay(bounds, bounds, ScreenShot.ScaleOf(bounds), true));
        }
        return list;
    }
}

/// <summary>截图复制时附带的外观快照（照上游 <c>ScreenshotClipboardAppearance</c>）。</summary>
internal sealed record ClipboardAppearance(
    int RasterWidth, int RasterHeight,
    int RadiusPx, int EffectPx, string Decoration,
    bool Checkerboard, ClipboardFile? File)
{
    /// <summary>还原成贴图窗口用的外观（长度是成品图像素，与 <see cref="PinAppearance"/> 同一套）。</summary>
    public PinAppearance Look => new PinAppearance
    {
        RadiusPx = RadiusPx,
        EffectPx = EffectPx,
        Decoration = PinAppearance.NormalizeDecoration(Decoration),
    }.Clamp(RasterWidth, RasterHeight);

    /// <summary>这张图对不对得上（贴图里的图与复制时是同一个尺寸）。</summary>
    public bool MatchesRaster(int width, int height) =>
        width == RasterWidth && height == RasterHeight;
}

/// <summary>截图复制时附带的落位快照（照上游 <c>ScreenshotClipboardPlacement</c>）。</summary>
internal sealed record ClipboardPlacement(
    Int32Rect WindowRect, int RasterWidth, int RasterHeight,
    IReadOnlyList<ClipboardDisplay> Displays, int AnchorIndex, ClipboardFile? File)
{
    /// <summary>这张图对不对得上（长宽都要一致，否则不能按原位置摆）。</summary>
    public bool MatchesRaster(int width, int height) =>
        width == RasterWidth && height == RasterHeight;
}

/// <summary>
/// 剪贴板里的截图外观 / 落位元数据：复制截图时一并写两份自定义格式，
/// 「贴剪贴板」时读回来按**原位置 + 原外观**贴出（照上游 `screenshotclipboardappearance` /
/// `screenshotclipboardplacement`）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：贴图窗口默认贴在光标所在屏中间、不带圆角 / 阴影。上游复制截图时把
/// 「窗口矩形 + 每块屏的几何 + 图像素尺寸 + 外观」一起写进剪贴板，于是「复制截图 → 贴剪贴板」
/// 能贴回**原来那块位置**，圆角 / 阴影 / 棋盘格也跟着回来。只靠图片本身做不到这件事。
/// </para>
/// <para>
/// 帧格式与上游一致：<c>magic(u32) + version(u32=1) + length(u32) + UTF-8 JSON</c>，
/// 小端；外观 64 KB、落位 16 KB 封顶（超出就不写，读回来按「没有元数据」处理）。
/// 注册格式名本地用 <c>LToolsScreenshotAppearance</c> / <c>LToolsScreenshotPlacement</c>。
/// </para>
/// <para>
/// **文件签名是陈旧保护**：复制时若还写了图片文件（「复制图片文件到剪贴板」/ 自动保存），
/// 元数据里记下路径 + 大小 + 修改时间；后来那个文件被改过就当成没匹配上，不按原位置贴回。
/// </para>
/// </remarks>
internal static class CaptureClipboard
{
    /// <summary>外观元数据的注册格式名。</summary>
    public const string AppearanceFormat = "LToolsScreenshotAppearance";

    /// <summary>落位元数据的注册格式名。</summary>
    public const string PlacementFormat = "LToolsScreenshotPlacement";

    /// <summary>外观元数据字节上限（上游 <c>kScreenshotClipboardAppearanceMaximumBytes</c>）。</summary>
    public const int MaxAppearanceBytes = 64 * 1024;

    /// <summary>落位元数据字节上限（上游 <c>kScreenshotClipboardPlacementMaximumBytes</c>）。</summary>
    public const int MaxPlacementBytes = 16 * 1024;

    private const uint AppearanceMagic = 0x4C544341;   // "ACTA" 小端写法 = 0x4C544341
    private const uint PlacementMagic = 0x4C544350;    // "ACTP"
    private const uint Version = 1;
    private const int HeaderBytes = 12;
    private const long MaxPixels = 64L * 1000 * 1000;

    // ============ 写 ============

    /// <summary>
    /// 为一次复制生成两份元数据载荷。没有落位（<paramref name="capture"/> 为空）时返回空表——
    /// 只复制图片、不改剪贴板格式的调用方不用管这件事。
    /// </summary>
    public static IReadOnlyList<ClipboardPayload> Payloads(BitmapSource image,
        ClipboardCapture? capture, string? file)
    {
        if (image == null || capture is not ClipboardCapture request) return Array.Empty<ClipboardPayload>();

        var look = request.Look;
        var signature = ClipboardFile.Of(file);
        var list = new List<ClipboardPayload>(2);

        var appearance = new ClipboardAppearance(image.PixelWidth, image.PixelHeight,
            look.RadiusPx, look.EffectPx, PinAppearance.NormalizeDecoration(look.Decoration),
            request.Checkerboard || look.HasShape, signature);
        if (EncodeAppearance(appearance) is byte[] appearanceBytes)
            list.Add(new ClipboardPayload(AppearanceFormat, appearanceBytes));

        if (TryDescribe(request.WindowRect, image.PixelWidth, image.PixelHeight, signature,
                out var placement) && EncodePlacement(placement!) is byte[] placementBytes)
        {
            list.Add(new ClipboardPayload(PlacementFormat, placementBytes));
        }

        return list;
    }

    /// <summary>一次复制该带上的落位：窗口矩形 + 当前桌面 + 锚点屏。</summary>
    /// <remarks>窗口矩形无效（宽高 ≤ 0）时返回 false——那就不写落位，贴出去居中。</remarks>
    public static bool TryDescribe(Int32Rect window, int rasterWidth, int rasterHeight,
        ClipboardFile? file, out ClipboardPlacement? placement)
    {
        placement = null;
        if (!ValidRect(window) || !ValidSize(rasterWidth, rasterHeight)) return false;

        var displays = ClipboardDisplay.Current();
        if (displays.Count is 0 or > 64) return false;

        int anchor = AnchorOf(window, displays);
        if (anchor < 0) return false;

        placement = new ClipboardPlacement(window, rasterWidth, rasterHeight, displays, anchor, file);
        return true;
    }

    /// <summary>窗口中心落在哪块屏（不在任何一块里就取交集最大的那块，最后退回第一块）。</summary>
    public static int AnchorOf(Int32Rect window, IReadOnlyList<ClipboardDisplay> displays)
    {
        if (displays.Count == 0) return -1;

        int cx = window.X + window.Width / 2;
        int cy = window.Y + window.Height / 2;
        int best = -1, bestArea = 0;

        for (int i = 0; i < displays.Count; i++)
        {
            var box = displays[i].Desktop;
            if (cx >= box.X && cx < box.X + box.Width && cy >= box.Y && cy < box.Y + box.Height)
                return i;

            int left = Math.Max(window.X, box.X);
            int top = Math.Max(window.Y, box.Y);
            int right = Math.Min(window.X + window.Width, box.X + box.Width);
            int bottom = Math.Min(window.Y + window.Height, box.Y + box.Height);
            if (right <= left || bottom <= top) continue;

            int area = (right - left) * (bottom - top);
            if (area > bestArea)
            {
                bestArea = area;
                best = i;
            }
        }

        return best >= 0 ? best : 0;
    }

    // ============ 读 ============

    /// <summary>读剪贴板里的两份原始载荷（不做尺寸校验；按需 <see cref="ClipboardMeta"/> 解）。</summary>
    public static ClipboardMeta Read()
    {
        byte[]? appearance = NativeClipboard.TryGetCustom(AppearanceFormat, MaxAppearanceBytes,
            out byte[] a) ? a : null;
        byte[]? placement = NativeClipboard.TryGetCustom(PlacementFormat, MaxPlacementBytes,
            out byte[] p) ? p : null;
        return new ClipboardMeta(appearance, placement);
    }

    /// <summary>按这张图解码两份元数据；对不上尺寸 / 文件签名不匹配时各自返回 <c>null</c>。</summary>
    public static (ClipboardAppearance? Appearance, ClipboardPlacement? Placement) Describe(
        ClipboardMeta meta, int rasterWidth, int rasterHeight, string? file)
    {
        var appearance = meta.AppearanceFor(rasterWidth, rasterHeight, file);
        var placement = meta.PlacementFor(rasterWidth, rasterHeight, file);
        return (appearance, placement);
    }

    // ============ 编码 / 解码 ============

    /// <summary>编码一份外观元数据（无效或超限返回 <c>null</c>）。</summary>
    public static byte[]? EncodeAppearance(ClipboardAppearance value)
    {
        if (!ValidSize(value.RasterWidth, value.RasterHeight)
            || value.RadiusPx < 0 || value.EffectPx < 0
            || PinAppearance.NormalizeDecoration(value.Decoration) != value.Decoration)
            return null;

        return Frame(AppearanceMagic, MaxAppearanceBytes, writer =>
        {
            writer.WriteStartObject();
            WriteSize(writer, "raster", value.RasterWidth, value.RasterHeight);
            writer.WriteBoolean("checkerboard", value.Checkerboard);

            writer.WriteStartObject("border");
            writer.WriteNumber("radius", value.RadiusPx);
            writer.WriteNumber("effect", value.EffectPx);
            writer.WriteString("decoration", value.Decoration);
            writer.WriteEndObject();

            if (value.File is ClipboardFile file) WriteFile(writer, file);
            writer.WriteEndObject();
        });
    }

    /// <summary>解码一份外观元数据（magic / 版本 / 长度 / 尺寸都要对）。</summary>
    public static ClipboardAppearance? DecodeAppearance(byte[]? bytes)
    {
        var doc = Open(bytes, AppearanceMagic, MaxAppearanceBytes);
        if (doc == null) return null;

        using (doc)
        {
            var root = doc.RootElement;
            if (!TrySize(root, "raster", out int width, out int height)) return null;
            if (!root.TryGetProperty("checkerboard", out var checker) || !IsBool(checker)) return null;

            int radius = 0, effect = 0;
            string decoration = PinAppearance.DecorationNone;

            if (root.TryGetProperty("border", out var border))
            {
                if (border.ValueKind != JsonValueKind.Object) return null;
                if (!border.TryGetProperty("radius", out var r) || !TryInt(r, out radius) || radius < 0) return null;
                if (!border.TryGetProperty("effect", out var e) || !TryInt(e, out effect) || effect < 0) return null;
                if (border.TryGetProperty("decoration", out var d) && d.ValueKind == JsonValueKind.String)
                    decoration = PinAppearance.NormalizeDecoration(d.GetString());
            }

            var file = ReadFile(root);
            return new ClipboardAppearance(width, height, radius, effect, decoration,
                checker.GetBoolean(), file);
        }
    }

    /// <summary>编码一份落位元数据（无效或超限返回 <c>null</c>）。</summary>
    public static byte[]? EncodePlacement(ClipboardPlacement value)
    {
        if (!ValidRect(value.WindowRect)
            || !ValidSize(value.RasterWidth, value.RasterHeight)
            || value.Displays.Count is 0 or > 64
            || value.AnchorIndex < 0 || value.AnchorIndex >= value.Displays.Count)
            return null;

        return Frame(PlacementMagic, MaxPlacementBytes, writer =>
        {
            writer.WriteStartObject();
            WriteRect(writer, "window", value.WindowRect);
            WriteSize(writer, "raster", value.RasterWidth, value.RasterHeight);
            writer.WriteNumber("anchor", value.AnchorIndex);

            writer.WriteStartArray("displays");
            foreach (var display in value.Displays)
            {
                writer.WriteStartObject();
                WriteRect(writer, "desktop", display.Desktop);
                WriteRect(writer, "usable", display.Usable);
                writer.WriteNumber("scale", display.Scale);
                writer.WriteBoolean("primary", display.Primary);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (value.File is ClipboardFile file) WriteFile(writer, file);
            writer.WriteEndObject();
        });
    }

    /// <summary>解码一份落位元数据（magic / 版本 / 长度 / 几何都要对）。</summary>
    public static ClipboardPlacement? DecodePlacement(byte[]? bytes)
    {
        var doc = Open(bytes, PlacementMagic, MaxPlacementBytes);
        if (doc == null) return null;

        using (doc)
        {
            var root = doc.RootElement;
            if (!TryRect(root, "window", out var window) || !ValidRect(window)) return null;
            if (!TrySize(root, "raster", out int width, out int height)) return null;
            if (!root.TryGetProperty("anchor", out var anchor) || !TryInt(anchor, out int index)) return null;
            if (!root.TryGetProperty("displays", out var displays) || displays.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<ClipboardDisplay>();
            foreach (var item in displays.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                if (!TryRect(item, "desktop", out var desktop) || !ValidRect(desktop)) return null;
                if (!TryRect(item, "usable", out var usable) || !ValidRect(usable)) return null;
                if (!item.TryGetProperty("scale", out var scale) || scale.ValueKind != JsonValueKind.Number
                    || !scale.TryGetDouble(out double value) || !double.IsFinite(value) || value < 1 || value > 16)
                    return null;

                bool primary = item.TryGetProperty("primary", out var flag)
                    && flag.ValueKind == JsonValueKind.True;
                list.Add(new ClipboardDisplay(desktop, usable, value, primary));
            }

            if (list.Count is 0 or > 64 || index < 0 || index >= list.Count) return null;

            return new ClipboardPlacement(window, width, height, list, index, ReadFile(root));
        }
    }

    // ============ 落位解析 ============

    /// <summary>
    /// 按当前桌面算出贴图窗口该落在哪儿（照上游 <c>resolveScreenshotClipboardPlacement</c>）。
    /// </summary>
    /// <remarks>
    /// 桌面没变（屏数、几何、缩放都一样）就原样返回复制时的窗口矩形；变了则换到对应 / 最近的显示器，
    /// 按缩放比改尺寸，必要时缩进工作区。返回 <c>null</c> = 没有可用落点（贴图退回居中的常规路径）。
    /// </remarks>
    public static Int32Rect? ResolveRect(ClipboardPlacement placement,
        IReadOnlyList<ClipboardDisplay> current, bool autoResize)
    {
        if (placement == null || current == null || current.Count == 0) return null;
        if (!ValidRect(placement.WindowRect)) return null;
        if (placement.AnchorIndex < 0 || placement.AnchorIndex >= placement.Displays.Count) return null;

        var saved = placement.Displays[placement.AnchorIndex];

        // 桌面完全没变：就是复制时的那个窗口矩形（上游 unchanged 分支）
        if (SameDesktop(placement.Displays, current)) return placement.WindowRect;

        int target = FindTarget(saved, placement.WindowRect, current);
        if (target < 0) return null;

        var screen = current[target];
        if (!ValidRect(screen.Desktop) || screen.Scale < 1 || screen.Scale > 16) return null;

        double ratio = screen.Scale / (saved.Scale > 0 ? saved.Scale : 1);
        int width = Math.Max(1, (int)Math.Round(placement.WindowRect.Width * ratio));
        int height = Math.Max(1, (int)Math.Round(placement.WindowRect.Height * ratio));

        if (autoResize)
        {
            double fit = Math.Min(1.0, Math.Min(
                screen.Usable.Width / (double)width,
                screen.Usable.Height / (double)height));
            if (fit < 1)
            {
                width = Math.Max(1, (int)Math.Round(width * fit));
                height = Math.Max(1, (int)Math.Round(height * fit));
            }
        }

        // 同一块屏：保持原来的相对偏移；换了屏（几何变了 / 拔了又插）：居中摆到目标屏
        int x, y;
        if (screen.Desktop == saved.Desktop)
        {
            x = screen.Desktop.X + (int)Math.Round((placement.WindowRect.X - saved.Desktop.X) * ratio);
            y = screen.Desktop.Y + (int)Math.Round((placement.WindowRect.Y - saved.Desktop.Y) * ratio);
        }
        else
        {
            x = screen.Desktop.X + (screen.Desktop.Width - width) / 2;
            y = screen.Desktop.Y + (screen.Desktop.Height - height) / 2;
        }

        x = Clamp(x, screen.Desktop.X, screen.Desktop.X + Math.Max(0, screen.Desktop.Width - width));
        y = Clamp(y, screen.Desktop.Y, screen.Desktop.Y + Math.Max(0, screen.Desktop.Height - height));

        var result = new Int32Rect(x, y, width, height);
        return HistoryPinPlacement.MeetsDesktop(result, Desktops(current)) ? result : null;
    }

    /// <summary>按当前桌面解析（<see cref="ClipboardDisplay.Current"/> 的薄封装）。</summary>
    public static Int32Rect? ResolveRect(ClipboardPlacement placement, bool autoResize = true) =>
        ResolveRect(placement, ClipboardDisplay.Current(), autoResize);

    private static int FindTarget(ClipboardDisplay saved, Int32Rect window,
        IReadOnlyList<ClipboardDisplay> current)
    {
        // 先认「同一块屏」：物理矩形完全一样（分辨率 / 位置都没变）
        for (int i = 0; i < current.Count; i++)
            if (current[i].Desktop == saved.Desktop) return i;

        // 再按「离复制时那块窗口最近」挑一块
        int best = -1;
        long nearest = long.MaxValue;
        for (int i = 0; i < current.Count; i++)
        {
            long dx = Math.Max(0, Math.Max(current[i].Desktop.X - (window.X + window.Width),
                window.X - (current[i].Desktop.X + current[i].Desktop.Width)));
            long dy = Math.Max(0, Math.Max(current[i].Desktop.Y - (window.Y + window.Height),
                window.Y - (current[i].Desktop.Y + current[i].Desktop.Height)));
            long distance = dx * dx + dy * dy;
            if (distance < nearest)
            {
                nearest = distance;
                best = i;
            }
        }

        return best;
    }

    private static bool SameDesktop(IReadOnlyList<ClipboardDisplay> saved,
        IReadOnlyList<ClipboardDisplay> current)
    {
        if (saved.Count != current.Count) return false;

        foreach (var a in saved)
        {
            bool found = false;
            foreach (var b in current)
            {
                if (a.Desktop != b.Desktop || a.Usable != b.Usable
                    || Math.Abs(a.Scale - b.Scale) > 1e-6) continue;
                found = true;
                break;
            }

            if (!found) return false;
        }

        return true;
    }

    private static List<Int32Rect> Desktops(IReadOnlyList<ClipboardDisplay> displays)
    {
        var list = new List<Int32Rect>(displays.Count);
        foreach (var display in displays) list.Add(display.Desktop);
        return list;
    }

    // ============ 基础 ============

    private static int Clamp(int value, int min, int max) =>
        max <= min ? min : Math.Min(Math.Max(value, min), max);

    private static bool ValidSize(int width, int height) =>
        width > 0 && height > 0 && (long)width * height <= MaxPixels;

    private static bool ValidRect(Int32Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return false;
        return (long)rect.X + rect.Width <= int.MaxValue && (long)rect.Y + rect.Height <= int.MaxValue;
    }

    private static bool IsBool(JsonElement value) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool TryInt(JsonElement value, out int number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        if (value.TryGetInt32(out number)) return true;
        if (!value.TryGetDouble(out double raw) || !double.IsFinite(raw) || Math.Truncate(raw) != raw) return false;
        if (raw < int.MinValue || raw > int.MaxValue) return false;
        number = (int)raw;
        return true;
    }

    private static void WriteSize(Utf8JsonWriter writer, string name, int width, int height)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(width);
        writer.WriteNumberValue(height);
        writer.WriteEndArray();
    }

    private static void WriteRect(Utf8JsonWriter writer, string name, Int32Rect rect)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(rect.X);
        writer.WriteNumberValue(rect.Y);
        writer.WriteNumberValue(rect.Width);
        writer.WriteNumberValue(rect.Height);
        writer.WriteEndArray();
    }

    private static void WriteFile(Utf8JsonWriter writer, ClipboardFile file)
    {
        writer.WriteStartObject("file");
        writer.WriteString("path", file.Path);
        // 大整数按字符串写：JSON 数字是 double，毫秒时间戳会丢精度（照上游做法）
        writer.WriteString("size", file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("modified", file.ModifiedMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static ClipboardFile? ReadFile(JsonElement root)
    {
        if (!root.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.Object) return null;
        if (!file.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) return null;

        string text = path.GetString() ?? "";
        if (text.Length is 0 or > 4096) return null;

        if (!file.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.String) return null;
        if (!file.TryGetProperty("modified", out var modified) || modified.ValueKind != JsonValueKind.String) return null;
        if (!long.TryParse(size.GetString(), out long bytes) || bytes <= 0) return null;
        if (!long.TryParse(modified.GetString(), out long stamp) || stamp < 0) return null;

        return new ClipboardFile(text, bytes, stamp);
    }

    private static bool TrySize(JsonElement root, string name, out int width, out int height)
    {
        width = height = 0;
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return false;
        if (value.GetArrayLength() != 2) return false;

        var a = value[0];
        var b = value[1];
        if (!TryInt(a, out width) || !TryInt(b, out height)) return false;
        return ValidSize(width, height);
    }

    private static bool TryRect(JsonElement root, string name, out Int32Rect rect)
    {
        rect = default;
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return false;
        if (value.GetArrayLength() != 4) return false;

        if (!TryInt(value[0], out int x) || !TryInt(value[1], out int y)
            || !TryInt(value[2], out int width) || !TryInt(value[3], out int height))
            return false;

        rect = new Int32Rect(x, y, width, height);
        return true;
    }

    /// <summary>写一帧 <c>magic + version + length + JSON</c>；超限返回 <c>null</c>。</summary>
    private static byte[]? Frame(uint magic, int maximum, Action<Utf8JsonWriter> body)
    {
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
                body(writer);

            byte[] json = stream.ToArray();
            if (json.Length + HeaderBytes > maximum) return null;

            var result = new byte[HeaderBytes + json.Length];
            BitConverter.TryWriteBytes(result.AsSpan(0, 4), magic);
            BitConverter.TryWriteBytes(result.AsSpan(4, 4), Version);
            BitConverter.TryWriteBytes(result.AsSpan(8, 4), (uint)json.Length);
            Buffer.BlockCopy(json, 0, result, HeaderBytes, json.Length);
            return result;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>校验帧头并解析出 JSON 文档（失败返回 <c>null</c>，调用方负责 Dispose）。</summary>
    private static JsonDocument? Open(byte[]? bytes, uint magic, int maximum)
    {
        if (bytes == null || bytes.Length < HeaderBytes || bytes.Length > maximum) return null;
        if (BitConverter.ToUInt32(bytes, 0) != magic) return null;
        if (BitConverter.ToUInt32(bytes, 4) != Version) return null;

        uint length = BitConverter.ToUInt32(bytes, 8);
        if (length > (uint)(bytes.Length - HeaderBytes)) return null;

        try
        {
            // 原生分配器可能把缓冲区补齐成更大的块：以帧头里的长度为准，别把填充字节当 JSON
            return JsonDocument.Parse(bytes.AsMemory(HeaderBytes, (int)length));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 从剪贴板读到的两份原始元数据：读一次，按每张图的实际像素尺寸各自解码。
/// </summary>
internal sealed class ClipboardMeta
{
    public ClipboardMeta(byte[]? appearance, byte[]? placement)
    {
        Appearance = appearance;
        Placement = placement;
    }

    /// <summary>原始外观载荷（没有就是 <c>null</c>）。</summary>
    public byte[]? Appearance { get; }

    /// <summary>原始落位载荷（没有就是 <c>null</c>）。</summary>
    public byte[]? Placement { get; }

    /// <summary>没有任何元数据（不用再解码）。</summary>
    public bool IsEmpty => Appearance == null && Placement == null;

    /// <summary>这张图的外观；尺寸对不上 / 文件签名不匹配返回 <c>null</c>。</summary>
    public ClipboardAppearance? AppearanceFor(int width, int height, string? file = null)
    {
        var value = CaptureClipboard.DecodeAppearance(Appearance);
        if (value == null || !value.MatchesRaster(width, height)) return null;
        if (value.File is ClipboardFile signature && (file == null || !signature.Matches(file))) return null;
        return value;
    }

    /// <summary>这张图的落位；尺寸对不上 / 文件签名不匹配返回 <c>null</c>。</summary>
    public ClipboardPlacement? PlacementFor(int width, int height, string? file = null)
    {
        var value = CaptureClipboard.DecodePlacement(Placement);
        if (value == null || !value.MatchesRaster(width, height)) return null;
        if (value.File is ClipboardFile signature && (file == null || !signature.Matches(file))) return null;
        return value;
    }
}
