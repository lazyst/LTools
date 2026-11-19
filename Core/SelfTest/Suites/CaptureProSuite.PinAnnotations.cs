using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LTools.Config;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 贴图「快速文字标注」（P9-d3，照上游 <c>screenshottextannotation.cpp</c> /
/// <c>screenshottextannotationrenderer.cpp</c> / <c>screenshotquicktextannotationtoolbar.cpp</c>）：
/// 样式默认值与归一、选区切分与合并、四种工具的 toggle、样式回写、载荷往返、几何。
/// </summary>
/// <remarks>
/// 真窗口那一段（划选 → 工具条 → 点按钮 → 画到画布 / 导出 png / 贴回恢复）在仓库外
/// <c>capture-overlay-smoke</c> 的 <c>pinannotate</c> 段；这里只碰纯数据。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void PinAnnotationCases()
    {
        // ============ 样式默认值 / 归一 ============

        Check.Equal(PinTextAnnotationStyle.HighlightDefault,
            PinTextAnnotationStyle.Default(PinTextAnnotationTool.Highlight).Color,
            "高亮默认色 = 上游 rectangleHighlight.fill #FF7875");
        Check.Equal(PinTextAnnotationStyle.StrokeDefault,
            PinTextAnnotationStyle.Default(PinTextAnnotationTool.Underline).Color,
            "线标注默认色 = 上游 rectangle.stroke #F5222D");
        Check.Equal(2.0, PinTextAnnotationStyle.Default(PinTextAnnotationTool.WavyUnderline).Width,
            "默认线宽 2");
        Check.Equal(StrokeKind.Solid,
            PinTextAnnotationStyle.Default(PinTextAnnotationTool.Strikethrough).StrokeStyle,
            "默认实线");
        Check.Equal(StrokeRendering.Architect,
            PinTextAnnotationStyle.Default(PinTextAnnotationTool.Strikethrough).RenderingStyle,
            "默认干净笔触");

        var fallback = PinTextAnnotationStyle.Default(PinTextAnnotationTool.Highlight);
        var coerced = PinTextAnnotationStyle.Normalize(Colors.Blue, 0, 9, -1,
            PinTextAnnotationTool.Highlight);
        Check.Equal(Colors.Blue, coerced.Color, "颜色有效就保留（只有坏项回落默认）");
        Check.Equal(fallback.Width, coerced.Width, "宽 0 回落默认");
        Check.Equal(fallback.StrokeStyle, coerced.StrokeStyle, "线型 9 回落默认");
        Check.Equal(fallback.RenderingStyle, coerced.RenderingStyle, "笔触 -1 回落默认");
        Check.Equal(fallback.Width, PinTextAnnotationStyle.Normalize(Colors.Blue, 100, 0, 0,
            PinTextAnnotationTool.Highlight).Width, "宽超 72 回落默认");
        Check.Equal(72.0, PinTextAnnotationStyle.Normalize(Colors.Blue, 72, 0, 0,
            PinTextAnnotationTool.Highlight).Width, "宽正好 72 合法");
        Check.Equal(StrokeKind.Dotted, PinTextAnnotationStyle.Normalize(Colors.Blue, 3, 2, 2,
            PinTextAnnotationTool.Highlight).StrokeStyle, "线型 2 = 点线");
        Check.Equal(StrokeRendering.Cartoonist, PinTextAnnotationStyle.Normalize(Colors.Blue, 3, 0, 2,
            PinTextAnnotationTool.Highlight).RenderingStyle, "笔触 2 = 涂鸦");
        Check.False(new PinTextAnnotationStyle(Colors.Blue, 0, StrokeKind.Solid,
            StrokeRendering.Architect).IsValid, "宽 0 不算有效样式");
        Check.False(new PinTextAnnotationStyle(Colors.Blue, 73, StrokeKind.Solid,
            StrokeRendering.Architect).IsValid, "宽超 72 不算有效样式");
        Check.True(PinTextAnnotationStyle.Default(PinTextAnnotationTool.Highlight).IsValid, "默认样式有效");

        // ============ 工具键 / 中文名（与上游英文键一致） ============

        Check.Equal("highlight", PinSettings.AnnotationToolKey(PinTextAnnotationTool.Highlight), "高亮的键");
        Check.Equal("wavy_underline", PinSettings.AnnotationToolKey(PinTextAnnotationTool.WavyUnderline),
            "波浪线的键");
        Check.Equal("underline", PinSettings.AnnotationToolKey(PinTextAnnotationTool.Underline), "下划线的键");
        Check.Equal("strikethrough", PinSettings.AnnotationToolKey(PinTextAnnotationTool.Strikethrough),
            "删除线的键");
        Check.Equal(PinTextAnnotationTool.WavyUnderline, PinSettings.AnnotationToolOf("wavy_underline"),
            "键 → 工具");
        Check.Equal(PinTextAnnotationTool.Highlight, PinSettings.AnnotationToolOf("认不出的键"),
            "认不出的键回落高亮（上游默认）");
        Check.Equal("波浪线", PinSettings.AnnotationToolLabel(PinTextAnnotationTool.WavyUnderline),
            "工具中文名");
        Check.Equal(4, PinSettings.AnnotationTools.Length, "四种工具");

        // 配置项 → 样式：缺项用默认，坏值回落，四项齐全
        var entry = new PinTextAnnotationStyleFile { Color = "#00FF00FF", Width = 5, StrokeStyle = 1,
            RenderingStyle = 2 };
        var normalized = PinSettings.NormalizeAnnotationStyle(PinTextAnnotationTool.Underline, entry);
        Check.Equal(Color.FromArgb(255, 0, 255, 0), normalized.Color, "配置颜色按 #RRGGBBAA 解析");
        Check.Equal(5.0, normalized.Width, "配置线宽保留");
        Check.Equal(StrokeKind.Dashed, normalized.StrokeStyle, "配置线型保留");
        Check.Equal(StrokeRendering.Cartoonist, normalized.RenderingStyle, "配置笔触保留");
        Check.Equal(PinTextAnnotationStyle.Default(PinTextAnnotationTool.Underline),
            PinSettings.NormalizeAnnotationStyle(PinTextAnnotationTool.Underline, null), "没有配置项 = 默认");
        Check.Equal(PinTextAnnotationStyle.Default(PinTextAnnotationTool.Highlight).Color,
            PinSettings.NormalizeAnnotationStyle(PinTextAnnotationTool.Highlight,
                new PinTextAnnotationStyleFile { Color = "不是颜色" }).Color, "坏颜色回落默认");

        // ============ 选区归一（排序 / 合并 / 丢空段） ============

        var merged = PinTextAnnotationDocument.NormalizeSelection(new[]
        {
            new PinTextRange(1, 4, 8), new PinTextRange(0, 6, 9), new PinTextRange(0, 2, 6),
            new PinTextRange(0, 6, 6), new PinTextRange(-1, 0, 3),
        });
        Check.Equal(2, merged.Count, "选区归一后两段（空段 / 负行丢掉，同行相邻合并）");
        Check.Equal(new PinTextRange(0, 2, 9), merged[0], "第 0 行并成 2..9");
        Check.Equal(new PinTextRange(1, 4, 8), merged[1], "第 1 行原样保留");

        // ============ toggle：加 / 减 / 相邻合并 / 部分重叠切分 ============

        var document = new PinTextAnnotationDocument();
        document.BindSource(PinTextAnnotationDocument.SourceOcr, "fp-1");

        var style = PinTextAnnotationStyle.Default(PinTextAnnotationTool.Highlight);
        Check.True(document.Toggle(new[] { new PinTextRange(0, 0, 5) },
            PinTextAnnotationTool.Highlight, style), "第一次点高亮 = 加上");
        Check.Equal(1, document.Runs.Count, "一段高亮");
        Check.Equal(new PinTextRange(0, 0, 5), document.Runs[0].Range, "范围就是选区");
        Check.True(document.HasStyle(new[] { new PinTextRange(0, 0, 5) },
            PinTextAnnotationTool.Highlight, requireFull: true), "整段都被覆盖");

        Check.True(document.Toggle(new[] { new PinTextRange(0, 3, 8) },
            PinTextAnnotationTool.Highlight, style), "再点重叠段 = 去掉重叠部分");
        Check.Equal(new PinTextRange(0, 0, 3), document.Runs[0].Range, "只剩没被点到的 0..3");
        Check.False(document.HasStyle(new[] { new PinTextRange(0, 3, 8) },
            PinTextAnnotationTool.Highlight), "去掉之后选区里没有高亮");

        Check.True(document.Toggle(new[] { new PinTextRange(0, 3, 8) },
            PinTextAnnotationTool.Highlight, style), "再点回来");
        Check.Equal(1, document.Runs.Count, "相邻且样式相同 → 合并回一段");
        Check.Equal(new PinTextRange(0, 0, 8), document.Runs[0].Range, "合并成 0..8");

        // 高亮与线标注互相独立
        var underline = PinTextAnnotationStyle.Default(PinTextAnnotationTool.Underline);
        Check.True(document.Toggle(new[] { new PinTextRange(0, 0, 8) },
            PinTextAnnotationTool.Underline, underline), "同一段再加下划线");
        Check.Equal(1, document.Runs.Count, "仍是同一段（一个 run 同时带两种标注）");
        Check.True(document.Runs[0].Highlight is not null, "高亮还在");
        Check.Equal(PinTextAnnotationTool.Underline, document.Runs[0].LineTool, "线标注是下划线");

        // 线标注同一段只能有一种（换工具 = 换线）
        var wavy = PinTextAnnotationStyle.Default(PinTextAnnotationTool.WavyUnderline);
        Check.True(document.Toggle(new[] { new PinTextRange(0, 0, 8) },
            PinTextAnnotationTool.WavyUnderline, wavy), "换成波浪线");
        Check.Equal(PinTextAnnotationTool.WavyUnderline, document.Runs[0].LineTool, "线标注换成了波浪线");
        Check.False(document.HasStyle(new[] { new PinTextRange(0, 0, 8) },
            PinTextAnnotationTool.Underline), "下划线没了");

        // 再点同一种 = 去掉线标注，高亮不受影响
        Check.True(document.Toggle(new[] { new PinTextRange(0, 0, 8) },
            PinTextAnnotationTool.WavyUnderline, wavy), "再点波浪线 = 去掉");
        Check.True(document.Runs[0].LineTool is null, "线标注已去掉");
        Check.True(document.Runs[0].Highlight is not null, "高亮仍在");

        // ============ 样式回写（只改已有标注，只改指定属性） ============

        var doc2 = new PinTextAnnotationDocument();
        doc2.BindSource(PinTextAnnotationDocument.SourceOcr, "fp-2");
        doc2.Toggle(new[] { new PinTextRange(0, 0, 4) }, PinTextAnnotationTool.Underline, underline);

        var recolored = underline with { Color = Colors.Green, Width = 6 };
        Check.True(doc2.UpdateStyle(new[] { new PinTextRange(0, 0, 4) }, PinTextAnnotationTool.Underline,
            recolored, PinTextAnnotationDocument.PropertyColor), "只改颜色");
        Check.Equal(Colors.Green, doc2.Runs[0].LineStyle.Color, "颜色改了");
        Check.Equal(underline.Width, doc2.Runs[0].LineStyle.Width, "线宽没动（属性掩码只含颜色）");

        Check.False(doc2.UpdateStyle(new[] { new PinTextRange(0, 1, 2) }, PinTextAnnotationTool.Highlight,
            style, PinTextAnnotationDocument.PropertyAll), "没有高亮可改 = 不新增");
        Check.Equal(1, doc2.Runs.Count, "仍然只有那一段下划线");

        // 多处样式不同：mixed + 差异属性
        var doc3 = new PinTextAnnotationDocument();
        doc3.BindSource(PinTextAnnotationDocument.SourceOcr, "fp-3");
        doc3.Toggle(new[] { new PinTextRange(0, 0, 2) }, PinTextAnnotationTool.Underline, underline);
        doc3.Toggle(new[] { new PinTextRange(0, 3, 5) }, PinTextAnnotationTool.Underline,
            underline with { Width = 5 });
        var selection = new[] { new PinTextRange(0, 0, 5) };
        Check.True(doc3.SelectedStyle(selection, PinTextAnnotationTool.Underline, out bool mixed,
            out int mixedProperties) is not null, "选区里取得到样式");
        Check.True(mixed, "两段样式不同 = mixed");
        Check.Equal(PinTextAnnotationDocument.PropertyWidth, mixedProperties, "差在线宽");

        // 部分覆盖：requireFull = false 有交集就算，true 要整段
        Check.True(doc3.HasStyle(new[] { new PinTextRange(0, 0, 5) },
            PinTextAnnotationTool.Underline), "有交集就算有");
        Check.False(doc3.HasStyle(new[] { new PinTextRange(0, 0, 5) },
            PinTextAnnotationTool.Underline, requireFull: true), "中间空着不算整段覆盖");

        // ============ 源指纹：换文本就整体作废 ============

        var doc4 = new PinTextAnnotationDocument();
        doc4.BindSource(PinTextAnnotationDocument.SourceOcr, "fp-a");
        doc4.Toggle(new[] { new PinTextRange(0, 0, 3) }, PinTextAnnotationTool.Highlight, style);
        Check.False(doc4.Empty, "有标注");
        int revision = doc4.Revision;
        doc4.BindSource(PinTextAnnotationDocument.SourceOcr, "fp-a");
        Check.Equal(revision, doc4.Revision, "同一份源重复绑定 = 空操作");
        doc4.BindSource(PinTextAnnotationDocument.SourceOcr, "fp-b");
        Check.True(doc4.Empty, "指纹变了 = 标注整份作废");

        Check.True(doc4.ValidForSource(new[] { 10 }), "空文档对任何源都成立");
        doc4.Toggle(new[] { new PinTextRange(0, 0, 10) }, PinTextAnnotationTool.Highlight, style);
        Check.True(doc4.ValidForSource(new[] { 10 }), "行内范围在长度内");
        Check.False(doc4.ValidForSource(new[] { 9 }), "超出该行长度就不成立");

        // ============ 载荷往返 ============

        var source = new PinTextAnnotationDocument();
        source.BindSource(PinTextAnnotationDocument.SourceOcr, "fingerprint-1");
        source.Toggle(new[] { new PinTextRange(0, 0, 3) }, PinTextAnnotationTool.Highlight, style);
        source.Toggle(new[] { new PinTextRange(1, 2, 6) }, PinTextAnnotationTool.WavyUnderline,
            wavy with { Width = 4, StrokeStyle = StrokeKind.Dotted, RenderingStyle = StrokeRendering.Artist });
        source.Toggle(new[] { new PinTextRange(1, 2, 6) }, PinTextAnnotationTool.Highlight, style);

        string payload = source.ToPayload();
        Check.True(payload.Length > 0, "有标注时载荷非空");

        var restored = new PinTextAnnotationDocument();
        Check.True(restored.Restore(payload), "载荷能恢复");
        Check.Equal("fingerprint-1", restored.Fingerprint, "指纹一起回来");
        Check.Equal(source.Runs.Count, restored.Runs.Count, "run 数一致");
        for (int i = 0; i < source.Runs.Count; i++)
            Check.Equal(source.Runs[i], restored.Runs[i], $"第 {i} 段逐字段一致");
        Check.Equal(source.ToPayload(), restored.ToPayload(), "再序列化完全一致");

        Check.False(new PinTextAnnotationDocument().Restore("{"), "坏 JSON 拒绝");
        Check.False(new PinTextAnnotationDocument().Restore("{\"Version\":2,\"Source\":\"ocr\"}"),
            "版本不对拒绝");
        Check.False(new PinTextAnnotationDocument().Restore(
                "{\"Version\":1,\"Source\":\"other\",\"Fingerprint\":\"x\",\"Runs\":[]}"),
            "源不是 ocr 拒绝");
        Check.False(new PinTextAnnotationDocument().Restore(
                "{\"Version\":1,\"Source\":\"ocr\",\"Fingerprint\":\"x\",\"Runs\":[{\"Line\":0,\"Start\":3,\"End\":3}]}"),
            "空范围拒绝");
        Check.False(new PinTextAnnotationDocument().Restore(
                "{\"Version\":1,\"Source\":\"ocr\",\"Fingerprint\":\"x\",\"Runs\":[{\"Line\":1,\"Start\":0,\"End\":2},{\"Line\":0,\"Start\":0,\"End\":2}]}"),
            "乱序拒绝");
        Check.True(new PinTextAnnotationDocument().Restore(""), "空载荷 = 清空并成功");
        Check.Equal("", new PinTextAnnotationDocument().ToPayload(), "空文档序列化成空串");

        // ============ 源指纹：文本 / 行框都参与 ============

        var lines = new List<OcrTextLine>
        {
            new() { Index = 0, Text = "第一行", Box = new Int32Rect(10, 10, 100, 20) },
            new() { Index = 1, Text = "第二行", Box = new Int32Rect(10, 40, 120, 20) },
        };
        var texts = new List<string> { "第一行", "第二行" };
        string fingerprint = PinTextAnnotationDocument.FingerprintOf(lines, texts);
        Check.Equal(fingerprint, PinTextAnnotationDocument.FingerprintOf(lines, texts), "同一份源指纹稳定");
        Check.NotEqual(fingerprint,
            PinTextAnnotationDocument.FingerprintOf(lines, new List<string> { "第一行", "第二行改" }),
            "文字改了指纹就变");
        Check.NotEqual(fingerprint,
            PinTextAnnotationDocument.FingerprintOf(new List<OcrTextLine>
            {
                new() { Index = 0, Text = "第一行", Box = new Int32Rect(10, 11, 100, 20) },
                new() { Index = 1, Text = "第二行", Box = new Int32Rect(10, 40, 120, 20) },
            }, texts), "行框挪了指纹也变");

        // ============ 几何（上游 amplitude / halfWave / y） ============

        Check.Equal(1.5, PinTextAnnotationGeometry.Amplitude(2), "振幅 = max(1, 宽 × 0.75)");
        Check.Equal(1.0, PinTextAnnotationGeometry.Amplitude(0.5), "振幅下限 1");
        Check.Equal(4.0, PinTextAnnotationGeometry.HalfWave(2), "半波长 = max(3, 宽 × 2)");
        Check.Equal(3.0, PinTextAnnotationGeometry.HalfWave(1), "半波长下限 3");

        var box = new Rect(0, 0, 100, 10);
        Check.Equal(11.0, PinTextAnnotationGeometry.LineY(box, PinTextAnnotationTool.Underline, 2),
            "下划线在行底 + 半个线宽");
        Check.Equal(12.5, PinTextAnnotationGeometry.LineY(box, PinTextAnnotationTool.WavyUnderline, 2),
            "波浪线再让一个振幅");
        Check.Equal(5.0, PinTextAnnotationGeometry.LineY(box, PinTextAnnotationTool.Strikethrough, 2),
            "删除线在行中");

        var straight = PinTextAnnotationGeometry.Centerline(box, PinTextAnnotationTool.Underline, 2);
        Check.Equal(1, straight.Count, "下划线是一段直线");
        Check.Equal(new Point(0, 11), straight[0].A, "直线起点");
        Check.Equal(new Point(100, 11), straight[0].B, "直线终点");

        var wave = PinTextAnnotationGeometry.Centerline(box, PinTextAnnotationTool.WavyUnderline, 2);
        Check.Equal(25, wave.Count, "半波长 4、宽 100 = 25 段");
        Check.True(wave.All(segment => segment.A.Y == 12.5 && segment.B.Y == 12.5),
            "每段两端都落在中心线上（峰谷在控制点）");
        Check.Equal(new Point(0, 12.5), wave[0].A, "从行首开始");
        Check.Equal(100.0, wave[^1].B.X, "到行尾结束");

        Check.NotEqual(PinTextAnnotationGeometry.Seed(new PinTextRange(0, 0, 3), PinTextAnnotationTool.Highlight),
            PinTextAnnotationGeometry.Seed(new PinTextRange(0, 0, 3), PinTextAnnotationTool.Underline),
            "不同工具种子不同");
        Check.Equal(PinTextAnnotationGeometry.Seed(new PinTextRange(0, 0, 3), PinTextAnnotationTool.Highlight),
            PinTextAnnotationGeometry.Seed(new PinTextRange(0, 0, 3), PinTextAnnotationTool.Highlight),
            "同一段种子稳定");

        // ============ 高亮近似色 / 运行模型 ============

        Check.Equal((byte)128, PinTextAnnotationPainter.HighlightColor(
            Color.FromRgb(0xFF, 0x78, 0x75)).A, "不透明高亮降到 50%（WPF 没有 Multiply）");
        Check.Equal((byte)64, PinTextAnnotationPainter.HighlightColor(
            Color.FromArgb(64, 0, 0, 0)).A, "本来就半透明的颜色原样保留");

        var emptyRun = new PinTextAnnotationRun(new PinTextRange(0, 0, 1), null, null, default);
        Check.False(emptyRun.HasAny, "空 run 不算标注");
        var highlightRun = new PinTextAnnotationRun(new PinTextRange(0, 0, 1), style, null, default);
        Check.True(highlightRun.HasAny, "只有高亮也算");
        Check.Equal(style, highlightRun.StyleFor(PinTextAnnotationTool.Highlight), "高亮取高亮样式");
        Check.True(highlightRun.StyleFor(PinTextAnnotationTool.Underline) is null, "没有线标注");

        Check.Equal("", new PinRecord().Annotations, "贴图记录默认没有标注");
    }
}
