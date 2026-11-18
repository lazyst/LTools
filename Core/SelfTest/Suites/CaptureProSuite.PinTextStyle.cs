using System;
using System.Windows;
using System.Windows.Media;
using LTools.Config;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 贴图文字样式的默认值（照上游 <c>pin_to_screen/text_*</c>）：范围夹取、颜色写法、字体归一、
/// 快照往返、按最大宽折行。
/// </summary>
/// <remarks>
/// 真窗口里的对话框（下拉 / 色板 / 预览 / 恢复默认）由仓库外 <c>capture-overlay-smoke</c> 端到端验；
/// 这里只断言纯函数，任何一条红都说明「写进 settings.json 的是什么」或「贴出来多宽」变了。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void PinTextStyleCases()
    {
        // ============ 范围：上游 100–2000 / 8–96 ============
        Check.Equal(1024, PinTextRules.NormalizeMaximumWidth(null), "最大宽缺省 1024");
        Check.Equal(100, PinTextRules.NormalizeMaximumWidth(-5), "最大宽负数夹到下限 100");
        Check.Equal(100, PinTextRules.NormalizeMaximumWidth(99), "最大宽下限 100");
        Check.Equal(1024, PinTextRules.NormalizeMaximumWidth(1024), "最大宽 1024 原样");
        Check.Equal(2000, PinTextRules.NormalizeMaximumWidth(5000), "最大宽上限 2000");
        Check.Equal(16, PinTextRules.NormalizeFontSize(null), "字号缺省 16");
        Check.Equal(8, PinTextRules.NormalizeFontSize(1), "字号下限 8");
        Check.Equal(96, PinTextRules.NormalizeFontSize(200), "字号上限 96");

        // ============ 字体：去空白；空 / 超长 = 系统默认 ============
        Check.Equal("", PinTextRules.NormalizeFontFamily(null), "字体缺省是空（系统默认）");
        Check.Equal("", PinTextRules.NormalizeFontFamily("   "), "字体全空白 = 系统默认");
        Check.Equal("Consolas", PinTextRules.NormalizeFontFamily("  Consolas  "), "字体去两侧空白");
        Check.Equal("", PinTextRules.NormalizeFontFamily(new string('x', 2000)), "超长字体名 = 系统默认");
        Check.Equal("x", PinTextRules.NormalizeFontFamily("x"), "短字体名保留");

        // ============ 颜色：system / #RRGGBB / #AARRGGBB ============
        Check.True(PinTextRules.ParseColor(null) == null, "缺省颜色 = 跟随主题");
        Check.True(PinTextRules.ParseColor("") == null, "空颜色 = 跟随主题");
        Check.True(PinTextRules.ParseColor("system") == null, "system = 跟随主题");
        Check.True(PinTextRules.ParseColor("SYSTEM") == null, "system 不分大小写");
        Check.Equal(Colors2(125, 211, 83, 255), PinTextRules.ParseColor("#7DD353")!.Value, "认 #RRGGBB");
        Check.Equal(Colors2(125, 211, 83, 255), PinTextRules.ParseColor("7dd353")!.Value, "可省 #");
        Check.Equal(Color.FromArgb(0xAA, 0x7D, 0xD3, 0x53), PinTextRules.ParseColor("#AA7DD353")!.Value,
            "认 #AARRGGBB");
        Check.True(PinTextRules.ParseColor("#12345") == null, "五位 HEX 不认");
        Check.True(PinTextRules.ParseColor("#GGGGGG") == null, "非十六进制不认");
        Check.True(PinTextRules.ParseColor("red") == null, "颜色名不认（与色卡解析同一口径）");

        Check.Equal("system", PinTextRules.FormatColor(null), "跟随主题写成 system");
        Check.Equal("#7DD353", PinTextRules.FormatColor(PinTextRules.ParseColor("#7dd353")), "不透明写 #RRGGBB");
        Check.Equal("#AA7DD353", PinTextRules.FormatColor(Color.FromArgb(0xAA, 0x7D, 0xD3, 0x53)),
            "半透明写 #AARRGGBB");

        // 往返：写出去再读回来是同一个颜色
        foreach (string text in new[] { "#7DD353", "#007DD353", "#FF000000" })
        {
            var color = PinTextRules.ParseColor(text)!.Value;
            Check.Equal(color, PinTextRules.ParseColor(PinTextRules.FormatColor(color))!.Value,
                "颜色写法往返：" + text);
        }

        // ============ 一次归一（文件里的值 → 可用值） ============
        var (width, fontSize, font, textColor, background) = PinTextRules.Normalize(
            5000, 4, " Consolas ", "#7DD353", "system");
        Check.Equal(2000, width, "归一：最大宽夹到 2000");
        Check.Equal(8, fontSize, "归一：字号夹到 8");
        Check.Equal("Consolas", font, "归一：字体去空白");
        Check.Equal(Colors2(125, 211, 83, 255), textColor, "归一：字色认 HEX");
        Check.True(background == null, "归一：底色 system = 跟随主题");

        // ============ PinTextLimits：配置对象 ↔ 可用值 ============
        Check.True(PinTextLimits.From(null) == PinTextLimits.Default, "缺段 = 上游默认值");
        Check.Equal(1024, PinTextLimits.Default.MaximumWidth, "默认最大宽");
        Check.Equal(16.0, PinTextLimits.Default.FontSize, "默认字号");
        Check.Equal("", PinTextLimits.Default.FontFamily, "默认字体 = 系统");
        Check.True(PinTextLimits.Default.TextColor == null, "默认字色跟随主题");
        Check.True(PinTextLimits.Default.BackgroundColor == null, "默认底色跟随主题");

        var limits = PinTextLimits.From(new PinTextFile
        {
            MaximumWidth = 5000,
            FontSize = 4,
            FontFamily = " Consolas ",
            TextColor = "#7DD353",
            BackgroundColor = null,
        });
        Check.Equal(2000, limits.MaximumWidth, "Limits：最大宽夹取");
        Check.Equal(8.0, limits.FontSize, "Limits：字号夹取");
        Check.Equal("Consolas", limits.FontFamily, "Limits：字体归一");
        Check.Equal(Colors2(125, 211, 83, 255), limits.TextColor!.Value, "Limits：字色");
        Check.True(limits.BackgroundColor == null, "Limits：底色跟随主题");

        var file = limits.ToFile();
        Check.Equal(2000, file.MaximumWidth, "ToFile：写归一后的最大宽");
        Check.Equal(8, file.FontSize, "ToFile：写归一后的字号");
        Check.Equal("Consolas", file.FontFamily, "ToFile：写字体");
        Check.Equal("#7DD353", file.TextColor, "ToFile：字色写 #RRGGBB");
        Check.Equal("system", file.BackgroundColor, "ToFile：跟随主题写 system");

        var defaults = PinTextLimits.Default.ToFile();
        Check.Equal(1024, defaults.MaximumWidth, "恢复默认：最大宽 1024");
        Check.Equal(16, defaults.FontSize, "恢复默认：字号 16");
        Check.True(defaults.FontFamily == null, "恢复默认：字体空（系统）");
        Check.Equal("system", defaults.TextColor, "恢复默认：字色 system");
        Check.Equal("system", defaults.BackgroundColor, "恢复默认：底色 system");

        // ============ 快照：贴出那一刻的样式，之后改默认值不受影响 ============
        var style = new PinTextStyle
        {
            MaximumWidth = 700,
            FontSize = 22.4,
            FontFamily = "Consolas",
            TextColor = Colors2(0x11, 0x22, 0x33, 255),
            BackgroundColor = Colors2(0xF0, 0xF1, 0xF2, 192),
        };
        var snapshot = PinTextSnapshot.Of(style);
        Check.Equal(700, snapshot.MaximumWidth, "快照：最大宽");
        Check.Equal(22, snapshot.FontSize, "快照：字号取整");
        Check.Equal("Consolas", snapshot.FontFamily, "快照：字体");
        Check.Equal("#112233", snapshot.TextColor, "快照：字色固定成 #RRGGBB");
        Check.Equal("#C0F0F1F2", snapshot.BackgroundColor, "快照：半透明底色带 alpha");

        var restored = snapshot.ToStyle();
        Check.Equal(700.0, restored.MaximumWidth, "按快照还原：最大宽");
        Check.Equal(22.0, restored.FontSize, "按快照还原：字号");
        Check.Equal("Consolas", restored.FontFamily, "按快照还原：字体");
        Check.Equal(Colors2(0x11, 0x22, 0x33, 255), restored.TextColor, "按快照还原：字色");
        Check.Equal(Colors2(0xF0, 0xF1, 0xF2, 192), restored.BackgroundColor, "按快照还原：底色");

        // 越界 / 认不出的快照值也按同一套规则兜底
        var odd = new PinTextSnapshot
        {
            MaximumWidth = 99999,
            FontSize = 0,
            FontFamily = "",
            TextColor = "red",
            BackgroundColor = "system",
        }.ToStyle();
        Check.Equal(2000.0, odd.MaximumWidth, "快照兜底：最大宽夹到 2000");
        Check.Equal(8.0, odd.FontSize, "快照兜底：字号夹到 8");
        Check.True(odd.FontFamily.Length > 0, "快照兜底：空字体回落到系统字体");

        // ============ 折行：卡片宽度按最大宽收口 ============
        var narrow = new PinTextStyle { MaximumWidth = 1024, FontSize = 16, Padding = 16 };
        var shortCard = narrow.CardSize(100, 20, out double shortWrap);
        Check.Equal(132.0, shortCard.Width, "短文字：理想宽 + 两侧内边距");
        Check.Equal(100.0, shortWrap, "短文字：排版宽度 = 卡片宽 − 内边距");

        var capped = new PinTextStyle { MaximumWidth = 200, FontSize = 16, Padding = 16 };
        var wideCard = capped.CardSize(5000, 20, out double wideWrap);
        Check.Equal(200.0, wideCard.Width, "超宽文字：卡片宽收到最大宽");
        Check.Equal(168.0, wideWrap, "超宽文字：排版宽度扣掉内边距");

        var smallCap = new PinTextStyle { MaximumWidth = 100, FontSize = 16, Padding = 16 };
        Check.Equal(100.0, smallCap.CardSize(5000, 20, out _).Width, "下限 100 时仍能收口");
    }
}
