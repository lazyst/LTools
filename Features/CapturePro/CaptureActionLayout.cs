using System;
using System.Collections.Generic;
using System.Linq;

namespace LTools.Features.CapturePro;

/// <summary>
/// 工具条上一个「位置」：要么是一条竖线（分隔），要么是一叠动作（同组动作只露第一个，悬停展开）。
/// </summary>
/// <remarks>
/// 照上游 <c>screenshot_toolbar/action_tools_layout</c> 的 <c>positions</c>：
/// 一个 position 可以是 <c>{separator}</c>，也可以是若干动作键（<c>stackPresentation</c> 把它们
/// 合成一个按钮 + 悬停弹层）。上游默认只有**一条**竖线（在「取消」前面），其余位置直接相邻。
/// </remarks>
internal sealed record ActionPosition(bool IsSeparator, IReadOnlyList<CaptureActionTool> Tools);

/// <summary>
/// 截图工具条右半边那些「完成动作」的布局：一叠一叠的动作、顺序、哪些收起来。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshot_toolbar/action_tools_layout</c>（<c>ScreenshotToolbarLayout</c>：
/// <c>positions</c> + <c>hidden</c>）与 <c>screenshottoolbarlayoutmodel.h</c> 的
/// <c>normalizedLayout</c> / <c>actionDefaultPositions</c>。上游按「工具栏种类」各存一份
/// （截图 / 贴图 / 录屏 / 悬浮 / 绘制），LTools 的贴图编辑与截图编辑是同一个覆盖层，
/// 所以**只存一份**：编辑模式里用不到的按钮（长截图 / 录屏 / 实时贴图 / 更多 / 取消）自动跳过。
/// </para>
/// <para>
/// 纯数据 + 纯函数（<c>--selftest</c> 直接断言）：落盘只有两件事——每一叠是哪些动作键
/// （一行一叠、叠内逗号分隔；<c>-</c> = 一条竖线）+ 被收起来的动作键。目录里**新增**、两处都没
/// 提到的动作由 <see cref="Parse"/> 补进最后一叠，所以加新动作不用改配置文件；认不出的键
/// （动作下线 / 改名）直接丢一边，不做兼容、也不留别名。
/// </para>
/// </remarks>
internal sealed class CaptureActionLayout
{
    private readonly List<ActionPosition> _positions;
    private readonly HashSet<CaptureActionTool> _hidden;

    private CaptureActionLayout(List<ActionPosition> positions, HashSet<CaptureActionTool> hidden)
    {
        _positions = positions;
        _hidden = hidden;
    }

    /// <summary>落盘里表示「竖线」的那一行。</summary>
    public const string SeparatorKey = "-";

    /// <summary>动作目录（上游顺序：识别 → 录屏 → 贴图 → 翻译 → 长截图 → 保存类 → 更多 → 取消 / 复制）。</summary>
    public static readonly IReadOnlyList<CaptureActionTool> Catalog = new[]
    {
        CaptureActionTool.Recognize,
        CaptureActionTool.QrCode,
        CaptureActionTool.Record,
        CaptureActionTool.Pin,
        CaptureActionTool.Live,
        CaptureActionTool.Translate,
        CaptureActionTool.Scroll,
        CaptureActionTool.Print,
        CaptureActionTool.QuickSave,
        CaptureActionTool.SaveAs,
        CaptureActionTool.More,
        CaptureActionTool.Cancel,
        CaptureActionTool.Copy,
    };

    /// <summary>按顺序排出来的「位置」（含竖线）。工具条照它摆按钮。</summary>
    public IReadOnlyList<ActionPosition> Positions => _positions;

    /// <summary>每一叠的动作（组内顺序就是按钮顺序；竖线不算一叠）。</summary>
    public IReadOnlyList<IReadOnlyList<CaptureActionTool>> Groups =>
        _positions.Where(position => !position.IsSeparator).Select(position => position.Tools).ToList();

    /// <summary>被收起来的动作（不占位置，可在编辑器里放回去）。</summary>
    public IReadOnlyCollection<CaptureActionTool> Hidden => _hidden;

    private static ActionPosition Stack(params CaptureActionTool[] tools) => new(false, tools);

    private static ActionPosition Line() => new(true, Array.Empty<CaptureActionTool>());

