using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Core;

namespace LTools.Views;

/// <summary>
/// 设置搜索的索引：从页面自己的逻辑树里抓「分组标题 + 可交互控件 + 它旁边那行标签」。
/// </summary>
/// <remarks>
/// <para>
/// **不维护手工清单**：新增一行设置、改一个标签，搜索自动跟着变。索引只在第一次聚焦搜索框 /
/// 第一次输入时建一次；遍历的是逻辑树，所以页面没显示过（<c>Visibility=Collapsed</c>）也能索引。
/// 6 个惰性页在这一步才实例化（索引本身也是它们第一次被建出来的时机），之后一直复用。
/// </para>
/// <para>
/// 跳过 <see cref="ItemsControl"/>（宏列表 / 快捷键表 / 触发规则 / 采集历史这些是用户数据，
/// 不是设置项），只索引 CheckBox / RadioButton / ComboBox / TextBox / Button / Expander。
/// 跳转与高亮在 <c>ConfigHelper.Search.cs</c>，匹配规则在 <c>Core/SettingsSearch.cs</c>。
/// </para>
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>已建好的索引；<c>null</c> = 还没建。控件不会在设置窗口生命周期里被替换，所以不失效。</summary>
    private List<SettingsEntry>? _searchEntries;

    /// <summary>条目 → 真正要滚动 / 高亮的控件。与 <see cref="_searchEntries"/> 同生共死。</summary>
    private readonly Dictionary<SettingsEntry, FrameworkElement> _searchTargets = new();

    /// <summary>建索引时缓存一次：遍历几千个 TextBlock，每个都 TryFindResource 会白等。</summary>
    private Style? _searchSectionStyle;
    private Style? _searchHintStyle;

    private void EnsureSearchIndex()
    {
        if (_searchEntries != null) return;

        _searchSectionStyle = TryFindResource("SectionLabel") as Style;
        _searchHintStyle = TryFindResource("HintLabel") as Style;
        var collect = new SearchCollect();
        try
        {
            // 页面标题从导航读（含父项前缀，如「键鼠宏 · 设置」），不在 Core 里再维护一份中文名表
            Dictionary<string, string> titles = BuildNavTitles();
            foreach ((string tag, FrameworkElement root) in EnumerateSearchRoots())
            {
                collect.Tag = tag;
                collect.Page = titles.TryGetValue(tag, out string? title) ? title : tag;
                collect.Group = "";
                CollectSearchItems(root, collect);
            }
        }
        catch (Exception ex)
        {
            // 索引失败不该让搜索框不能用：保留已经抓到的条目，其余靠下次打开窗口重建
            AppLog.Warn("SettingsSearch", "建立设置搜索索引时出错：" + ex.Message);
        }

        _searchEntries = collect.Entries;
        _searchTargets.Clear();
        foreach (var pair in collect.Targets) _searchTargets[pair.Key] = pair.Value;
    }

    private sealed class SearchCollect
    {
        public readonly List<SettingsEntry> Entries = new();
        public readonly Dictionary<SettingsEntry, FrameworkElement> Targets = new();
        public string Tag = "";
        public string Page = "";
        public string Group = "";
        public int Order;
    }

    /// <summary>所有设置页的逻辑树根，顺序与左栏导航一致（结果并列时按页面顺序）。</summary>
    private IEnumerable<(string Tag, FrameworkElement Root)> EnumerateSearchRoots()
    {
        yield return ("general", Page_General);
        yield return ("actions", Page_Actions);
        yield return ("subactions", Page_SubActions);
        // 「热键」三个同层子页（预设组是惰性视图，另两个是内联页）
        yield return ("hotkeys-groups", Page_Hotkeys);
        yield return ("hotkeys-macro", Page_HotkeysMacro);
        yield return ("hotkeys-capture", Page_HotkeysCapture);
        // 「文字识别」是一级页（从截图 Pro 抽出来），其余八个是截图 Pro 的子页
        yield return ("ocr", Page_CaptureOcr);
        // 截图 Pro 的八个同层子页（与 SetPageVisibility 一一对应）
        yield return ("capture-general", Page_CaptureGeneral);
        yield return ("capture-behavior", Page_CaptureBehavior);
        yield return ("capture-drawing", Page_CaptureDrawing);
        yield return ("capture-overlay", Page_CaptureOverlay);
        yield return ("capture-pin", Page_CapturePin);
        yield return ("capture-record", Page_CaptureRecord);
        yield return ("capture-history", Page_CaptureHistory);
        yield return ("capture-output", Page_Output);
        // 键鼠宏 / 面板管理 / AI 各自有同层子页，由控件自己给出各自的根
        foreach (var root in Page_Macros.SearchRoots()) yield return root;
        yield return ("triggers", Page_Triggers);
        foreach (var root in Page_SuperPanel.SearchRoots()) yield return root;
        yield return ("terminals", Page_Terminals);
        yield return ("logs-settings", Page_LogsSettings);
        yield return ("logs-recent", Page_LogsRecent);
        yield return ("logs-history", Page_LogsHistory);
        foreach (var root in Page_Ai.SearchRoots()) yield return root;
        yield return ("backup", Page_Backup);
    }

    /// <summary>导航 tag → 显示名；二级子项带父项前缀（「截图 Pro · 常规」），搜「截图」也能收窄到该组。</summary>
    private Dictionary<string, string> BuildNavTitles()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string lastTop = "";
        foreach (object child in NavList.Children)
        {
            if (child is RadioButton rb && rb.Tag is string tag)
            {
                lastTop = NavItemText(rb);
                map[tag] = lastTop;
            }
            else if (child is Border border && border.Child is StackPanel sub)
            {
                foreach (RadioButton subItem in sub.Children.OfType<RadioButton>())
                {
                    if (subItem.Tag is string subTag)
                        map[subTag] = lastTop + " · " + NavItemText(subItem);
                }
            }
        }
        return map;
    }

    /// <summary>导航项的文字：一级父项是 StackPanel（文字 + MDL2 箭头），子项是字符串。</summary>
    private static string NavItemText(RadioButton item) => item.Content switch
    {
        string s => s.Trim(),
        TextBlock block => block.Text.Trim(),
        Panel panel => panel.Children.OfType<TextBlock>()
            .Select(t => t.Text?.Trim() ?? "")
            .FirstOrDefault(t => t.Length > 0 && !IsGlyphText(t)) ?? "",
        _ => "",
    };

    /// <summary>MDL2 的私有区字形（箭头等），不是给用户看的文字。</summary>
    private static bool IsGlyphText(string text) => text.Length > 0 && text[0] is >= '\uE000' and <= '\uF8FF';

    private void CollectSearchItems(DependencyObject node, SearchCollect collect)
    {
        // TextBlock：StackPanel 里的 SectionLabel 是卡片 / 分组标题，之后的条目都归在它下面；
        // 同一行 Grid 里的 SectionLabel 是**行标签**（「左键双击」+ 下拉就是这么排的），
        // 不能当成分组标题，否则下拉 / 输入框只挂在「左键双击」这个假分组下。
        if (node is TextBlock block)
        {
            if (IsSectionLabel(block) && LogicalTreeHelper.GetParent(block) is not Grid)
            {
                string title = CleanLabel(block.Text);
                if (title.Length is > 0 and <= 24) collect.Group = title;
            }
            return;
        }

        // 可交互控件自己就是一条；列 / 表里的内容不索引（那是用户数据，不是设置项）
        if (node is FrameworkElement element && TryDescribe(element, out string label))
        {
            var entry = new SettingsEntry(collect.Tag, collect.Page, collect.Group, label, collect.Order++,
                SettingsSearch.AliasesFor(label));
            collect.Entries.Add(entry);
            collect.Targets[entry] = element;
        }
        if (node is ItemsControl) return;

        // 子树里可能有自己的分组标题：遍历完把外层分组还回去，别让兄弟卡片继承内层标题
        string savedGroup = collect.Group;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject dependencyObject)
                CollectSearchItems(dependencyObject, collect);
        }
        collect.Group = savedGroup;
    }

    /// <summary>把控件翻译成「可搜索的一条」；不认识的控件返回 false。</summary>
    private bool TryDescribe(FrameworkElement element, out string label)
    {
        string? text = element switch
        {
            // 「启用 CapsLock…」这类文案就在控件里
            CheckBox check => TextOf(check.Content),
            RadioButton radio => TextOf(radio.Content),
            // 下拉 / 输入框本身没有文字：取同一行左边的标签，退而求其次用 ToolTip
            ComboBox combo => NearbyLabel(combo) is { Length: > 0 } near ? near : TextOf(combo.ToolTip),
            TextBox box => NearbyLabel(box),
            Button button => TextOf(button.Content) is { Length: > 0 } content ? content : TextOf(button.ToolTip),
            Expander expander => TextOf(expander.Header),
            _ => null,
        };

        label = text == null ? "" : CleanLabel(text);
        return label.Length is > 0 and <= 40;
    }

    /// <summary>控件所在容器里、排在它前面的第一个短文本（XAML 里「标签 + 控件」普遍这么排）。</summary>
    private string NearbyLabel(FrameworkElement element)
    {
        if (LogicalTreeHelper.GetParent(element) is not DependencyObject parent) return "";
        List<DependencyObject> siblings = LogicalTreeHelper.GetChildren(parent)
            .OfType<DependencyObject>()
            .ToList();
        int index = siblings.IndexOf(element);
        for (int i = index - 1; i >= 0; i--)
        {
            if (siblings[i] is TextBlock block && IsPlainLabel(block))
                return block.Text;
        }
        return "";
    }

    /// <summary>
    /// 行标签：短文本、不是灰色说明。**卡片分组标题（SectionLabel）也要算**——设置页里
    /// 「标签 + 控件」同一行时标签就是用 SectionLabel 画的（如「左键双击」+ 下拉），
    /// 排除它会让所有下拉 / 输入框都索引不到文字。
    /// </summary>
    private bool IsPlainLabel(TextBlock block)
    {
        string text = block.Text?.Trim() ?? "";
        if (text.Length is 0 or > 24) return false;
        return !IsHintLabel(block);
    }

    private bool IsSectionLabel(TextBlock block) =>
        _searchSectionStyle != null && ReferenceEquals(block.Style, _searchSectionStyle);

    private bool IsHintLabel(TextBlock block) =>
        _searchHintStyle != null && ReferenceEquals(block.Style, _searchHintStyle);

    private static string TextOf(object? content) => content switch
    {
        null => "",
        string text => text,
        TextBlock block => block.Text,
        AccessText access => access.Text,
        Panel panel => string.Join(" ", panel.Children.OfType<TextBlock>().Select(t => t.Text)),
        ContentControl control => TextOf(control.Content),
        _ => content.ToString() ?? "",
    };

    /// <summary>去掉换行 / 连续空白 / MDL2 字形（按钮上的图标不该进搜索词）。</summary>
    private static string CleanLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = new System.Text.StringBuilder(text.Length);
        bool space = false;
        foreach (char ch in text)
        {
            if (ch is >= '\uE000' and <= '\uF8FF')
            {
                space = chars.Length > 0;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                space = chars.Length > 0;
                continue;
            }
            if (space)
            {
                chars.Append(' ');
                space = false;
            }
            chars.Append(ch);
        }
        return chars.ToString().Trim();
    }
}
