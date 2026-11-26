using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>参数条上那三排快捷颜色属于哪一族（照上游 settings 的三个键）。</summary>
internal enum ColorPresetFamily
{
    /// <summary>描边 / 文字色 / 聚光（上游 <c>screenshot/stroke_color_presets</c>）。</summary>
    Stroke,

    /// <summary>填充 / 文字底色 / 箭头外描边（上游 <c>screenshot/fill_color_presets</c>）。</summary>
    Fill,

    /// <summary>荧光笔 / 矩形荧光（上游 <c>screenshot/highlight_color_presets</c>）。</summary>
    Highlight,
}

/// <summary>
/// 用户可编辑的颜色预设：三族各 5 个，存 <c>configs/settings.json</c> 的
/// <c>Screenshot</c> 段（照上游 <c>screenshot/stroke_color_presets</c> /
/// <c>fill_color_presets</c> / <c>highlight_color_presets</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游把颜色存成 <c>#RRGGBBAA</c>（8 位十六进制、大写），**恒定 5 个**——
/// <c>setColorPresetValues</c> 数不对就直接拒绝写盘。这里保持同一口径：
/// 读盘时数量不是 5 就整组回默认；某一格解析不出来时只把这一格回默认
/// （上游会让那一格变成无效色，画出来是个空块，没必要照着坏）。
/// </para>
/// <para>
/// 读盘只发生一次（参数条每换一次工具都要这 5 个色，不能每次读 JSON），
/// 写盘只写 settings 域。三族的默认值与上游 <c>configurationschema.cpp</c> 逐字一致。
/// </para>
/// </remarks>
internal static class ColorPresets
{
    /// <summary>每族固定的格数（上游 <c>setColorPresetValues</c> 只收 5 个）。</summary>
    public const int Count = 5;

    private static readonly string[] StrokeDefaults =
    {
        "#F5222DFF", "#52C41AFF", "#1677FFFF", "#FADB14FF", "#000000FF",
    };

    private static readonly string[] FillDefaults =
    {
        "#FFFFFF00", "#FFCCC7FF", "#D9F7BEFF", "#BAE0FFFF", "#FFF1B8FF",
    };

    private static readonly string[] HighlightDefaults =
    {
        "#FF7875FF", "#D3F261FF", "#69B1FFFF", "#FFF566FF", "#B37FEBFF",
    };

    private static readonly object Gate = new();

    private static Color[]? _stroke;
    private static Color[]? _fill;
    private static Color[]? _highlight;

    /// <summary>某一族的默认值（<c>#RRGGBBAA</c> 文本，顺序与上游一致）。</summary>
    public static IReadOnlyList<string> DefaultHex(ColorPresetFamily family) => family switch
    {
        ColorPresetFamily.Fill => FillDefaults,
        ColorPresetFamily.Highlight => HighlightDefaults,
        _ => StrokeDefaults,
    };

    /// <summary>某一族当前生效的 5 个颜色（**只读，不要改这个数组**）。</summary>
    public static IReadOnlyList<Color> Get(ColorPresetFamily family)
    {
        LoadAll();
        lock (Gate)
        {
            return family switch
            {
                ColorPresetFamily.Fill => _fill!,
                ColorPresetFamily.Highlight => _highlight!,
                _ => _stroke!,
            };
        }
    }

    /// <summary>某一族当前生效的 5 个颜色（<c>#RRGGBBAA</c> 文本，写盘 / 设置页用）。</summary>
    public static string[] GetHex(ColorPresetFamily family)
    {
        var colors = Get(family);
        var result = new string[colors.Count];
        for (int i = 0; i < result.Length; i++) result[i] = Format(colors[i]);
        return result;
    }

    /// <summary>
    /// 换掉某一族的 5 个颜色：先归一（数量必须正好 5，解析不出的格子回默认），
    /// 再写缓存与 <c>settings.json</c>。返回真正落地的 5 个颜色。
    /// </summary>
    public static string[] Set(ColorPresetFamily family, IReadOnlyList<string> colors)
    {
        var normalized = Normalize(colors, Defaults(family));
        lock (Gate) Store(family, normalized);

        var hex = new string[Count];
        for (int i = 0; i < Count; i++) hex[i] = Format(normalized[i]);

        Save(file =>
        {
            var shot = file.Screenshot ??= new ScreenshotFile();
            switch (family)
            {
                case ColorPresetFamily.Fill:
                    shot.FillColorPresets = new List<string>(hex);
                    break;
                case ColorPresetFamily.Highlight:
                    shot.HighlightColorPresets = new List<string>(hex);
                    break;
                default:
                    shot.StrokeColorPresets = new List<string>(hex);
                    break;
            }
        });

        return hex;
    }