    /// <summary>
    /// 默认布局（照上游 <c>actionDefaultPositions</c> 映射到本项目的动作集）：
    /// 识别（文字 / 二维码）│ 录屏 │ 贴图 / 实时贴图 │ 识别并翻译 │ 长截图 │
    /// 打印 / 快速保存 / 另存为 │ 更多 ｜ 取消 │ 复制。
    /// </summary>
    /// <remarks>
    /// 上游第一叠是「转 Markdown / HTML / LaTeX / 二维码 / 文档 / 表格」六个转换动作，
    /// 本项目这些都在识别结果窗的页签里，所以第一叠用「识别文字 + 二维码标记」表达；
    /// 只有「取消」前面那条竖线与上游一致，其余位置直接相邻（上游也是这样）。
    /// </remarks>
    public static CaptureActionLayout Default() => new(
        new List<ActionPosition>
        {
            Stack(CaptureActionTool.Recognize, CaptureActionTool.QrCode),
            Stack(CaptureActionTool.Record),
            Stack(CaptureActionTool.Pin, CaptureActionTool.Live),
            Stack(CaptureActionTool.Translate),
            Stack(CaptureActionTool.Scroll),
            Stack(CaptureActionTool.Print, CaptureActionTool.QuickSave, CaptureActionTool.SaveAs),
            Stack(CaptureActionTool.More),
            Line(),
            Stack(CaptureActionTool.Cancel),
            Stack(CaptureActionTool.Copy),
        },
        new HashSet<CaptureActionTool>());

    /// <summary>动作键（落盘 / 上游同名）。</summary>
    public static string KeyOf(CaptureActionTool tool) => tool switch
    {
        CaptureActionTool.Recognize => "recognize",
        CaptureActionTool.QrCode => "qrcode",
        CaptureActionTool.Record => "record-screen",
        CaptureActionTool.Pin => "pin-to-screen",
        CaptureActionTool.Live => "pin-live",
        CaptureActionTool.Translate => "text-translation",
        CaptureActionTool.Scroll => "scrolling-screenshot",
        CaptureActionTool.Print => "print",
        CaptureActionTool.QuickSave => "quick-save",
        CaptureActionTool.SaveAs => "save-as-file",
        CaptureActionTool.More => "more",
        CaptureActionTool.Cancel => "cancel",
        CaptureActionTool.Copy => "copy",
        _ => "recognize",
    };

    /// <summary>键 → 动作；认不出返回 <c>null</c>（调用方丢一边，不做兼容）。</summary>
    public static CaptureActionTool? ToolOf(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        string value = key.Trim();
        foreach (CaptureActionTool tool in Catalog)
            if (string.Equals(KeyOf(tool), value, StringComparison.OrdinalIgnoreCase))
                return tool;
        return null;
    }

    /// <summary>中文名（设置页 / 摘要 / 冒烟断言用）。</summary>
    public static string LabelOf(CaptureActionTool tool) => tool switch
    {
        CaptureActionTool.Recognize => "识别文字",
        CaptureActionTool.QrCode => "显示二维码",
        CaptureActionTool.Record => "录屏",
        CaptureActionTool.Pin => "贴图到屏幕",
        CaptureActionTool.Live => "实时贴图",
        CaptureActionTool.Translate => "识别并翻译",
        CaptureActionTool.Scroll => "长截图",
        CaptureActionTool.Print => "打印",
        CaptureActionTool.QuickSave => "快速保存",
        CaptureActionTool.SaveAs => "另存为",
        CaptureActionTool.More => "更多",
        CaptureActionTool.Cancel => "取消",
        CaptureActionTool.Copy => "复制",
        _ => "识别文字",
    };

    /// <summary>按落盘内容还原（空 / 全认不出 → 默认布局）。</summary>
    public static CaptureActionLayout Parse(IReadOnlyList<string>? lines, IReadOnlyList<string>? hidden) =>
        Normalize(lines, hidden, appendMissing: true);

