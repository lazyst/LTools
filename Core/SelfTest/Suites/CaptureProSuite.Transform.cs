using System;
using System.Collections.Generic;
using LTools.Features;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 识别文字的排版 / 标点转换（照上游 <c>screenshotocrtexttransform.cpp</c> 与
/// <c>screenshotocrtexteditingsession</c> 的基线式变换）。
/// </summary>
/// <remarks>
/// 纯数据，不碰 WPF：<c>OcrTextTransform</c> 给出「新行文本 + 新文字层（合并后的行框）」，
/// 界面据此重建文字层（仓库外 <c>capture-overlay-smoke</c> 端到端断言画布行数与文本）。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void TextTransformCases()
    {
        OcrOptionCases();
        OcrPunctuationTableCases();
        OcrTransformApplyCases();
        OcrSmartLayoutCases();
    }

    private static void OcrOptionCases()
    {
        Check.Equal(4, OcrFormatting.Keys.Count, "排版四个选项");
        Check.Equal("none", OcrFormatting.Keys[0], "排版第一项是「不排版」");
        Check.Equal("smart", OcrFormatting.Keys[3], "排版最后一项是智能排版");
        Check.Equal(3, OcrPunctuation.Keys.Count, "标点三个选项");

        Check.Equal(OcrFormatting.None, OcrFormatting.Normalize(null), "空排版值 → 不排版");
        Check.Equal(OcrFormatting.None, OcrFormatting.Normalize("  "), "空白排版值 → 不排版");
        Check.Equal(OcrFormatting.None, OcrFormatting.Normalize("乱写"), "认不出的排版值 → 不排版");
        Check.Equal(OcrFormatting.Smart, OcrFormatting.Normalize(" SMART "), "排版键大小写不敏感 + 去空白");
        Check.Equal(OcrPunctuation.None, OcrPunctuation.Normalize(null), "空标点值 → 不转换");
        Check.Equal(OcrPunctuation.Full, OcrPunctuation.Normalize("Full"), "标点键大小写不敏感");

        Check.Equal("不排版", OcrFormatting.LabelOf("none"), "不排版的标签");
        Check.Equal("保留换行", OcrFormatting.LabelOf("keep"), "保留换行的标签");
        Check.Equal("去掉换行", OcrFormatting.LabelOf("remove"), "去掉换行的标签");
        Check.Equal("智能排版", OcrFormatting.LabelOf("smart"), "智能排版的标签");
        Check.Equal("半角标点", OcrPunctuation.LabelOf("half"), "半角的标签");
        Check.Equal("全角标点", OcrPunctuation.LabelOf("full"), "全角的标签");
        Check.Equal("不转换", OcrPunctuation.LabelOf("none"), "不转换的标签");

        Check.True(OcrFormatting.Describe("smart").Contains("段落"), "智能排版的说明提到段落");
        Check.True(OcrPunctuation.Describe("full").Contains("全角"), "全角的说明提到全角");

        // 列表记号（智能排版用来判「自己一段」）：与 ConversionDocument 的块模型共用一份判定
        Check.True(ConversionDocument.StartsListItem("• 项目一"), "圆点开头是列表项");
        Check.True(ConversionDocument.StartsListItem("- dash"), "短横线开头是列表项");
        Check.True(ConversionDocument.StartsListItem("1. 第一步"), "1. 开头是列表项");
        Check.True(ConversionDocument.StartsListItem("2、第二步"), "2、开头是列表项");
        Check.False(ConversionDocument.StartsListItem("普通一句话"), "普通句子不是列表项");
        Check.False(ConversionDocument.StartsListItem("2026 年"), "纯数字开头不是列表项");
        Check.False(ConversionDocument.StartsListItem(""), "空行不是列表项");
    }

    private static void OcrPunctuationTableCases()
    {
        // 只删 \n（\r 一起收掉，Windows 换行不留尾巴）
        Check.Equal("abc", OcrTextTransform.RemoveLineBreaks("a\nb\nc"), "去换行把三行接成一行");
        Check.Equal("abc", OcrTextTransform.RemoveLineBreaks("a\r\nb\r\nc"), "CRLF 也接成一行（不留 \\r）");
        Check.Equal("", OcrTextTransform.RemoveLineBreaks(""), "空文本去换行还是空");

        // 32 个字符表：半角 → 全角 → 半角 回到原样
        const string ascii = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
        string wide = OcrTextTransform.ConvertPunctuation(ascii, true);
        Check.Equal(32, wide.Length, "32 个半角标点换出 32 个全角字符");
        Check.True(wide.Contains('，') && wide.Contains('！') && wide.Contains('：'), "换出的是全角标点");
        Check.Equal(ascii, OcrTextTransform.ConvertPunctuation(wide, false), "全角再换回半角是原样");

        // 汉字、字母、数字都不动；不在表里的中文标点（。、《》）也不动
        Check.Equal("你好，世界", OcrTextTransform.ConvertPunctuation("你好,世界", true), "只换标点，汉字不动");
        Check.Equal("Hello, world!", OcrTextTransform.ConvertPunctuation("Hello, world!", false),
            "已经是半角就原样");
        Check.Equal("。、《》", OcrTextTransform.ConvertPunctuation("。、《》", true),
            "不在表里的中文标点不动");

        // ApplyPunctuation 认键
        Check.Equal("a,b", OcrTextTransform.ApplyPunctuation("a，b", "half"), "half 换半角");
        Check.Equal("a，b", OcrTextTransform.ApplyPunctuation("a,b", "full"), "full 换全角");
        Check.Equal("a,b", OcrTextTransform.ApplyPunctuation("a,b", "none"), "none 原样");
        Check.Equal("a,b", OcrTextTransform.ApplyPunctuation("a,b", "乱写"), "认不出的键按 none");
    }

    private static void OcrTransformApplyCases()
    {
        var boxes = new List<OcrLineBox>
        {
            Line("Hello", 10, 10, 100),
            Line("world", 10, 32, 100),
            Line("Third", 10, 80, 120),
        };
        IReadOnlyList<OcrTextLine> layer = OcrTextLayer.FromLines(boxes);
        var texts = new List<string> { "Hello", "world", "Third" };

        // 1) 不排版：行数与行框原样
        OcrTransformResult none = OcrTextTransform.Apply(texts, layer, "none", "none");
        Check.Equal(3, none.Lines.Count, "不排版仍是 3 行");
        Check.Equal(3, none.Layer.Count, "不排版的文字层仍是 3 行");
        Check.Equal("Hello", none.Lines[0], "不排版第一行原样");
        Check.Equal(10, none.Layer[0].Box.X, "不排版行框不变");
        Check.Equal(20, none.Layer[0].Box.Height, "不排版行高不变");

        // 2) 保留换行 = 与不排版等价
        OcrTransformResult keep = OcrTextTransform.Apply(texts, layer, "keep", "none");
        Check.Equal(3, keep.Lines.Count, "保留换行不合并");

        // 3) 去掉换行：合成一行，行框取并集
        OcrTransformResult removed = OcrTextTransform.Apply(texts, layer, "remove", "none");
        Check.Equal(1, removed.Lines.Count, "去换行合成 1 行");
        Check.Equal("HelloworldThird", removed.Lines[0], "去换行只是把 \\n 删掉（不补空格，照上游）");
        Check.Equal(1, removed.Layer.Count, "去换行的文字层也是 1 行");
        Check.Equal(10, removed.Layer[0].Box.X, "合并后的左边界 = 最左");
        Check.Equal(10, removed.Layer[0].Box.Y, "合并后的上边界 = 最上");
        Check.Equal(120, removed.Layer[0].Box.Width, "合并后的宽 = 最宽那行");
        Check.Equal(90, removed.Layer[0].Box.Height, "合并后的高 = 从最上到最下");

        // 4) 标点只改标点，行数与行框不变
        var marks = new List<OcrLineBox> { Line("a,b", 10, 10, 100), Line("c,d", 10, 32, 100) };
        IReadOnlyList<OcrTextLine> markLayer = OcrTextLayer.FromLines(marks);
        var markTexts = new List<string> { "a,b", "c,d" };
        OcrTransformResult full = OcrTextTransform.Apply(markTexts, markLayer, "none", "full");
        Check.Equal(2, full.Lines.Count, "标点转换不改行数");
        Check.Equal("a，b", full.Lines[0], "第一行的标点换成全角");
        Check.Equal("c，d", full.Lines[1], "第二行的标点换成全角");
        Check.Equal(10, full.Layer[0].Box.Y, "标点转换不动行框");

        // 5) 排版 + 标点一起：先去换行再换标点
        OcrTransformResult both = OcrTextTransform.Apply(
            new List<string> { "a,b", "c,d" }, markLayer, "remove", "half");
        Check.Equal(1, both.Lines.Count, "排版 + 标点合成 1 行");
        Check.Equal("a,bc,d", both.Lines[0], "先删换行，标点已是半角就原样");

        // 6) 空层：不炸、返回空
        OcrTransformResult empty = OcrTextTransform.Apply(Array.Empty<string>(),
            Array.Empty<OcrTextLine>(), "smart", "full");
        Check.Equal(0, empty.Lines.Count, "空文字层不产出任何行");
        Check.Equal(0, empty.Layer.Count, "空文字层的层也是空");
    }

    private static void OcrSmartLayoutCases()
    {
        // 一、同一段：行距紧（≤ 中位行高 0.75）+ 行高一致 → 接成一段（英文补空格）
        var paragraph = new List<OcrLineBox>
        {
            Line("Hello", 10, 10, 100),
            Line("world", 10, 32, 100),
        };
        IReadOnlyList<OcrTextLine> paragraphLayer = OcrTextLayer.FromLines(paragraph);
        IReadOnlyList<string> merged = OcrTextTransform.SmartTexts(
            new List<string> { "Hello", "world" }, paragraphLayer);
        Check.Equal(1, merged.Count, "紧挨着的两行接成一段");
        Check.Equal("Hello world", merged[0], "英文接段落补一个空格");

        // 中文不补空格
        var cjk = new List<OcrLineBox>
        {
            Line("第一句，", 10, 10, 100),
            Line("第二句。", 10, 32, 100),
        };
        IReadOnlyList<string> cjkMerged = OcrTextTransform.SmartTexts(
            new List<string> { "第一句，", "第二句。" }, OcrTextLayer.FromLines(cjk));
        Check.Equal(1, cjkMerged.Count, "中文两行也接成一段");
        Check.Equal("第一句，第二句。", cjkMerged[0], "中文接段落不补空格");

        // 二、竖向间距大 → 另起一段
        var spaced = new List<OcrLineBox>
        {
            Line("第一段", 10, 10, 100),
            Line("第二段", 10, 90, 100),
        };
        IReadOnlyList<string> spacedMerged = OcrTextTransform.SmartTexts(
            new List<string> { "第一段", "第二段" }, OcrTextLayer.FromLines(spaced));
        Check.Equal(2, spacedMerged.Count, "行距大就另起一段");

        // 三、行高差超过 1.6 倍 → 另起一段（标题不会跟正文粘一起）
        var sizes = new List<OcrLineBox>
        {
            Line("大标题", 10, 10, 120, 40),
            Line("正文一行", 10, 54, 120, 20),
        };
        IReadOnlyList<string> sizeMerged = OcrTextTransform.SmartTexts(
            new List<string> { "大标题", "正文一行" }, OcrTextLayer.FromLines(sizes));
        Check.Equal(2, sizeMerged.Count, "字号差超 1.6 倍另起一段");

        // 四、列表项自己一段（哪怕紧挨着上一行）
        var list = new List<OcrLineBox>
        {
            Line("说明文字", 10, 10, 100),
            Line("• 项目一", 10, 32, 100),
        };
        IReadOnlyList<string> listMerged = OcrTextTransform.SmartTexts(
            new List<string> { "说明文字", "• 项目一" }, OcrTextLayer.FromLines(list));
        Check.Equal(2, listMerged.Count, "列表项自己一段");
        Check.Equal("• 项目一", listMerged[1], "列表项的记号保留");

        // 五、智能排版后行框是成员的并集
        OcrTransformResult smart = OcrTextTransform.Apply(
            new List<string> { "Hello", "world", "Third" },
            OcrTextLayer.FromLines(new List<OcrLineBox>
            {
                Line("Hello", 10, 10, 100),
                Line("world", 10, 32, 100),
                Line("Third", 10, 80, 120),
            }), "smart", "none");
        Check.Equal(2, smart.Lines.Count, "智能排版：两段");
        Check.Equal("Hello world", smart.Lines[0], "第一段接起来了");
        Check.Equal("Third", smart.Lines[1], "第二段是单独一行");
        Check.Equal(10, smart.Layer[0].Box.Y, "第一段的框从第一行开始");
        Check.Equal(42, smart.Layer[0].Box.Height, "第一段的高度 = 两行的并集");
        Check.Equal(80, smart.Layer[1].Box.Y, "第二段的框是自己的位置");

        // 六、空白行不参与（不产出空段）
        IReadOnlyList<string> skipped = OcrTextTransform.SmartTexts(
            new List<string> { "Hello", "   ", "world" },
            OcrTextLayer.FromLines(new List<OcrLineBox>
            {
                Line("Hello", 10, 10, 100),
                Line("   ", 10, 32, 100),
                Line("world", 10, 82, 100),
            }));
        bool anyEmpty = false;
        foreach (string text in skipped)
            if (text.Length == 0) anyEmpty = true;
        Check.False(anyEmpty, "空白行不产出空段");
        Check.Equal("Hello", skipped[0], "空白行之前的文字照常");
    }
}
