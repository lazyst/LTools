using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using LTools.Config;

namespace LTools.Features.CapturePro;

/// <summary>快速文字标注的四种工具（数值与上游 <c>ScreenshotTextAnnotationTool</c> 一致）。</summary>
internal enum PinTextAnnotationTool
{
    Highlight = 0,
    WavyUnderline = 1,
    Underline = 2,
    Strikethrough = 3,
}

/// <summary>源文本里的一段：行号 + 行内 [<see cref="Start"/>, <see cref="End"/>)（字符下标）。</summary>
internal readonly record struct PinTextRange(int SourceLine, int Start, int End);

/// <summary>
/// 一种标注的样式（颜色 / 线宽 / 线型 / 笔触风格），与上游
/// <c>ScreenshotTextAnnotationStyle</c> 一一对应。
/// </summary>
internal readonly record struct PinTextAnnotationStyle(
    Color Color, double Width, StrokeKind StrokeStyle, StrokeRendering RenderingStyle)
{
    /// <summary>高亮默认色（上游 <c>rectangleHighlight.fill</c>）。</summary>
    public static readonly Color HighlightDefault = Color.FromRgb(0xFF, 0x78, 0x75);

    /// <summary>三种线标注的默认色（上游 <c>rectangle.stroke</c>）。</summary>
    public static readonly Color StrokeDefault = Color.FromRgb(0xF5, 0x22, 0x2D);

    /// <summary>默认线宽（上游 <c>rectangle.strokeWidth</c>）。</summary>
    public const double DefaultWidth = 2;

    /// <summary>线宽上限（上游 <c>validStyle</c> 的 72）。</summary>
    public const double MaxWidth = 72;

    /// <summary>某个工具的默认样式：高亮用高亮色，其余用描边色；宽 2、实线、干净。</summary>
    public static PinTextAnnotationStyle Default(PinTextAnnotationTool tool) => new(
        tool == PinTextAnnotationTool.Highlight ? HighlightDefault : StrokeDefault,
        DefaultWidth, StrokeKind.Solid, StrokeRendering.Architect);

    /// <summary>能不能拿去画（与上游 <c>validStyle</c> 同口径；WPF 的 Color 总是有效）。</summary>
    public bool IsValid =>
        Width > 0 && Width <= MaxWidth &&
        (int)StrokeStyle is >= 0 and <= 2 && (int)RenderingStyle is >= 0 and <= 2;

    /// <summary>把外部输入按上游规则归一：越界 / 缺项一律回落该工具的默认值。</summary>
    public static PinTextAnnotationStyle Normalize(Color color, double? width, int? stroke,
        int? rendering, PinTextAnnotationTool tool)
    {
        PinTextAnnotationStyle fallback = Default(tool);
        double normalizedWidth = width is > 0 and <= MaxWidth ? width.Value : fallback.Width;
        var normalizedStroke = stroke is >= 0 and <= 2 ? (StrokeKind)stroke.Value : fallback.StrokeStyle;
        var normalizedRendering = rendering is >= 0 and <= 2
            ? (StrokeRendering)rendering.Value
            : fallback.RenderingStyle;
        return new PinTextAnnotationStyle(color, normalizedWidth, normalizedStroke, normalizedRendering);
    }
}

/// <summary>
/// 一段标注：源文本区间 + 可选的高亮 + 可选的一条线标注（工具 + 样式）。
/// 与上游 <c>ScreenshotTextAnnotationRun</c> 同构——高亮与线标注互相独立，可同时存在。
/// </summary>
internal sealed record PinTextAnnotationRun(
    PinTextRange Range,
    PinTextAnnotationStyle? Highlight,
    PinTextAnnotationTool? LineTool,
    PinTextAnnotationStyle LineStyle)
{
    /// <summary>这段在这一种工具下的样式（没有就是 <c>null</c>）。</summary>
    public PinTextAnnotationStyle? StyleFor(PinTextAnnotationTool tool) =>
        tool == PinTextAnnotationTool.Highlight
            ? Highlight
            : LineTool == tool
                ? LineStyle
                : null;

    /// <summary>至少有一种标注（空 run 不落盘）。</summary>
    public bool HasAny => Highlight != null || LineTool != null;
}