    /// <summary>把某一族恢复成上游默认值（设置对话框的「恢复默认」）。</summary>
    public static string[] Reset(ColorPresetFamily family) => Set(family, DefaultHex(family));

    /// <summary>配置被外部改过（导入备份 / 冒烟切目录）时丢掉缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _stroke = null;
            _fill = null;
            _highlight = null;
        }
    }

    /// <summary>
    /// 参数条「颜色」那一排该用哪一族（照上游 <c>screenshottoolpalettestylecontrols.cpp</c>）：
    /// 荧光笔与矩形荧光用荧光预设，其余（含聚光，它复用 <c>textColors()</c>）都用描边预设。
    /// </summary>
    public static ColorPresetFamily ForTool(CaptureTool tool) =>
        tool is CaptureTool.Highlighter or CaptureTool.HighlightRect
            ? ColorPresetFamily.Highlight
            : ColorPresetFamily.Stroke;

    /// <summary>上游 <c>colorToRgbaString</c> 的同一形状：<c>#RRGGBBAA</c>，大写。</summary>
    public static string Format(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";

    /// <summary>
    /// 认 <c>#RRGGBBAA</c> 与 <c>#RRGGBB</c>（后者 alpha = FF）；其它写法一律不认。
    /// </summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string value = text.Trim();
        if (value.Length == 0 || value[0] != '#') return false;

        string hex = value[1..];
        if (hex.Length == 6) hex += "FF";
        else if (hex.Length != 8) return false;

        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgba)) return false;

        color = Color.FromArgb(
            (byte)(rgba & 0xFF),
            (byte)((rgba >> 24) & 0xFF),
            (byte)((rgba >> 16) & 0xFF),
            (byte)((rgba >> 8) & 0xFF));
        return true;
    }

    /// <summary>
    /// 归一：数量正好 5 就逐格解析（解析不出的格子用默认值补），否则整组回默认。
    /// </summary>
    internal static Color[] Normalize(IReadOnlyList<string>? stored, IReadOnlyList<Color> fallback)
    {
        if (stored == null || stored.Count != Count) return Copy(fallback);

        var result = new Color[Count];
        for (int i = 0; i < Count; i++)
            result[i] = TryParse(stored[i], out var color) ? color : fallback[i];
        return result;
    }

    /// <summary>
    /// 色块的画刷：不透明就给纯色；带透明度时铺一层棋盘格再叠颜色，
    /// 否则「填充色」的第一个默认值（<c>#FFFFFF00</c>）在参数条上是个看不见的空块。
    /// </summary>
    public static Brush Swatch(Color color)
    {
        if (color.A == 255) return new SolidColorBrush(color);

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));

        var gray = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD8));
        group.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
        group.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(4, 4, 4, 4))));

        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(color), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));

        return new DrawingBrush(group) { Stretch = Stretch.Fill };
    }

    private static Color[] Defaults(ColorPresetFamily family)
    {
        var hex = DefaultHex(family);
        var result = new Color[Count];
        for (int i = 0; i < Count; i++)
            result[i] = TryParse(hex[i], out var color) ? color : Colors.Black;
        return result;
    }

    private static Color[] Copy(IReadOnlyList<Color> colors)
    {
        var result = new Color[Count];
        for (int i = 0; i < Count; i++) result[i] = colors[i];
        return result;
    }

    private static void Store(ColorPresetFamily family, Color[] colors)
    {
        switch (family)
        {
            case ColorPresetFamily.Fill:
                _fill = Copy(colors);
                break;
            case ColorPresetFamily.Highlight:
                _highlight = Copy(colors);
                break;
            default:
                _stroke = Copy(colors);
                break;
        }
    }

    private static void LoadAll()
    {
        lock (Gate)
        {
            if (_stroke != null && _fill != null && _highlight != null) return;
        }

        Color[] stroke = Defaults(ColorPresetFamily.Stroke);
        Color[] fill = Defaults(ColorPresetFamily.Fill);
        Color[] highlight = Defaults(ColorPresetFamily.Highlight);

        try
        {
            var shot = SettingsStore.Read(ConfigStore.Anchor).Screenshot;
            stroke = Normalize(shot?.StrokeColorPresets, stroke);
            fill = Normalize(shot?.FillColorPresets, fill);
            highlight = Normalize(shot?.HighlightColorPresets, highlight);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.ColorPresets", "读取颜色预设失败：" + ex.Message);
        }

        lock (Gate)
        {
            _stroke ??= stroke;
            _fill ??= fill;
            _highlight ??= highlight;
        }
    }

    private static void Save(Action<SettingsFile> mutate)
    {
        try
        {
            SettingsStore.Modify(ConfigStore.Anchor, mutate);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.ColorPresets", "保存颜色预设失败：" + ex.Message);
        }
    }
}
