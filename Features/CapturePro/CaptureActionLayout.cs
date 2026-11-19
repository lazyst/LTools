using System;
using System.Collections.Generic;
using System.Linq;

namespace LTools.Features.CapturePro;

/// <summary>
/// 截图工具条右半边那些「完成动作」的布局：一组一组的按钮、顺序、哪些收起来。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshot_toolbar/action_tools_layout</c>（<c>ScreenshotToolbarLayout</c>：
/// <c>positions</c> + <c>hidden</c>）与 <c>screenshottoolbarlayoutmodel.h</c> 的
/// <c>normalizedLayout</c> / <c>defaultPositions(ActionTools)</c>。上游按「工具栏种类」各存一份
/// （截图 / 贴图 / 录屏 / 悬浮 / 绘制），LTools 的贴图编辑与截图编辑是同一个覆盖层，
/// 所以**只存一份**：编辑模式里用不到的按钮（长截图 / 录屏 / 实时贴图 / 更多 / 取消）自动跳过，
/// 剩下的顺序正好与上游 <c>PinnedActionTools</c> 的默认顺序一致。
/// </para>
/// <para>
/// 纯数据 + 纯函数（<c>--selftest</c> 直接断言）：落盘只有两件事——每一组是哪些动作键
/// （一行一组、组内逗号分隔）+ 被收起来的动作键。目录里**新增**、两处都没提到的动作由
/// <see cref="Parse"/> 按默认分组补回来，所以加新动作不用改配置文件；认不出的键（动作下线 /
/// 改名）直接丢一边，不做兼容、也不留别名。
/// </para>
/// </remarks>
internal sealed class CaptureActionLayout
{
    private readonly List<List<CaptureActionTool>> _groups;
    private readonly HashSet<CaptureActionTool> _hidden;

    private CaptureActionLayout(List<List<CaptureActionTool>> groups, HashSet<CaptureActionTool> hidden)
    {
        _groups = groups;
        _hidden = hidden;
    }

    /// <summary>动作目录（上游顺序：识别 → 录屏 → 贴图 → 长截图 → 保存类 → 更多 → 取消 / 复制）。</summary>
    public static readonly IReadOnlyList<CaptureActionTool> Catalog = new[]
    {
        CaptureActionTool.Recognize,
        CaptureActionTool.QrCode,
        CaptureActionTool.Record,
        CaptureActionTool.Pin,
        CaptureActionTool.Live,
        CaptureActionTool.Scroll,
        CaptureActionTool.Print,
        CaptureActionTool.QuickSave,
        CaptureActionTool.SaveAs,
        CaptureActionTool.More,
        CaptureActionTool.Cancel,
        CaptureActionTool.Copy,
    };

    /// <summary>每一组的动作（组内顺序就是按钮顺序）。</summary>
    public IReadOnlyList<IReadOnlyList<CaptureActionTool>> Groups => _groups;

    /// <summary>被收起来的动作（不占位置，可在编辑器里放回去）。</summary>
    public IReadOnlyCollection<CaptureActionTool> Hidden => _hidden;

    /// <summary>
    /// 默认布局（照上游 <c>actionDefaultPositions</c> 映射到本项目的动作集）：
    /// 识别 / 二维码 │ 录屏 │ 贴图 / 实时贴图 │ 长截图 │ 打印 / 快速保存 / 另存为 │ 更多 │ 取消 / 复制。
    /// </summary>
    public static CaptureActionLayout Default() => new(
        new List<List<CaptureActionTool>>
        {
            new() { CaptureActionTool.Recognize, CaptureActionTool.QrCode },
            new() { CaptureActionTool.Record },
            new() { CaptureActionTool.Pin, CaptureActionTool.Live },
            new() { CaptureActionTool.Scroll },
            new() { CaptureActionTool.Print, CaptureActionTool.QuickSave, CaptureActionTool.SaveAs },
            new() { CaptureActionTool.More },
            new() { CaptureActionTool.Cancel, CaptureActionTool.Copy },
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
    /// </summary>
    public static CaptureActionLayout FromGroups(IEnumerable<IEnumerable<CaptureActionTool>> groups,
        IEnumerable<CaptureActionTool> hidden)
    {
        var result = new List<List<CaptureActionTool>>();
        var positioned = new HashSet<CaptureActionTool>();

        foreach (var input in groups)
        {
            var group = new List<CaptureActionTool>();
            foreach (var tool in input)
                if (positioned.Add(tool)) group.Add(tool);
            if (group.Count > 0) result.Add(group);
        }

        var hiddenSet = new HashSet<CaptureActionTool>();
        foreach (var tool in hidden)
            if (!positioned.Contains(tool)) hiddenSet.Add(tool);

        if (result.Count == 0 && hiddenSet.Count == 0) return Default();
        return new CaptureActionLayout(result, hiddenSet);
    }

    /// <summary>
    /// 归一（照上游 <c>normalizedLayout</c>）：只认目录里的键、同一个键只算一次、空组丢掉、
    /// 藏在「位置」里的不算隐藏；<paramref name="appendMissing"/> 为真时把目录里新增、
    /// 两处都没提到的动作按默认分组补回来（可见）。
    /// </summary>
    public static CaptureActionLayout Normalize(IReadOnlyList<string>? lines,
        IReadOnlyList<string>? hidden, bool appendMissing)
    {
        var groups = new List<List<CaptureActionTool>>();
        var positioned = new HashSet<CaptureActionTool>();

        if (lines != null)
        {
            foreach (string line in lines)
            {
                var group = new List<CaptureActionTool>();
                foreach (string part in (line ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    CaptureActionTool? tool = ToolOf(part);
                    if (tool == null || !positioned.Add(tool.Value)) continue;
                    group.Add(tool.Value);
                }

                if (group.Count > 0) groups.Add(group);
            }
        }

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

        if (groups.Count == 0 && hiddenSet.Count == 0) return Default();

        if (appendMissing)
        {
            foreach (var defaultGroup in Default()._groups)
            {
                var missing = defaultGroup
                    .Where(tool => !positioned.Contains(tool) && !hiddenSet.Contains(tool))
                    .ToList();
                if (missing.Count > 0) groups.Add(missing);
            }
        }

        return new CaptureActionLayout(groups, hiddenSet);
    }

    /// <summary>落盘：一行一组、组内逗号分隔。</summary>
    public List<string> ToLines() =>
        _groups.Select(group => string.Join(",", group.Select(KeyOf))).ToList();

    /// <summary>落盘：被收起来的动作键。</summary>
    public List<string> ToHidden() => _hidden.Select(KeyOf).ToList();

    /// <summary>可见动作数（摘要用）。</summary>
    public int VisibleCount => _groups.Sum(group => group.Count);

    /// <summary>一组里的动作名（摘要 / 冒烟断言用）。</summary>
    public string Describe() =>
        string.Join(" │ ", _groups.Select(group => string.Join(" ", group.Select(LabelOf))));
}

/// <summary>截图工具条上的完成动作（布局里的键与它一一对应）。</summary>
internal enum CaptureActionTool
{
    Recognize,
    QrCode,
    Record,
    Pin,
    Live,
    Scroll,
    Print,
    QuickSave,
    SaveAs,
    More,
    Cancel,
    Copy,
}
