using System;
using System.Linq;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 颜色预设：默认值 / <c>#RRGGBBAA</c> 读写 / 归一 / 工具到族的映射 / 色块画刷。
/// </summary>
/// <remarks>
/// <para>
/// 默认值与「恒定 5 个」照上游 <c>configurationschema.cpp</c> 的
/// <c>screenshot/stroke_color_presets</c> / <c>fill_color_presets</c> /
/// <c>highlight_color_presets</c>；哪一排用哪一族照
/// <c>screenshottoolpalettestylecontrols.cpp</c>。真窗口（换工具时参数条跟着换预设、
/// 设置页改完回显）由仓库外 <c>capture-overlay-smoke</c> 的 <c>colorpresets</c> 段验。
/// </para>
/// <para>
/// 这里**只读**配置：写盘会改掉用户真实的 <c>configs/settings.json</c>，
/// 读写往返留给冒烟（它跑前备份、跑完还原）。
/// </para>
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void ColorPresetCases()
    {
        // ============ 默认值：逐字照上游 ============
        Check.Equal("#F5222DFF", ColorPresets.DefaultHex(ColorPresetFamily.Stroke)[0], "描边第一个是红");
        Check.Equal("#000000FF", ColorPresets.DefaultHex(ColorPresetFamily.Stroke)[4], "描边第五个是黑");
        Check.Equal(5, ColorPresets.DefaultHex(ColorPresetFamily.Stroke).Count, "描边恒 5 个");
        Check.Equal("#FFFFFF00", ColorPresets.DefaultHex(ColorPresetFamily.Fill)[0], "填充第一个是透明");
        Check.Equal("#FFF1B8FF", ColorPresets.DefaultHex(ColorPresetFamily.Fill)[4], "填充第五个是浅黄");
        Check.Equal("#FF7875FF", ColorPresets.DefaultHex(ColorPresetFamily.Highlight)[0], "荧光第一个是浅红");
        Check.Equal("#B37FEBFF", ColorPresets.DefaultHex(ColorPresetFamily.Highlight)[4], "荧光第五个是浅紫");

        // ============ Format：恒 #RRGGBBAA 大写（上游 colorToRgbaString） ============
        Check.Equal("#F5222DFF", ColorPresets.Format(Color.FromRgb(0xF5, 0x22, 0x2D)), "不透明也写 8 位");
        Check.Equal("#FFFFFF00", ColorPresets.Format(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)), "alpha 0 也写出来");
        Check.Equal("#0A0B0C0D", ColorPresets.Format(Color.FromArgb(0x0D, 0x0A, 0x0B, 0x0C)), "四段都补零");

        // ============ TryParse：8 位 / 6 位，别的写法不认 ============
        Check.True(ColorPresets.TryParse("#F5222DFF", out var red) &&
                   red == Color.FromArgb(0xFF, 0xF5, 0x22, 0x2D), "8 位按 RRGGBBAA 解析");
        Check.True(ColorPresets.TryParse("  #ffffff00 ", out var clear) &&
                   clear == Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), "6 位补成不透明，前后空白可忽略");
        Check.True(ColorPresets.TryParse("#123456", out var six) && six.A == 0xFF, "6 位默认不透明");
        Check.False(ColorPresets.TryParse(null, out _), "null 不认");
        Check.False(ColorPresets.TryParse("", out _), "空串不认");
        Check.False(ColorPresets.TryParse("F5222DFF", out _), "少了 # 不认");
        Check.False(ColorPresets.TryParse("#F5222D0", out _), "7 位不认");
        Check.False(ColorPresets.TryParse("#GGGGGGGG", out _), "非十六进制不认");

        // ============ Normalize：数量不对整组回默认，单格坏只坏那一格 ============
        var fallback = new[]
        {
            Color.FromRgb(1, 1, 1), Color.FromRgb(2, 2, 2), Color.FromRgb(3, 3, 3),
            Color.FromRgb(4, 4, 4), Color.FromRgb(5, 5, 5),
        };

        var none = ColorPresets.Normalize(null, fallback);
        Check.True(none.SequenceEqual(fallback), "没有配置就用默认值");

        var four = ColorPresets.Normalize(new[] { "#010101FF", "#020202FF", "#030303FF", "#040404FF" }, fallback);
        Check.True(four.SequenceEqual(fallback), "只有 4 个时整组回默认（上游只收 5 个）");

        var mixed = ColorPresets.Normalize(
            new[] { "#0A0A0AFF", "坏色", "#0C0C0CFF", "#0D0D0DFF", "#0E0E0EFF" }, fallback);
        Check.Equal(Q(0x0A), mixed[0], "能解析的格子照用");
        Check.Equal(fallback[1], mixed[1], "解析不出的格子回默认");
        Check.Equal(Q(0x0E), mixed[4], "后面的格子不受影响");

        // ============ 族映射：荧光用荧光预设，其余（含聚光）用描边 ============
        Check.Equal(ColorPresetFamily.Highlight, ColorPresets.ForTool(CaptureTool.Highlighter), "荧光笔用荧光预设");
        Check.Equal(ColorPresetFamily.Highlight, ColorPresets.ForTool(CaptureTool.HighlightRect), "矩形荧光用荧光预设");
        Check.Equal(ColorPresetFamily.Stroke, ColorPresets.ForTool(CaptureTool.Pen), "画笔用描边预设");
        Check.Equal(ColorPresetFamily.Stroke, ColorPresets.ForTool(CaptureTool.Spotlight), "聚光用描边预设（上游 textColors）");
        Check.Equal(ColorPresetFamily.Stroke, ColorPresets.ForTool(CaptureTool.Shape), "形状用描边预设");

        // ============ 生效值：读到的恒是 5 个，`GetHex` 能原样解析回来 ============
        foreach (var family in new[]
                 {
                     ColorPresetFamily.Stroke, ColorPresetFamily.Fill, ColorPresetFamily.Highlight,
                 })
        {
            var colors = ColorPresets.Get(family);
            Check.Equal(ColorPresets.Count, colors.Count, $"{family} 恒有 5 个颜色");

            var hex = ColorPresets.GetHex(family);
            Check.Equal(ColorPresets.Count, hex.Length, $"{family} 的文本也是 5 个");
            Check.True(hex.All(value => ColorPresets.TryParse(value, out _)), $"{family} 的每个文本都能解析");
        }

        // ============ 色块画刷：不透明是纯色，带透明度铺棋盘格 ============
        Check.True(ColorPresets.Swatch(Colors.Red) is SolidColorBrush, "不透明色块是纯色");
        Check.True(ColorPresets.Swatch(Color.FromArgb(0, 255, 255, 255)) is DrawingBrush,
            "透明色块铺棋盘格（不然看不出来）");
    }

    private static Color Q(int value) => Color.FromRgb((byte)value, (byte)value, (byte)value);
}
