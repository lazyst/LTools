using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.SelfTest;

/// <summary>
/// 分类导航（动作编辑器左缘图标条）：条目 / 字形 / 显示名 + 定位与留白的纯函数。
/// 不碰 WPF、不开窗口——真机几何与滚动仍靠仓库外的 UI 冒烟。
/// </summary>
internal static class CategoryNavSuite
{
    /// <summary>
    /// <c>UnitCategories.Order</c> 的镜像：故意抄一份。官方分类改名 / 新增时这里会红，
    /// 提醒同步 <c>CategoryNav.GlyphByCategory</c> 的字形，别让它静默退化成兜底图标。
    /// </summary>
    private static readonly string[] OfficialCategories =
    {
        "键鼠与输入", "流程", "文本与数据", "剪贴板", "文件",
        "网络", "窗口与程序", "屏幕", "界面", "脚本",
    };

    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("categoryNav", "分类导航条目：全部分类 + 注册表分类，顺序与左栏一致", Entries));
        cases.Add(new("categoryNav", "分类导航字形：全部有效、互不相同、官方分类不退化", Glyphs));
        cases.Add(new("categoryNav", "分类显示名与兜底", LabelsAndFallback));
        cases.Add(new("categoryNav", "定位高亮（顶部 / 容差 / 中段 / 滚到底 / 空锚点）", ActiveIndex));
        cases.Add(new("categoryNav", "末尾留白高度", PadHeight));
    }

    private static void Entries()
    {
        List<CategoryNavEntry> entries = CategoryNav.Entries();
        Check.True(entries.Count >= 2, "至少两项（全部分类 + 至少一个分类）");
        Check.True(entries[0].IsAll, "第一项标了 IsAll");
        Check.Equal(CategoryNav.AllCategories, entries[0].Label, "第一项叫「全部分类」");
        Check.Equal(CategoryNav.AllCategories, entries[0].Key, "第一项的 Key 也是「全部分类」");
        Check.False(entries.Skip(1).Any(e => e.IsAll), "只有一个「全部分类」");

        List<string> cats = CategoryNav.Categories();
        Check.Equal(cats.Count + 1, entries.Count, "条目数 = 分类数 + 1");
        Check.Equal(cats.Count, cats.Distinct(StringComparer.Ordinal).Count(), "分类键不重复");
        for (int i = 0; i < cats.Count; i++)
        {
            Check.Equal(cats[i], entries[i + 1].Key, $"第 {i + 1} 项 Key 与 Categories 一致");
            Check.Equal(CategoryNav.LabelOf(cats[i]), entries[i + 1].Label,
                $"第 {i + 1} 项显示名走 LabelOf");
        }

        // 顺序只在 UnitCategories.Rank 里定，导航别自己再排一次
        List<string> expected = cats.OrderBy(UnitCategories.Rank)
            .ThenBy(c => c, StringComparer.CurrentCulture).ToList();
        for (int i = 0; i < expected.Count; i++)
            Check.Equal(expected[i], cats[i], $"分类顺序第 {i + 1} 项与 UnitCategories.Rank 一致");

        // 注册表里出现过的分类一个都不能漏（新增动作单元不用改导航源码，但断言会盯住）
        foreach (string c in StepRegistry.Default.Units
                     .Select(u => u.Spec.Category ?? "").Distinct(StringComparer.Ordinal))
            Check.True(cats.Contains(c, StringComparer.Ordinal),
                $"注册表分类「{CategoryNav.LabelOf(c)}」在导航里");
    }

    private static void Glyphs()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (CategoryNavEntry e in CategoryNav.Entries())
        {
            Check.True(e.Glyph.Length == 1, $"「{e.Label}」字形是单个字符");
            if (e.Glyph.Length == 1)
                Check.True(e.Glyph[0] is >= '\uE000' and <= '\uF8FF',
                    $"「{e.Label}」字形在 Segoe MDL2 私用区（不会渲染成空框）");
            Check.True(seen.Add(e.Glyph), $"「{e.Label}」字形不和其它项撞脸");
        }

        Check.False(string.IsNullOrEmpty(CategoryNav.Fallback), "兜底字形非空");
        foreach (string category in OfficialCategories)
            Check.NotEqual(CategoryNav.Fallback, CategoryNav.GlyphOf(category),
                $"官方分类「{category}」有专属字形（没退化到兜底）");
    }

    private static void LabelsAndFallback()
    {
        Check.Equal("其它", CategoryNav.LabelOf(""), "空分类叫「其它」");
        Check.Equal("文件", CategoryNav.LabelOf("文件"), "非空分类原样显示");
        Check.Equal(CategoryNav.Fallback, CategoryNav.GlyphOf("一个不存在的分类"),
            "认不出的分类走兜底字形");

        var byKey = CategoryNav.Entries().Where(e => !e.IsAll)
            .ToDictionary(e => e.Key, e => e.Glyph, StringComparer.Ordinal);
        foreach (string category in CategoryNav.Categories())
            Check.Equal(CategoryNav.GlyphOf(category), byKey[category],
                $"「{CategoryNav.LabelOf(category)}」条目字形与 GlyphOf 一致");
    }

    private static void ActiveIndex()
    {
        double[] tops = { 100, 200, 300 };

        Check.Equal(0, CategoryNav.ActiveIndex(tops, 0, 500), "顶部（还没进第一个分类）= 全部分类");
        Check.Equal(0, CategoryNav.ActiveIndex(tops, -50, 500), "负偏移也算全部分类");
        Check.Equal(0, CategoryNav.ActiveIndex(new double[] { 8, 200, 300 }, 0, 500),
            "第一个分类紧贴内容顶部时，顶部仍是「全部分类」（默认项不让出去）");
        Check.Equal(1, CategoryNav.ActiveIndex(new double[] { 8, 200, 300 }, 6, 500),
            "往下滚一点点才把高亮交给第一个分类");
        Check.Equal(1, CategoryNav.ActiveIndex(tops, 95, 500), "容差内算第一个分类");
        Check.Equal(1, CategoryNav.ActiveIndex(tops, 100, 500), "正好到第一个分类顶部");
        Check.Equal(2, CategoryNav.ActiveIndex(tops, 250, 500), "中段 = 第二个分类");
        Check.Equal(3, CategoryNav.ActiveIndex(tops, 300, 500), "到第三个分类顶部");
        Check.Equal(3, CategoryNav.ActiveIndex(tops, 499, 500), "滚到底算最后一个分类");
        Check.Equal(3, CategoryNav.ActiveIndex(tops, 10000, 500), "偏移超界夹到最后一个分类");
        Check.Equal(0, CategoryNav.ActiveIndex(Array.Empty<double>(), 0, 0),
            "没有锚点（空注册表）= 全部分类");

        // 容差是参数，不是写死的常数
        Check.Equal(0, CategoryNav.ActiveIndex(tops, 99, 500, tolerance: 0), "容差 0 时差 1px 不算到达");
        Check.Equal(1, CategoryNav.ActiveIndex(tops, 100, 500, tolerance: 0), "容差 0 时正好到达算命中");
    }

    private static void PadHeight()
    {
        Near(200, CategoryNav.PadHeight(400, 1000, 800), "最后一块 200 高 → 补 200");
        Near(0, CategoryNav.PadHeight(400, 1000, 100), "最后一块比一屏还高 → 不留白");
        Near(350, CategoryNav.PadHeight(400, 900, 850), "最后一块 50 高 → 补 350");
        Near(0, CategoryNav.PadHeight(0, 1000, 800), "视口为 0（窗口还没量好）→ 不留白");
        Check.True(CategoryNav.PadHeight(400, 100, 200) >= 0, "lastTop 超界也不返回负数");

        // 补完后的总高正好 = 「最后一块顶部 + 一屏」，那个分类才滚得到顶部
        double pad = CategoryNav.PadHeight(400, 1000, 800);
        Near(1200, 1000 + pad, "补完后总高 = 最后一块顶部(800) + 一屏(400)");
    }

    private static void Near(double expected, double actual, string what) =>
        Check.True(Math.Abs(expected - actual) < 0.001,
            $"{what}（期望 {expected}，实际 {actual}）");
}