/// <summary>
/// 贴图上「快速文字标注」的全部内容（照上游 <c>ScreenshotTextAnnotationDocument</c>）：
/// 源标识 + 指纹 + 一串按源行坐标存的 run。
/// </summary>
/// <remarks>
/// <para>
/// **坐标属于源文本、不属于画布**：缩放 / 平移 / 导出都不改变 run，绘制时按行框换算，
/// 所以同一份标注在文字层、隐形文字层、导出的 png 里位置完全一致。
/// </para>
/// <para>
/// **指纹变了就整体作废**：源文本换了（重新识别 / 换图）时 <see cref="BindSource"/> 会清空 run，
/// 贴图从历史贴回时由 <see cref="ValidForSource"/> 再兜一层——宁可不画，也不把标注画到别的字上。
/// </para>
/// <para>
/// 序列化是 JSON（<see cref="ToPayload"/> / <see cref="Restore"/>），存进
/// <c>pins/pins.json</c> 的 <see cref="PinRecord.Annotations"/>；坏数据一律当空，不抛。
/// </para>
/// </remarks>
internal sealed class PinTextAnnotationDocument
{
    /// <summary>唯一支持的源（LTools 的贴图只有 OCR 这一种文字来源）。</summary>
    public const string SourceOcr = "ocr";

    /// <summary>载荷版本（上游 1）。</summary>
    public const int PayloadVersion = 1;

    /// <summary>载荷上限（上游 32 MB；正常标注远小于它）。</summary>
    public const int MaxPayload = 32 * 1024 * 1024;

    /// <summary>样式属性位（与上游 <c>ScreenshotTextAnnotationStyleProperty</c> 一致）。</summary>
    public const int PropertyColor = 1;
    public const int PropertyWidth = 2;
    public const int PropertyStrokeStyle = 4;
    public const int PropertyRenderingStyle = 8;
    public const int PropertyAll = 15;

    private readonly List<PinTextAnnotationRun> _runs = new();

    /// <summary>源标识（<see cref="SourceOcr"/>）。</summary>
    public string Source { get; private set; } = SourceOcr;

    /// <summary>源指纹（<see cref="FingerprintOf"/> 的十六进制串）。</summary>
    public string Fingerprint { get; private set; } = "";

    /// <summary>内容版本（每改一次加一，界面据此决定要不要重画）。</summary>
    public int Revision { get; private set; }

    /// <summary>没有任何标注。</summary>
    public bool Empty => _runs.Count == 0;

    /// <summary>全部 run（按行、按起点升序）。</summary>
    public IReadOnlyList<PinTextAnnotationRun> Runs => _runs;

    // ============ 源 ============

    /// <summary>
    /// 绑定源（照上游 <c>bindSource</c>）：源或指纹变了就丢掉全部 run。
    /// 同一份源重复绑定是空操作（不会白加版本号）。
    /// </summary>
    public void BindSource(string source, string fingerprint)
    {
        source = string.IsNullOrEmpty(source) ? SourceOcr : source;
        fingerprint ??= "";
        if (Source == source && Fingerprint == fingerprint) return;

        Source = source;
        Fingerprint = fingerprint;
        _runs.Clear();
        Revision++;
    }

    /// <summary>清空标注（源文本被改 / 换图）。</summary>
    public void Clear()
    {
        if (_runs.Count == 0) return;
        _runs.Clear();
        Revision++;
    }

