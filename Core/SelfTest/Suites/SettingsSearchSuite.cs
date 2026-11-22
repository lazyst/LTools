using System.Collections.Generic;
using System.Linq;
using LTools.Core;

namespace LTools.Core.SelfTest;

/// <summary>
/// 设置搜索的匹配规则（<see cref="SettingsSearch"/>）：规范化 / 打分档位 / 多词 AND / 去重 / 截断。
/// 不碰窗口、不建索引——索引抓取靠真机点一下搜索框（见 <c>Views/ConfigHelper.Search*.cs</c>）。
/// </summary>
internal static class SettingsSearchSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("settingsSearch", "规范化：大小写 / 全角空格 / 连续空白", Normalize));
        cases.Add(new("settingsSearch", "打分档位：标签开头 > 标签包含 > 别名 > 分组 > 页面", Scoring));
        cases.Add(new("settingsSearch", "多词按 AND 收窄，分数取平均", MultiTerm));
        cases.Add(new("settingsSearch", "空查询不返回任何结果", EmptyQuery));
        cases.Add(new("settingsSearch", "同页同名去重、结果上限、稳定排序", OrderingAndLimit));
        cases.Add(new("settingsSearch", "中文子串与英文大小写都不敏感", Substring));
        cases.Add(new("settingsSearch", "别名只补在会搜错的条目上", Aliases));
    }

    private static SettingsEntry Entry(string tag, string page, string group, string label, int order,
        string keywords = "") => new(tag, page, group, label, order, keywords);

    private static List<SettingsEntry> Sample() => new()
    {
        Entry("general", "通用", "工具开关", "启用 CapsLock 修饰键", 0),
        Entry("general", "通用", "工具开关", "屏幕边缘音量", 1),
        Entry("hotkeys", "快捷键", "", "新建绑定", 2),
        Entry("macros-settings", "键鼠宏 · 设置", "录制", "启用键鼠宏", 3),
        Entry("macros-settings", "键鼠宏 · 设置", "录制", "停止运行热键", 4),
        Entry("macros-settings", "键鼠宏 · 设置", "录制", "录制 / 停止热键", 5),
        Entry("macros-settings", "键鼠宏 · 设置", "回放默认值", "开始前倒计时", 6),
        Entry("capture-ocr", "截图 Pro · 文字识别", "识别模型", "识别语言", 7, "ocr"),
        Entry("triggers", "自动触发", "触发条件", "停止条件", 8),
    };

    private static void Normalize()
    {
        Check.Equal("停止 热键", SettingsSearch.Normalize("  停止\u3000\u3000热键  "), "全角空格与首尾空白归一");
        Check.Equal("abc def", SettingsSearch.Normalize("ABC\tDEF"), "英文转小写、制表符变空格");
        Check.Equal("", SettingsSearch.Normalize(" \u3000 "), "只有空白等于空");
        Check.Equal("", SettingsSearch.Normalize(null), "null 等于空");
    }

    private static void Scoring()
    {
        var entries = Sample();
        // 「倒计时」只在标签里（开头）：100
        var hit = SettingsSearch.Match(entries, "倒计时").Single().Entry;
        Check.Equal("开始前倒计时", hit.Label, "标签开头命中");

        // 「热键」在三个标签里都出现（停止运行热键 / 录制 / 停止热键 / 快捷键页面名）
        var hits = SettingsSearch.Match(entries, "热键");
        Check.True(hits.Count >= 2, "多个条目命中");
        Check.True(hits[0].Score >= hits[^1].Score, "按分数降序");

        // 页面名只算最低档：搜「快捷键」能命中，但分数低于标签命中
        var pageHit = SettingsSearch.Match(entries, "快捷键").First();
        Check.Equal("hotkeys", pageHit.Entry.Tag, "页面名可搜");
        Check.Equal(45, pageHit.Score, "页面匹配固定 45");

        // 分组标题 55 分
        var groupHit = SettingsSearch.Match(entries, "回放默认值").Single();
        Check.Equal("开始前倒计时", groupHit.Entry.Label, "分组标题命中该组条目");
        Check.Equal(55, groupHit.Score, "分组匹配固定 55");

        // 别名 70 分（Keywords 只在没有更精确的命中时才用）
        var aliasEntries = new List<SettingsEntry>
        {
            Entry("x", "某页", "", "停止运行热键", 0, "abort stop"),
        };
        Check.Equal(70, SettingsSearch.Match(aliasEntries, "abort").Single().Score, "别名 70 分");
    }

    private static void MultiTerm()
    {
        var entries = Sample();
        var only = SettingsSearch.Match(entries, "停止").Select(h => h.Entry.Label).ToList();
        var narrowed = SettingsSearch.Match(entries, "宏 停止").Select(h => h.Entry.Label).ToList();

        Check.True(only.Count >= 2, "单词命中多条");
        Check.True(narrowed.Count < only.Count, "加上第二个词会收窄");
        Check.True(narrowed.All(l => l.Contains("停止")), "收窄后每条都含第二个词");
        Check.True(narrowed.All(l => l is "停止运行热键" or "录制 / 停止热键"), "两次都命中的只剩停止相关项");

        // 有一个词落空 = 整条不命中（AND，不是 OR）
        Check.Equal(0, SettingsSearch.Match(entries, "停止 不存在的词").Count, "任一词落空就不命中");
    }

    private static void EmptyQuery()
    {
        Check.Equal(0, SettingsSearch.Match(Sample(), "").Count, "空查询");
        Check.Equal(0, SettingsSearch.Match(Sample(), null).Count, "null 查询");
        Check.Equal(0, SettingsSearch.Match(Sample(), "   ").Count, "只有空白");
    }

    private static void OrderingAndLimit()
    {
        var entries = new List<SettingsEntry>
        {
            Entry("a", "通用", "开关", "启用", 0),
            Entry("a", "通用", "开关", "启用", 1),      // 同页同名：去重
            Entry("b", "快捷键", "开关", "启用绑定", 2),
        };
        var hits = SettingsSearch.Match(entries, "启用");
        Check.Equal(2, hits.Count, "同页同名只留一条");
        Check.Equal(0, hits[0].Entry.Order, "分数相同按页面顺序");

        var many = Enumerable.Range(0, 60)
            .Select(i => Entry("p", "页", "", "开关 " + i, i))
            .ToList();
        Check.Equal(SettingsSearch.MaxResults, SettingsSearch.Match(many, "开关").Count, "默认上限 40 条");
        Check.Equal(7, SettingsSearch.Match(many, "开关", 7).Count, "可指定更小的上限");
    }

    private static void Substring()
    {
        var entries = Sample();
        Check.Equal("停止运行热键", SettingsSearch.Match(entries, "运行热").First().Entry.Label, "中文中间子串");
        Check.Equal(1, SettingsSearch.Match(entries, "CAPSLOCK").Count, "英文大小写不敏感");
        Check.Equal("识别语言", SettingsSearch.Match(entries, "ocr").Single().Entry.Label, "页面名英文也行（截图 Pro · 文字识别不含 ocr）");
    }

    private static void Aliases()
    {
        Check.Contains(SettingsSearch.AliasesFor("停止运行热键"), "暂停", "停止键补「暂停 / 中止」等口头说法");
        Check.Contains(SettingsSearch.AliasesFor("左键双击"), "托盘", "托盘设置补「托盘」");
        Check.Equal("", SettingsSearch.AliasesFor("启用 CapsLock 修饰键"), "界面词已经能搜到的不补别名");

        var entries = new List<SettingsEntry>
        {
            Entry("macros-settings", "键鼠宏 · 设置", "录制", "停止运行热键", 0,
                SettingsSearch.AliasesFor("停止运行热键")),
        };
        Check.Equal("停止运行热键", SettingsSearch.Match(entries, "暂停").Single().Entry.Label, "别名能搜到");
        Check.Equal(70, SettingsSearch.Match(entries, "暂停").Single().Score, "别名 70 分（低于标签命中）");
    }
}
