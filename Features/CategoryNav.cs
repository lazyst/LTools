using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Core.Actions;

namespace LTools.Features;

/// <summary>「分类导航」图标条里的一项：纯数据，不碰 WPF。</summary>
/// <param name="IsAll">是不是第一项「全部分类」（它不是注册表里的分类，没有对应的分类标题）。</param>
/// <param name="Key">分类键（空串 = 「其它」）；全部分类这项放的是 <see cref="CategoryNav.AllCategories"/>。</param>
/// <param name="Label">悬停提示里给人看的名字。</param>
/// <param name="Glyph">Segoe MDL2 Assets 的字形字符。</param>
internal sealed record CategoryNavEntry(bool IsAll, string Key, string Label, string Glyph);

/// <summary>
/// 「分类导航」的固定条目：动作编辑器左栏最左缘那列小图标的数据源。术语见 <c>CONTEXT.md</c>。
/// </summary>
/// <remarks>
/// <para>
/// 第一项恒为「全部分类」——它就是工具箱一直以来的平铺页（收藏 / 最近使用 / 各分类纵向平铺），
/// 点它回顶部；其余项按 <see cref="UnitCategories.Rank"/> 排，与 <c>BuildToolbox</c> 里分类组的
/// 顺序**完全一致**，所以「图标下标 i」和「第 i−1 个分类标题」能对上，定位只要按分类键找标题元素。
/// </para>
/// <para>
/// 条目从注册表里真实出现过的分类派生：新增动作单元 / 新增分类都不用改这张表。字形一律取自
/// <see cref="IconCatalog"/>（Segoe MDL2 Assets，Win10/11 都有），没登记过的分类走
/// <see cref="FallbackGlyph"/>，不会渲染成空框。分类名只是显示层，代码里仍用英文分类键。
/// </para>
/// <para>
/// 图标只做**定位**（把那个分类滚到视图顶部），不切页、不筛选、不落盘；搜索时点图标先清搜索再定位。
/// 这里是纯数据，<c>--selftest</c> 能直接断言顺序 / 字形 / 覆盖，不需要开窗口。
/// </para>
/// </remarks>
internal static class CategoryNav
{
    /// <summary>「全部分类」的显示名，也是这一项在代码里的标识。</summary>
    public const string AllCategories = "全部分类";

    /// <summary>空分类在界面上的显示名（与 <c>BuildToolboxHeader</c> 一致）。</summary>
    public const string OtherCategories = "其它";

    /// <summary>没登记过字形的分类用它：认不出的分类不会变成空框，也不会和已有图标撞脸。</summary>
    private static readonly string FallbackGlyph = IconCatalog.GetGlyph("filter");

    /// <summary>
    /// 分类 → 字形。值全部走 <see cref="IconCatalog.GetGlyph"/>（只认已经证明能渲染的码点），
    /// 刻意不引新字形：项目里「图标看起来是个空框」比「语义不够贴切」糟得多，悬停提示会给全名。
    /// </summary>
    private static readonly Dictionary<string, string> GlyphByCategory = new(StringComparer.Ordinal)
    {
        ["键鼠与输入"] = IconCatalog.GetGlyph("keyboard"),
        ["流程"]       = IconCatalog.GetGlyph("composite"),
        ["文本与数据"] = IconCatalog.GetGlyph("note"),
        ["剪贴板"]     = IconCatalog.GetGlyph("copy"),
        ["文件"]       = IconCatalog.GetGlyph("folder"),
        ["网络"]       = IconCatalog.GetGlyph("globe"),
        ["窗口与程序"] = IconCatalog.GetGlyph("window"),
        ["屏幕"]       = IconCatalog.GetGlyph("image"),
        ["界面"]       = IconCatalog.GetGlyph("app"),
        ["脚本"]       = IconCatalog.GetGlyph("script"),
    };

    /// <summary>图标条的全部条目（全部分类 + 注册表里出现过的分类，排序同左栏分类组）。</summary>
    public static List<CategoryNavEntry> Entries()
    {
        List<string> categories = Categories();
        var list = new List<CategoryNavEntry>(categories.Count + 1)
        {
            new(true, AllCategories, AllCategories, IconCatalog.GetGlyph("home")),
        };
        foreach (string category in categories)
            list.Add(new(false, category, LabelOf(category), GlyphOf(category)));
        return list;
    }

    /// <summary>注册表里出现过的分类，按「分类顺序 + 名字」排（与 <c>BuildToolbox</c> 的 groups 同序）。</summary>
    public static List<string> Categories() => StepRegistry.Default.Units
        .Select(u => u.Spec.Category ?? "")
        .Distinct(StringComparer.Ordinal)
        .OrderBy(UnitCategories.Rank)
        .ThenBy(c => c, StringComparer.CurrentCulture)
        .ToList();

    /// <summary>分类键 → 显示名（空分类叫「其它」）。</summary>
    public static string LabelOf(string category) =>
        category.Length == 0 ? OtherCategories : category;

    /// <summary>分类键 → 字形；没登记过的走兜底字形。</summary>
    public static string GlyphOf(string category) =>
        GlyphByCategory.TryGetValue(category, out string? glyph) ? glyph : FallbackGlyph;

    /// <summary>兜底字形（自检 / 冒烟用，确认它不是空的）。</summary>
    public static string Fallback => FallbackGlyph;

    /// <summary>
    /// 滚动到 <paramref name="offset"/> 时高亮第几个图标：<c>0</c> = 全部分类，<c>i</c> = 第 i−1 个分类。
    /// 纯函数——编辑器把「每个分类标题的 Y」量出来喂给它，<c>--selftest</c> 直接断言定位语义。
    /// </summary>
    /// <param name="anchorTops">每个分类标题相对内容顶部的 Y，与分类组同序。</param>
    /// <param name="offset">滚动偏移。</param>
    /// <param name="scrollableHeight">可滚距离；滚到底算最后一个分类。</param>
    public static int ActiveIndex(IReadOnlyList<double> anchorTops, double offset,
                                  double scrollableHeight, double tolerance = 8)
    {
        if (anchorTops.Count == 0) return 0;

        // 顶部恒为「全部分类」：没有收藏 / 最近使用时第一个分类标题就贴着内容顶部，
        // 不能因为「已经进了第一段」就把默认高亮让出去（默认项要稳）。
        if (offset <= 0.5) return 0;

        int active = 0;
        for (int i = 0; i < anchorTops.Count; i++)
        {
            if (anchorTops[i] - offset <= tolerance) active = i + 1;
            else break;
        }

        // 最后一个分类本来滚不到顶部（留白还没算好）：滚到底就算它。
        if (scrollableHeight > 0 && offset >= scrollableHeight - 1)
            active = anchorTops.Count;

        return Math.Clamp(active, 0, anchorTops.Count);
    }

    /// <summary>
    /// 末尾留白高度：让最后一个分类的标题也能滚到视图顶部。纯函数。
    /// 目标 = 「最后一块顶部 + 一屏 = 总内容高」，所以留白 = 视口高 − 最后一块的高。
    /// </summary>
    /// <param name="viewportHeight">视口高。</param>
    /// <param name="contentWithoutPad">去掉留白后的内容总高。</param>
    /// <param name="lastTop">最后一个分类标题相对内容顶部的 Y。</param>
    public static double PadHeight(double viewportHeight, double contentWithoutPad, double lastTop)
    {
        if (viewportHeight <= 0) return 0;
        double lastBlock = Math.Max(0, contentWithoutPad - lastTop);
        return Math.Max(0, viewportHeight - lastBlock);
    }
}