    /// <summary>run 是否还落在源文本里（行数 + 每行长度），照上游 <c>validForSource</c>。</summary>
    public bool ValidForSource(IReadOnlyList<int> sourceLengths)
    {
        foreach (var run in _runs)
        {
            if (run.Range.SourceLine >= sourceLengths.Count ||
                run.Range.End > sourceLengths[run.Range.SourceLine])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>算一份源文本的指纹：行号 + 文本 + 行框（源图像素）。</summary>
    public static string FingerprintOf(IReadOnlyList<OcrTextLine> lines, IReadOnlyList<string> texts)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            OcrTextLine line = lines[i];
            string text = i < texts.Count ? texts[i] : line.Text;
            builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\u001f')
                .Append(text).Append('\u001f')
                .Append(line.Box.X.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(line.Box.Y.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(line.Box.Width.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(line.Box.Height.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ============ 查询 ============

    /// <summary>选区在这一种工具下有没有样式；<paramref name="requireFull"/> 要求整段都被覆盖。</summary>
    public bool HasStyle(IReadOnlyList<PinTextRange> selection, PinTextAnnotationTool tool,
        bool requireFull = false)
    {
        List<PinTextRange> ranges = NormalizeSelection(selection);
        if (ranges.Count == 0) return false;

        int index = 0;
        foreach (var range in ranges)
        {
            while (index < _runs.Count && (_runs[index].Range.SourceLine < range.SourceLine ||
                                          (_runs[index].Range.SourceLine == range.SourceLine &&
                                           _runs[index].Range.End <= range.Start)))
            {
                index++;
            }

            int covered = range.Start;
            for (int i = index; i < _runs.Count; i++)
            {
                PinTextAnnotationRun run = _runs[i];
                if (run.Range.SourceLine != range.SourceLine || run.Range.Start >= range.End) break;
                if (run.StyleFor(tool) is null) continue;

                if (!requireFull) return true;
                if (run.Range.Start > covered) return false;
                covered = Math.Max(covered, run.Range.End);
            }

            if (requireFull && covered < range.End) return false;
        }

        return requireFull;
    }

    /// <summary>
    /// 选区里这一种工具的样式（照上游 <c>selectedStyle</c>）：没有 = <c>null</c>；
    /// 多处样式不同时 <paramref name="mixed"/> 为真、<paramref name="mixedProperties"/> 指出差在哪几项。
    /// </summary>
    public PinTextAnnotationStyle? SelectedStyle(IReadOnlyList<PinTextRange> selection,
        PinTextAnnotationTool tool, out bool mixed, out int mixedProperties)
    {
        mixed = false;
        mixedProperties = 0;

        PinTextAnnotationStyle? result = null;
        List<PinTextRange> ranges = NormalizeSelection(selection);
        int index = 0;
        foreach (var range in ranges)
        {
            while (index < _runs.Count && (_runs[index].Range.SourceLine < range.SourceLine ||
                                          (_runs[index].Range.SourceLine == range.SourceLine &&
                                           _runs[index].Range.End <= range.Start)))
            {
                index++;
            }

            for (int i = index; i < _runs.Count; i++)
            {
                PinTextAnnotationRun run = _runs[i];
                if (run.Range.SourceLine != range.SourceLine || run.Range.Start >= range.End) break;
                if (run.StyleFor(tool) is not { } style) continue;

                if (result is { } existing)
                {
                    if (!existing.Equals(style)) mixed = true;
                    if (existing.Color != style.Color) mixedProperties |= PropertyColor;
                    if (Math.Abs(existing.Width - style.Width) > 0.0005) mixedProperties |= PropertyWidth;
                    if (existing.StrokeStyle != style.StrokeStyle) mixedProperties |= PropertyStrokeStyle;
                    if (existing.RenderingStyle != style.RenderingStyle)
                        mixedProperties |= PropertyRenderingStyle;
                }
                else
                {
                    result = style;
                }
            }
        }

        return result;
    }

    // ============ 修改 ============

    /// <summary>切换（照上游 <c>toggle</c>）：整段已有就整段去掉，否则整段加上。</summary>
    public bool Toggle(IReadOnlyList<PinTextRange> selection, PinTextAnnotationTool tool,
        PinTextAnnotationStyle style) =>
        Mutate(selection, tool, style, remove: HasStyle(selection, tool), existingOnly: false, 0);

    /// <summary>只改选区里已有标注的样式（照上游 <c>updateStyle</c>，不新增标注）。</summary>
    public bool UpdateStyle(IReadOnlyList<PinTextRange> selection, PinTextAnnotationTool tool,
        PinTextAnnotationStyle style, int properties) =>
        Mutate(selection, tool, style, remove: false, existingOnly: true, properties);

    private bool Mutate(IReadOnlyList<PinTextRange> selection, PinTextAnnotationTool tool,
        PinTextAnnotationStyle style, bool remove, bool existingOnly, int properties)
    {
        if ((int)tool is < 0 or > 3 || !style.IsValid) return false;

        List<PinTextRange> ranges = NormalizeSelection(selection);
        if (ranges.Count == 0) return false;

        // 把「所有 run 的边界」与「选区的边界」合起来切成互不重叠的小段
        var boundaries = new SortedDictionary<int, List<int>>();
        void AddBoundary(int line, int position)
        {
            if (!boundaries.TryGetValue(line, out List<int>? list))
                boundaries[line] = list = new List<int>();
            list.Add(position);
        }

        foreach (var run in _runs)
        {
            AddBoundary(run.Range.SourceLine, run.Range.Start);
            AddBoundary(run.Range.SourceLine, run.Range.End);
        }

        foreach (var range in ranges)
        {
            AddBoundary(range.SourceLine, range.Start);
            AddBoundary(range.SourceLine, range.End);
        }

        var result = new List<PinTextAnnotationRun>();
        int runIndex = 0, rangeIndex = 0;

        foreach (var (line, raw) in boundaries)
        {
            List<int> positions = raw.Distinct().OrderBy(position => position).ToList();
            for (int i = 1; i < positions.Count; i++)
            {
                var piece = new PinTextRange(line, positions[i - 1], positions[i]);

                while (runIndex < _runs.Count &&
                       (_runs[runIndex].Range.SourceLine < piece.SourceLine ||
                        (_runs[runIndex].Range.SourceLine == piece.SourceLine &&
                         _runs[runIndex].Range.End <= piece.Start)))
                {
                    runIndex++;
                }

                while (rangeIndex < ranges.Count &&
                       (ranges[rangeIndex].SourceLine < piece.SourceLine ||
                        (ranges[rangeIndex].SourceLine == piece.SourceLine &&
                         ranges[rangeIndex].End <= piece.Start)))
                {
                    rangeIndex++;
                }

                PinTextAnnotationStyle? highlight = null;
                PinTextAnnotationTool? lineTool = null;
                var lineStyle = default(PinTextAnnotationStyle);
                if (runIndex < _runs.Count && _runs[runIndex].Range.SourceLine == piece.SourceLine &&
                    _runs[runIndex].Range.Start <= piece.Start)
                {
                    highlight = _runs[runIndex].Highlight;
                    lineTool = _runs[runIndex].LineTool;
                    lineStyle = _runs[runIndex].LineStyle;
                }

                bool selected = rangeIndex < ranges.Count &&
                                ranges[rangeIndex].SourceLine == piece.SourceLine &&
                                ranges[rangeIndex].Start <= piece.Start;
                PinTextAnnotationStyle? current = StyleOf(highlight, lineTool, lineStyle, tool);
                if (selected && (!existingOnly || current is not null))
                {
                    PinTextAnnotationStyle applied = style;
                    if (existingOnly)
                    {
                        applied = current!.Value;
                        if ((properties & PropertyColor) != 0)
                            applied = applied with { Color = style.Color };
                        if ((properties & PropertyWidth) != 0)
                            applied = applied with { Width = style.Width };
                        if ((properties & PropertyStrokeStyle) != 0)
                            applied = applied with { StrokeStyle = style.StrokeStyle };
                        if ((properties & PropertyRenderingStyle) != 0)
                            applied = applied with { RenderingStyle = style.RenderingStyle };
                    }

                    if (tool == PinTextAnnotationTool.Highlight)
                    {
                        highlight = remove ? null : applied;
                    }
                    else if (remove)
                    {
                        lineTool = null;
                    }
                    else
                    {
                        lineTool = tool;
                        lineStyle = applied;
                    }
                }

                Append(result, new PinTextAnnotationRun(piece, highlight, lineTool, lineStyle));
            }
        }

        if (SameRuns(result, _runs)) return false;

        _runs.Clear();
        _runs.AddRange(result);
        Revision++;
        return true;
    }

    private static PinTextAnnotationStyle? StyleOf(PinTextAnnotationStyle? highlight,
        PinTextAnnotationTool? lineTool, PinTextAnnotationStyle lineStyle, PinTextAnnotationTool tool) =>
        tool == PinTextAnnotationTool.Highlight
            ? highlight
            : lineTool == tool
                ? lineStyle
                : null;

    /// <summary>选区归一（照上游 <c>normalized</c>）：丢掉空段、按行与起点排序、同行的相邻段合并。</summary>
    public static List<PinTextRange> NormalizeSelection(IReadOnlyList<PinTextRange> ranges)
    {
        var sorted = ranges
            .Where(range => range.SourceLine >= 0 && range.Start >= 0 && range.End > range.Start)
            .OrderBy(range => range.SourceLine)
            .ThenBy(range => range.Start)
            .ToList();

        var result = new List<PinTextRange>();
        foreach (var range in sorted)
        {
            if (result.Count > 0 && result[^1].SourceLine == range.SourceLine &&
                range.Start <= result[^1].End)
            {
                result[^1] = result[^1] with { End = Math.Max(range.End, result[^1].End) };
            }
            else
            {
                result.Add(range);
            }
        }

        return result;
    }

    /// <summary>相邻且样式完全一样的两段并成一段（照上游 <c>append</c>）。</summary>
    private static void Append(List<PinTextAnnotationRun> result, PinTextAnnotationRun run)
    {
        if (!run.HasAny) return;

        if (result.Count > 0)
        {
            PinTextAnnotationRun last = result[^1];
            bool sameLine = last.Range.SourceLine == run.Range.SourceLine;
            bool adjacent = last.Range.End == run.Range.Start;
            bool sameHighlight = Nullable.Equals(last.Highlight, run.Highlight);
            bool sameLineMark = last.LineTool == run.LineTool &&
                                (last.LineTool is null || last.LineStyle.Equals(run.LineStyle));
            if (sameLine && adjacent && sameHighlight && sameLineMark)
            {
                result[^1] = last with { Range = last.Range with { End = run.Range.End } };
                return;
            }
        }

        result.Add(run);
    }

    private static bool SameRuns(IReadOnlyList<PinTextAnnotationRun> left,
        IReadOnlyList<PinTextAnnotationRun> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!left[i].Equals(right[i]))
                return false;
        return true;
    }

    // ============ 序列化 ============

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>序列化成 JSON 文本（空文档返回空串）。</summary>
    public string ToPayload()
    {
        if (_runs.Count == 0) return "";

        var payload = new AnnotationPayload
        {
            Version = PayloadVersion,
            Source = Source,
            Fingerprint = Fingerprint,
            Runs = _runs.Select(run => new RunPayload
            {
                Line = run.Range.SourceLine,
                Start = run.Range.Start,
                End = run.Range.End,
                Highlight = run.Highlight is { } highlight ? StylePayload.From(highlight) : null,
                LineTool = run.LineTool is { } tool ? (int)tool : null,
                LineStyle = run.LineTool is null ? null : StylePayload.From(run.LineStyle),
            }).ToList(),
        };

        string json = JsonSerializer.Serialize(payload, Json);
        return json.Length <= MaxPayload ? json : "";
    }

    /// <summary>
    /// 从 JSON 文本恢复（照上游 <c>restore</c>）：空串 = 清空并成功；
    /// 坏数据 / 版本不对 / 越界 / 乱序一律返回 <c>false</c>，且不改动现有内容。
    /// </summary>
    public bool Restore(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            Clear();
            return true;
        }

        if (payload.Length > MaxPayload) return false;

        AnnotationPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AnnotationPayload>(payload, Json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed == null || parsed.Version != PayloadVersion) return false;
        if (parsed.Source != SourceOcr) return false;
        if ((parsed.Fingerprint ?? "").Length > 128) return false;

        var runs = new List<PinTextAnnotationRun>();
        int previousLine = -1;
        int previousEnd = -1;

        foreach (RunPayload? item in parsed.Runs ?? new List<RunPayload>())
        {
            if (item == null) return false;
            if (item.Line < 0 || item.Start < 0 || item.End <= item.Start) return false;
            if (item.Line < previousLine ||
                (item.Line == previousLine && item.Start < previousEnd))
            {
                return false;
            }

            PinTextAnnotationStyle? highlight = null;
            if (item.Highlight != null)
            {
                highlight = item.Highlight.ToStyle(PinTextAnnotationTool.Highlight);
                if (highlight is not { } value || !value.IsValid) return false;
            }

            PinTextAnnotationTool? lineTool = null;
            var lineStyle = default(PinTextAnnotationStyle);
            if (item.LineTool is { } rawTool)
            {
                if (rawTool is < 1 or > 3) return false;
                lineTool = (PinTextAnnotationTool)rawTool;
                if (item.LineStyle == null) return false;
                PinTextAnnotationStyle? parsedStyle = item.LineStyle.ToStyle(lineTool.Value);
                if (parsedStyle is not { } value || !value.IsValid) return false;
                lineStyle = value;
            }
            else if (item.LineStyle != null)
            {
                return false;
            }

            if (highlight == null && lineTool == null) return false;

            previousLine = item.Line;
            previousEnd = item.End;
            Append(runs, new PinTextAnnotationRun(new PinTextRange(item.Line, item.Start, item.End),
                highlight, lineTool, lineStyle));
        }

        _runs.Clear();
        _runs.AddRange(runs);
        Source = parsed.Source;
        Fingerprint = parsed.Fingerprint ?? "";
        Revision++;
        return true;
    }

    private sealed class AnnotationPayload
    {
        public int Version { get; set; }
        public string? Source { get; set; }
        public string? Fingerprint { get; set; }
        public List<RunPayload>? Runs { get; set; }
    }

    private sealed class RunPayload
    {
        public int Line { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
        public StylePayload? Highlight { get; set; }
        public int? LineTool { get; set; }
        public StylePayload? LineStyle { get; set; }
    }

    private sealed class StylePayload
    {
        public string? Color { get; set; }
        public double Width { get; set; }
        public int Stroke { get; set; }
        public int Rendering { get; set; }

        public static StylePayload From(PinTextAnnotationStyle style) => new()
        {
            Color = ColorPresets.Format(style.Color),
            Width = style.Width,
            Stroke = (int)style.StrokeStyle,
            Rendering = (int)style.RenderingStyle,
        };

        public PinTextAnnotationStyle? ToStyle(PinTextAnnotationTool tool)
        {
            if (!ColorPresets.TryParse(Color, out Color color)) return null;
            return PinTextAnnotationStyle.Normalize(color, Width, Stroke, Rendering, tool);
        }
    }
}

/// <summary>
/// 标注的纯几何（照上游 <c>screenshottextannotationgeometry.cpp</c> +
/// <c>screenshottextannotationrenderer.cpp</c>）：行框 → 中心线 / 振幅 / 种子。
/// </summary>
internal static class PinTextAnnotationGeometry
{
    /// <summary>波浪线的振幅（上游 <c>max(1, width * 0.75)</c>）。</summary>
    public static double Amplitude(double width) => Math.Max(1, width * 0.75);

    /// <summary>波浪线的半波长（上游 <c>max(3, width * 2)</c>）。</summary>
    public static double HalfWave(double width) => Math.Max(3, width * 2);

    /// <summary>线的中心 y：删除线在行中，下划线 / 波浪线在行底再往下半个线宽（波浪再让一个振幅）。</summary>
    public static double LineY(Rect rect, PinTextAnnotationTool tool, double width) =>
        tool == PinTextAnnotationTool.Strikethrough
            ? rect.Top + rect.Height / 2
            : rect.Bottom + width / 2 +
              (tool == PinTextAnnotationTool.WavyUnderline ? Amplitude(width) : 0);

    /// <summary>抖动种子：同一段 + 同一工具永远得到同一个种子（重绘 / 导出 / 缩放都不变）。</summary>
    public static uint Seed(PinTextRange range, PinTextAnnotationTool tool)
    {
        unchecked
        {
            uint seed = (uint)range.SourceLine * 0x9e3779b9U;
            seed ^= (uint)range.Start * 0x85ebca6bU;
            seed ^= (uint)range.End * 0xc2b2ae35U;
            return seed ^ (uint)tool;
        }
    }

    /// <summary>中心线：直线一段，波浪线按半波长交替的三次贝塞尔。</summary>
    public static List<InkSegment> Centerline(Rect rect, PinTextAnnotationTool tool, double width)
    {
        var segments = new List<InkSegment>();
        double y = LineY(rect, tool, width);

        if (tool != PinTextAnnotationTool.WavyUnderline)
        {
            segments.Add(InkSegment.Line(new Point(rect.Left, y), new Point(rect.Right, y)));
            return segments;
        }

        double amplitude = Amplitude(width);
        double halfWave = HalfWave(width);
        double x = rect.Left;
        double direction = -1;
        while (x < rect.Right)
        {
            double next = Math.Min(rect.Right, x + halfWave);
            if (!(next > x)) break;
            double span = next - x;
            segments.Add(InkSegment.Cubic(
                new Point(x, y),
                new Point(x + span / 3, y + direction * amplitude * 4 / 3),
                new Point(x + span * 2 / 3, y + direction * amplitude * 4 / 3),
                new Point(next, y)));
            direction = -direction;
            x = next;
        }

        return segments;
    }
}
