using System;
using System.Collections.Generic;
using System.Linq;

namespace LTools.Features.CapturePro;

/// <summary>
/// 悬浮工具条的布局：一叠一叠的功能引用（一行一叠，叠内只露第一个、悬停展开）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>floatingtoolbarsettings.h</c> 的 <c>defaultFloatingToolbarLayout()</c>：
/// 五个位置——截图 / 贴图 / 延时截图 / 一叠「识别 · 翻译 · 长截图 · 录屏」，其余收起来。
/// 上游的位置是动作 id；LTools 里这些入口都是**内置功能引用**（<c>ltools.captureScreen</c> 等），
/// 所以布局里直接存功能引用，面板 / 快捷键 / 悬浮用的是同一套 <c>BindingRef</c>。
/// </para>
/// <para>
/// 纯数据 + 纯函数：认不出的引用（功能下线 / 改名）直接丢，不做兼容；目录里新增、两处都没提到的
/// 功能由 <see cref="Parse"/> 补进最后一叠，所以加新功能不用改配置文件。
/// </para>
/// </remarks>
internal sealed class FloatingToolbarLayout
{
    private readonly List<IReadOnlyList<string>> _positions;
    private readonly HashSet<string> _hidden;

    private FloatingToolbarLayout(List<IReadOnlyList<string>> positions, HashSet<string> hidden)
    {
        _positions = positions;
        _hidden = hidden;
    }

    /// <summary>可以放进工具条的功能（顺序 = 编辑器的默认顺序）。</summary>
    public static readonly IReadOnlyList<string> Catalog = new[]
    {
        "ltools.captureScreen",
        "ltools.captureDelay",
        "common.pinClipboard",
        "ltools.capturePin",
        "ltools.captureRecognize",
        "ltools.captureTranslate",
        "ltools.captureScrolling",
        "common.record",
        "ltools.captureQuickSave",
        "ltools.captureSave",
        "ltools.captureHistory",
        "ltools.globalCanvas",
        "ltools.pinManager",
        "common.screenText",
    };

    /// <summary>每一叠（叠内顺序就是展开后的按钮顺序）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> Positions => _positions;

    /// <summary>被收起来的功能。</summary>
    public IReadOnlyCollection<string> Hidden => _hidden;

    /// <summary>可见功能数。</summary>
    public int VisibleCount => _positions.Sum(position => position.Count);

    /// <summary>
    /// 默认布局（照上游 <c>defaultFloatingToolbarLayout()</c> 映射到本项目的内置功能）。
    /// </summary>
    public static FloatingToolbarLayout Default() => new(
        new List<IReadOnlyList<string>>
        {
            new[] { "ltools.captureScreen" },
            new[] { "common.pinClipboard" },
            new[] { "ltools.captureDelay" },
            new[]
            {
                "ltools.captureRecognize", "ltools.captureTranslate", "ltools.captureScrolling",
                "common.record",
            },
        },
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ltools.capturePin", "ltools.captureQuickSave", "ltools.captureSave",
            "ltools.captureHistory", "ltools.globalCanvas", "ltools.pinManager", "common.screenText",
        });

    /// <summary>功能的中文名（摘要 / 编辑器 / 冒烟断言用）。</summary>
    public static string LabelOf(string reference) => Builtin.BindingRef.Name(reference);

    /// <summary>按落盘内容还原（空 / 全认不出 → 默认布局）。</summary>
    public static FloatingToolbarLayout Parse(IReadOnlyList<string>? positions,
        IReadOnlyList<string>? hidden) => Normalize(positions, hidden, appendMissing: true);

    /// <summary>
    /// 归一：只认目录里的引用、同一条只算一次、空叠丢掉；<paramref name="appendMissing"/> 为真时
    /// 把目录里新增、两处都没提到的功能补进最后一叠。
    /// </summary>
    public static FloatingToolbarLayout Normalize(IReadOnlyList<string>? positions,
        IReadOnlyList<string>? hidden, bool appendMissing)
    {
        var result = new List<IReadOnlyList<string>>();
        var placed = new HashSet<string>(StringComparer.Ordinal);

        if (positions != null)
        {
            foreach (string line in positions)
            {
                var stack = new List<string>();
                foreach (string part in (line ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string id = part.Trim();
                    if (!Catalog.Contains(id, StringComparer.Ordinal) || !placed.Add(id)) continue;
                    stack.Add(id);
                }

                if (stack.Count > 0) result.Add(stack);
            }
        }

        var hiddenSet = new HashSet<string>(StringComparer.Ordinal);
        if (hidden != null)
        {
            foreach (string part in hidden)
            {
                string id = (part ?? "").Trim();
                if (!Catalog.Contains(id, StringComparer.Ordinal) || placed.Contains(id)) continue;
                hiddenSet.Add(id);
            }
        }

        if (result.Count == 0 && hiddenSet.Count == 0) return Default();

        if (appendMissing)
        {
            var missing = Catalog.Where(id => !placed.Contains(id) && !hiddenSet.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                if (result.Count == 0) result.Add(missing);
                else result[^1] = result[^1].Concat(missing).ToList();
            }
        }

        return new FloatingToolbarLayout(result, hiddenSet);
    }

    /// <summary>落盘：一行一叠、叠内逗号分隔。</summary>
    public List<string> ToPositions() => _positions.Select(stack => string.Join(",", stack)).ToList();

    /// <summary>落盘：收起来的功能。</summary>
    public List<string> ToHidden() => _hidden.ToList();

    /// <summary>摘要（设置页 / 冒烟断言用）：一叠用空格、叠与叠之间用 │。</summary>
    public string Describe() =>
        string.Join(" │ ", _positions.Select(stack => string.Join(" ", stack.Select(LabelOf))));
}