    /// <summary>
    /// 用已经分好组的动作直接造一份（设置页编辑器保存走这里）：丢掉空组、同一个键只算一次，
    /// 既在位置里又在隐藏里的按「在位置里」算（与 <see cref="Normalize"/> 同口径）。
    /// 竖线跟着「取消」那一叠走（上游唯一那条竖线就在它前面）。
    /// </summary>
    public static CaptureActionLayout FromGroups(IEnumerable<IEnumerable<CaptureActionTool>> groups,
        IEnumerable<CaptureActionTool> hidden)
    {
        var positions = new List<ActionPosition>();
        var positioned = new HashSet<CaptureActionTool>();

        foreach (var input in groups)
        {
            var tools = new List<CaptureActionTool>();
            foreach (var tool in input)
                if (positioned.Add(tool)) tools.Add(tool);
            if (tools.Count == 0) continue;

            if (tools.Contains(CaptureActionTool.Cancel)) positions.Add(Line());
            positions.Add(new ActionPosition(false, tools));
        }

        var hiddenSet = new HashSet<CaptureActionTool>();
        foreach (var tool in hidden)
            if (!positioned.Contains(tool)) hiddenSet.Add(tool);

        if (positions.Count == 0 && hiddenSet.Count == 0) return Default();
        return new CaptureActionLayout(positions, hiddenSet);
    }

    /// <summary>
    /// 归一（照上游 <c>normalizedLayout</c>）：只认目录里的键、同一个键只算一次、空叠丢掉、
    /// 连着 / 收尾的竖线丢掉；<paramref name="appendMissing"/> 为真时把目录里新增、
    /// 两处都没提到的动作补进最后一叠（可见）。
    /// </summary>
    public static CaptureActionLayout Normalize(IReadOnlyList<string>? lines,
        IReadOnlyList<string>? hidden, bool appendMissing)
    {
        var positions = new List<ActionPosition>();
        var positioned = new HashSet<CaptureActionTool>();

        if (lines != null)
        {
            foreach (string line in lines)
            {
                string text = (line ?? "").Trim();
                if (text.Length == 0) continue;

                if (text == SeparatorKey || text.Equals("separator", StringComparison.OrdinalIgnoreCase))
                {
                    // 连着两条 / 开头那条都没有意义
                    if (positions.Count == 0 || positions[^1].IsSeparator) continue;
                    positions.Add(Line());
                    continue;
                }

                var tools = new List<CaptureActionTool>();
                foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    CaptureActionTool? tool = ToolOf(part);
                    if (tool == null || !positioned.Add(tool.Value)) continue;
                    tools.Add(tool.Value);
                }

                if (tools.Count > 0) positions.Add(new ActionPosition(false, tools));
            }
        }

        // 收尾的竖线没有意义
        while (positions.Count > 0 && positions[^1].IsSeparator) positions.RemoveAt(positions.Count - 1);

        var hiddenSet = new HashSet<CaptureActionTool>();
        if (hidden != null)
        {
            foreach (string key in hidden)
            {
                CaptureActionTool? tool = ToolOf(key);
                if (tool == null || positioned.Contains(tool.Value)) continue;
                hiddenSet.Add(tool.Value);
            }
        }

        if (positions.Count == 0 && hiddenSet.Count == 0) return Default();

        if (appendMissing)
        {
            var missing = Catalog.Where(tool => !positioned.Contains(tool) && !hiddenSet.Contains(tool)).ToList();
            if (missing.Count > 0)
            {
                int last = positions.FindLastIndex(position => !position.IsSeparator);
                if (last < 0)
                {
                    positions.Add(new ActionPosition(false, missing));
                }
                else
                {
                    var merged = positions[last].Tools.Concat(missing).ToList();
                    positions[last] = new ActionPosition(false, merged);
                }
            }
        }

        return new CaptureActionLayout(positions, hiddenSet);
    }

    /// <summary>落盘：一行一叠（竖线写 <c>-</c>）、叠内逗号分隔。</summary>
    public List<string> ToLines() =>
        _positions
            .Select(position => position.IsSeparator
                ? SeparatorKey
                : string.Join(",", position.Tools.Select(KeyOf)))
            .ToList();

    /// <summary>落盘：被收起来的动作键。</summary>
    public List<string> ToHidden() => _hidden.Select(KeyOf).ToList();

    /// <summary>可见动作数（摘要用）。</summary>
    public int VisibleCount => _positions.Sum(position => position.Tools.Count);

    /// <summary>一组里的动作名（摘要 / 冒烟断言用）。</summary>
    public string Describe() =>
        string.Join(" │ ", _positions.Select(position => position.IsSeparator
            ? "|"
            : string.Join(" ", position.Tools.Select(LabelOf))));
}

/// <summary>截图工具条上的完成动作（布局里的键与它一一对应）。</summary>
internal enum CaptureActionTool
{
    Recognize,
    QrCode,
    Record,
    Pin,
    Live,
    Translate,
    Scroll,
    Print,
    QuickSave,
    SaveAs,
    More,
    Cancel,
    Copy,
}
