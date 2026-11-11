using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LTools.Config;
using LTools.Features;
using LTools.Features.Builtin;

namespace LTools.Views;

/// <summary>
/// 编辑一条自动触发规则（见 <c>docs/AUTO_TRIGGER.md</c> §9 / §13.6）。
/// 三种触发方式共用一个对话框：事件类型的参数表按 §13.5 动态生成，新增事件类型只改一张表。
/// 能通过 GUI 点选的一律给选择入口（系统窗口 / 进程 / 目录 / 常用值下拉 / 多选勾选），
/// 正则、说明这类本质自由的才留纯文本；下拉都可编辑，手填永远兜底。
/// </summary>
public partial class TriggerRuleEditDialog : Window
{
    /// <summary>动态参数行用哪种输入控件。</summary>
    private enum ParamMode
    {
        Text, Bool, WindowTitle, WindowClass, Process, Folder, FileFilter, Events, Formats,
    }

    private sealed record ParamSpec(
        string Key, string Label, bool IsBool, string Default, string Hint,
        ParamMode Mode = ParamMode.Text);

    /// <summary>三个事件类型的参数表单（§13.5）。新增事件类型只改这张表，不动 XAML。</summary>
    private static readonly Dictionary<string, ParamSpec[]> EventParams = new(StringComparer.OrdinalIgnoreCase)
    {
        ["window.foreground"] = new[]
        {
            new ParamSpec("title", "窗口标题", false, "", "包含 / regex: 前缀；分号多值", ParamMode.WindowTitle),
            new ParamSpec("process", "进程名", false, "", "如 chrome.exe", ParamMode.Process),
            new ParamSpec("class", "窗口类名", false, "", "精确 / 通配", ParamMode.WindowClass),
            new ParamSpec("prevTitle", "上一个窗口标题", false, "", "用于「从 A 切到 B」；上个真实应用（跳过桌面 / 任务栏 / LTools 自己）", ParamMode.WindowTitle),
            new ParamSpec("prevProcess", "上一个进程名", false, "", "进程真的换了才触发；如 explorer.exe", ParamMode.Process),
            new ParamSpec("excludeOwn", "排除自己的窗口", true, "true", "LTools 自己的窗口不触发（也不污染「上一个进程」）"),
        },
        ["clipboard.change"] = new[]
        {
            new ParamSpec("text", "文本内容", false, "", "包含 / regex: 前缀；非文本格式不参与"),
            new ParamSpec("formats", "格式", false, "", "全不选 = 全部格式", ParamMode.Formats),
            new ParamSpec("ignoreOwnWrites", "忽略自己写入", true, "true", "LTools 写剪贴板不触发"),
        },
        ["file.change"] = new[]
        {
            new ParamSpec("path", "监听目录", false, "", "必填；点「浏览」选目录", ParamMode.Folder),
            new ParamSpec("filter", "文件名筛选", false, "*.*", "从下拉选常用筛选，也可手填", ParamMode.FileFilter),
            new ParamSpec("events", "事件类型", false, "created;changed", "勾选要监听的变化", ParamMode.Events),
            new ParamSpec("includeSubdirectories", "包含子目录", true, "false", ""),
            new ParamSpec("pathMatch", "路径匹配", false, "", "包含 / regex: 前缀"),
        },
    };

    private static readonly Dictionary<string, string> EventVars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["window.foreground"] = "可用变量：triggerWindowTitle / triggerWindowProcess / triggerWindowClass / triggerWindowHandle / triggerPrevWindowTitle / triggerPrevWindowProcess",
        ["clipboard.change"] = "可用变量：triggerClipboardText / triggerClipboardFormats",
        ["file.change"] = "可用变量：triggerFilePath / triggerFileName / triggerFileChange / triggerFileOldPath",
    };

    /// <summary>每行参数配一个「怎么读当前值」的委托，复合控件（勾选组 / 下拉）也能统一收集。</summary>
    private readonly List<(ParamSpec Spec, Func<string> Read)> _paramReads = new();
    /// <summary>剪贴板「文本内容」行右侧的 ⚠ 徽章，只在「非文本时跳过」语义出现时点亮；详情挂 ToolTip。</summary>
    private Border? _textBadge;
    private readonly TriggerRule _rule;
    private string _actionRef = "";

    private TriggerRuleEditDialog(TriggerRule rule)
    {
        InitializeComponent();
        _rule = rule;
        _actionRef = rule.ActionRef;

        NoteBox.Text = rule.Note;
        EnabledBox.IsChecked = rule.Enabled;

        SelectKind(rule.Kind);
        // 间隔按分钟展示（支持小数，避免「打开→不改→确定」把非整分钟的值静默改掉）。
        IntervalBox.Text = rule.IntervalMs is > 0
            ? (rule.IntervalMs.Value / 60000.0).ToString(CultureInfo.InvariantCulture)
            : "";
        DailyBox.Text = rule.DailyAt ?? "";
        CronBox.Text = rule.Cron ?? "";
        DelaySecondsBox.Text = rule.DelaySeconds.ToString(CultureInfo.InvariantCulture);
        OnlyFirstStartBox.IsChecked = rule.OnlyFirstStartToday;
        OnlyOnAutoStartBox.IsChecked = rule.OnlyOnAutoStart;

        ActionBox.Text = ActionName(_actionRef);

        var filter = rule.Scope ?? new AppFilter();
        ProcessBox.Text = string.Join(Environment.NewLine, filter.Processes);
        TitleBox.Text = string.Join(Environment.NewLine, filter.Titles);
        ClassBox.Text = string.Join(Environment.NewLine, filter.Classes);
        ScopeExpander.IsExpanded = filter is { IsEmpty: false };

        DelayMsBox.Text = rule.DelayMs.ToString(CultureInfo.InvariantCulture);
        DebounceBox.Text = rule.DebounceMs.ToString(CultureInfo.InvariantCulture);
        ThrottleBox.Text = rule.ThrottleMs.ToString(CultureInfo.InvariantCulture);
        ActionParamBox.Text = rule.ActionParam ?? "";
        SkipFurtherBox.IsChecked = rule.SkipFurther;
        AdvExpander.IsExpanded = rule.DelayMs > 0 || rule.DebounceMs > 0 || rule.ThrottleMs > 0 || rule.SkipFurther;

        ApplyKindVisibility();
        SelectEventType(rule.EventType);
        RebuildParamRows();
    }

    /// <summary>录入结果（仅 <see cref="Window.DialogResult"/> 为 true 时有效）。</summary>
    public TriggerRule Result { get; private set; } = new();

    /// <summary>编辑一条规则（会被克隆，取消时不影响原对象）。</summary>
    public static TriggerRule? Show(Window? owner, TriggerRule rule)
    {
        var dlg = new TriggerRuleEditDialog(rule);
        if (owner != null) { dlg.Owner = owner; dlg.Topmost = owner.Topmost; }
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    // ============ 触发方式 ============

    private void SelectKind(TriggerKind kind)
    {
        string tag = kind switch
        {
            TriggerKind.startup => "startup",
            TriggerKind.@event => "event",
            _ => "timer",
        };
        foreach (ComboBoxItem item in KindBox.Items)
            if ((item.Tag as string) == tag) { KindBox.SelectedItem = item; return; }
        KindBox.SelectedIndex = 0;
    }

    private string SelectedKind() => (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "timer";

    private void Kind_Changed(object sender, SelectionChangedEventArgs e) => ApplyKindVisibility();

    private void ApplyKindVisibility()
    {
        string tag = SelectedKind();
        bool isEvent = tag == "event";
        // 右栏标题跟着触发方式变，分栏后一眼看出右栏在配什么。
        RightTitle.Text = tag switch
        {
            "startup" => "启动后设置",
            "event" => "事件设置",
            _ => "定时设置",
        };
        TimerPanel.Visibility = tag == "timer" ? Visibility.Visible : Visibility.Collapsed;
        StartupPanel.Visibility = tag == "startup" ? Visibility.Visible : Visibility.Collapsed;
        EventPanel.Visibility = isEvent ? Visibility.Visible : Visibility.Collapsed;
        // ActionParam / SkipFurther 只在事件规则里写入 Result（见 Ok_Click），非事件时收起，别让用户填了不生效。
        ActionParamPanel.Visibility = isEvent ? Visibility.Visible : Visibility.Collapsed;
        SkipFurtherBox.Visibility = isEvent ? Visibility.Visible : Visibility.Collapsed;
    }

    private TriggerKind KindFromTag() => SelectedKind() switch
    {
        "startup" => TriggerKind.startup,
        "event" => TriggerKind.@event,
        _ => TriggerKind.timer,
    };

    // ============ 事件类型 / 参数表单 ============

    private void SelectEventType(string? type)
    {
        foreach (ComboBoxItem item in EventTypeBox.Items)
            if (string.Equals(item.Tag as string, type, StringComparison.OrdinalIgnoreCase))
            { EventTypeBox.SelectedItem = item; return; }
        EventTypeBox.SelectedIndex = 0;
    }

    private string SelectedEventType() =>
        (EventTypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "window.foreground";

    private void EventType_Changed(object sender, SelectionChangedEventArgs e) => RebuildParamRows();

    /// <summary>按当前事件类型重建参数行（§13.5）。已有 Params 值回填，没填的用默认值。</summary>
    private void RebuildParamRows()
    {
        _paramReads.Clear();
        ParamsHost.Children.Clear();
        _textBadge = null;
        string type = SelectedEventType();
        if (!EventParams.TryGetValue(type, out var specs)) { AvailableVarsText.Text = ""; return; }

        var existing = string.Equals(_rule.EventType, type, StringComparison.OrdinalIgnoreCase)
            ? _rule.Params : null;

        foreach (var spec in specs)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = spec.Label,
                Style = (Style)FindResource("SectionLabel"),
                FontSize = (double)FindResource("FontSizeHint"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = spec.Hint,
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            string raw = existing != null && existing.TryGetValue(spec.Key, out var saved) ? saved : spec.Default;
            FrameworkElement input;
            Func<string> read;

            if (spec.IsBool)
            {
                var cb = new CheckBox
                {
                    IsChecked = bool.TryParse(raw, out bool b) ? b : spec.Default == "true",
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = spec.Hint,
                };
                input = cb;
                read = () => cb.IsChecked == true ? "true" : "false";
            }
            else if (spec.Mode is ParamMode.Events or ParamMode.Formats)
            {
                (string Key, string Label)[] items = spec.Mode == ParamMode.Events
                    ? new[] { ("created", "新建"), ("changed", "修改"), ("deleted", "删除"), ("renamed", "重命名") }
                    : new[] { ("text", "文本"), ("image", "图片"), ("files", "文件") };
                var wrap = new WrapPanel();
                var selected = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var checks = new List<(string Key, CheckBox Box)>();
                foreach (var (key, text) in items)
                {
                    var c = new CheckBox
                    {
                        Content = text,
                        Margin = new Thickness(0, 0, 10, 2),
                        VerticalAlignment = VerticalAlignment.Center,
                        IsChecked = selected.Contains(key),
                        ToolTip = spec.Hint,
                    };
                    wrap.Children.Add(c);
                    checks.Add((key, c));
                }
                input = wrap;
                read = () => string.Join(";", checks.Where(x => x.Box.IsChecked == true).Select(x => x.Key));
            }
            else if (spec.Mode == ParamMode.FileFilter)
            {
                var combo = new ComboBox
                {
                    IsEditable = true,
                    MinHeight = 28,
                    Text = raw,
                    ToolTip = spec.Hint,
                };
                foreach (string f in new[] { "*.*", "*.txt", "*.png;*.jpg;*.jpeg", "*.pdf", "*.docx;*.xlsx", "*.zip" })
                    combo.Items.Add(f);
                input = combo;
                read = () => combo.Text.Trim();
            }
            else
            {
                var tb = new TextBox { MinHeight = 28, Text = raw, ToolTip = spec.Hint };
                read = () => tb.Text.Trim();
                if (spec.Mode == ParamMode.Text)
                {
                    input = tb;
                }
                else
                {
                    var btn = new Button
                    {
                        Content = spec.Mode switch
                        {
                            ParamMode.WindowTitle or ParamMode.WindowClass => "选窗口",
                            ParamMode.Process => "选进程",
                            ParamMode.Folder => "浏览…",
                            _ => "选择…",
                        },
                        Style = (Style)FindResource("BtnGhost"),
                        MinWidth = 0,
                        Padding = new Thickness(8, 2, 8, 2),
                        FontSize = (double)FindResource("FontSizeHint"),
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = PickToolTip(spec.Mode),
                    };
                    btn.Click += (_, _) => PickParamValue(spec.Mode, tb);
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(tb, 0); row.Children.Add(tb);
                    Grid.SetColumn(btn, 1); row.Children.Add(btn);
                    input = row;
                }
            }

            // 剪贴板「文本内容」行：输入框右侧挂一个 ⚠ 徽章（悬停看详情），有问题才显示。
            // 包装必须在挂载到 grid 之前做，否则 input 会有两个逻辑父（WPF 直接抛）。
            if (string.Equals(type, "clipboard.change", StringComparison.OrdinalIgnoreCase)
                && string.Equals(spec.Key, "text", StringComparison.OrdinalIgnoreCase))
            {
                var badge = new Border
                {
                    Background = (Brush)FindResource("DangerBgBrush"),
                    BorderBrush = (Brush)FindResource("DangerBorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = (CornerRadius)FindResource("CornerSm"),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed,
                    Child = new TextBlock
                    {
                        Text = "⚠ 纯图片时不参与",
                        Foreground = (Brush)FindResource("DangerBrush"),
                        FontSize = (double)FindResource("FontSizeHint"),
                    },
                };
                DockPanel.SetDock(badge, Dock.Right);
                var dock = new DockPanel { LastChildFill = true };
                dock.Children.Add(badge);
                dock.Children.Add(input);   // 最后添加 = 填充剩余宽度
                input = dock;
                _textBadge = badge;
            }

            Grid.SetColumn(input, 1);
            grid.Children.Add(input);
            HookRefresh(input);
            ParamsHost.Children.Add(grid);
            _paramReads.Add((spec, read));
        }

        AvailableVarsText.Text = EventVars.TryGetValue(type, out string? vars)
            ? vars + " · triggerEventType / triggerEventTime / triggerParam"
            : "";
        RefreshSemantics();
    }

    // ============ 实时语义预览（避免把「文本内容 + 格式」误读成 AND） ============

    /// <summary>给参数控件挂「值一变就刷新说明」的钩子（逻辑树递归，勾选组 / 带按钮的 Grid 都能覆盖）。</summary>
    private void HookRefresh(DependencyObject node)
    {
        switch (node)
        {
            case TextBox tb:
                tb.TextChanged += (_, _) => RefreshSemantics();
                break;
            case CheckBox cb:
                cb.Checked += (_, _) => RefreshSemantics();
                cb.Unchecked += (_, _) => RefreshSemantics();
                break;
            case ComboBox combo:
                combo.SelectionChanged += (_, _) => RefreshSemantics();
                combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                    new TextChangedEventHandler((_, _) => RefreshSemantics()));
                break;
        }
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject d) HookRefresh(d);
    }

    private Dictionary<string, string> CurrentParamValues()
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (spec, read) in _paramReads) v[spec.Key] = read();
        return v;
    }

    /// <summary>按当前参数值刷新一行短提示、行内徽章与底部极简状态（方案 4：参数区保持干净）。</summary>
    private void RefreshSemantics()
    {
        var v = CurrentParamValues();
        string type = SelectedEventType();
        bool clip = string.Equals(type, "clipboard.change", StringComparison.OrdinalIgnoreCase);

        ParamHint.Text = clip
            ? "值可用分号列多个（任一命中即可），regex: 前缀走正则；留空 = 不限制。"
            : string.Equals(type, "window.foreground", StringComparison.OrdinalIgnoreCase)
                ? "仅当前台进程真的换了才投递；同进程多窗口、桌面 / 任务栏不算切换。"
                : "监听目录里的文件发生变化时投递。";

        if (!clip)
        {
            if (_textBadge != null) _textBadge.Visibility = Visibility.Collapsed;
            SemanticMiniBar.Visibility = Visibility.Collapsed;
            return;
        }

        var (main, warning) = BuildClipboardSummary(v);
        bool skipped = warning.Length > 0;
        if (_textBadge != null)
        {
            _textBadge.Visibility = skipped ? Visibility.Visible : Visibility.Collapsed;
            _textBadge.ToolTip = skipped ? BuildBadgeToolTip(main, warning) : null;
        }

        SemanticMiniBar.Inlines.Clear();
        if (skipped)
        {
            var danger = (Brush)FindResource("DangerBrush");
            SemanticMiniBar.Inlines.Add(new Run("⚠ ") { Foreground = danger });
            SemanticMiniBar.Inlines.Add(new Run("共 " + CountConditions(v) + " 个条件，"));
            SemanticMiniBar.Inlines.Add(new Run("1 个可能不生效")
            { Foreground = danger, FontWeight = FontWeights.SemiBold });
            SemanticMiniBar.Inlines.Add(new Run(" · 悬停徽章看详情"));
            SemanticMiniBar.Visibility = Visibility.Visible;
        }
        else
        {
            SemanticMiniBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>徽章的 ToolTip：完整语义说明 + 警告，参数区里不放这一大段文字。</summary>
    private object BuildBadgeToolTip(string main, string warning)
    {
        var panel = new StackPanel { MaxWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = main, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(new TextBlock
        {
            Text = warning, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("DangerBrush"), FontWeight = FontWeights.SemiBold,
        });
        return panel;
    }

    /// <summary>底部状态里的「条件数」：只数非布尔、非空的项（忽略自己写入这类控制开关不算条件）。</summary>
    private int CountConditions(Dictionary<string, string> v)
    {
        int n = 0;
        foreach (var (spec, _) in _paramReads)
            if (!spec.IsBool && v.TryGetValue(spec.Key, out string? val) && !string.IsNullOrWhiteSpace(val)) n++;
        return n;
    }

    /// <summary>
    /// 生成剪贴板规则的「实际触发条件」文案（纯函数，供 UI 与无头冒烟共用）。
    /// 返回主说明与「文本内容被跳过」的警告（空串 = 没有该语义）。
    /// </summary>
    internal static (string Main, string Warning) BuildClipboardSummary(Dictionary<string, string> v)
    {
        string text = (v.GetValueOrDefault("text") ?? "").Trim();
        var tags = SplitTags(v.GetValueOrDefault("formats") ?? "");
        bool onlyText = tags.Count > 0 && tags.All(t => t == "text");
        bool own = !string.Equals((v.GetValueOrDefault("ignoreOwnWrites") ?? "true").Trim(),
            "false", StringComparison.OrdinalIgnoreCase);
        string ownTail = own
            ? " LTools 自己写入剪贴板不会触发。"
            : " LTools 自己写入剪贴板也会触发。";

        string main;
        if (tags.Count == 0)
        {
            main = text.Length == 0
                ? "任何一次剪贴板变化都触发（未限制格式与文本）。"
                : "剪贴板不含文本（纯图片 / 文件）时一定触发；含文本时，还需文本包含「" + Shorten(text) + "」才触发。";
        }
        else if (onlyText)
        {
            main = text.Length == 0
                ? "剪贴板含文本格式时触发。"
                : "剪贴板含文本、且文本包含「" + Shorten(text) + "」时触发。";
        }
        else
        {
            string names = DescribeFormats(tags);
            main = text.Length == 0
                ? "剪贴板含〔" + names + "〕中任一格式时触发（内容与上次相同也算一次变化）。"
                : "剪贴板含〔" + names + "〕中任一格式时触发；若同时含文本，文本还需包含「" + Shorten(text) + "」。";
        }

        // 命中不必然带文本时，EventMatcher 会跳过 text 条件——这正是「填了 66 却照触发」的原因。
        bool skipped = text.Length > 0
            && (tags.Count == 0 || tags.Contains("image") || tags.Contains("files"));
        string warning = skipped
            ? "⚠ 剪贴板不含文本时「文本内容」不参与判断：纯图片 / 文件即使不含「" + Shorten(text) + "」也照样触发。"
            : "";
        return (main + ownTail, warning);
    }

    private static HashSet<string> SplitTags(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string DescribeFormats(HashSet<string> tags)
    {
        var names = new List<string>();
        if (tags.Contains("text")) names.Add("文本");
        if (tags.Contains("image")) names.Add("图片");
        if (tags.Contains("files")) names.Add("文件");
        return names.Count == 0 ? "所选" : string.Join(" / ", names);
    }

    private static string Shorten(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 24 ? s : s[..24] + "…";
    }

    private static string PickToolTip(ParamMode mode) => mode switch
    {
        ParamMode.WindowTitle => "点击屏幕上的窗口，把标题填进来",
        ParamMode.WindowClass => "点击屏幕上的窗口，把类名填进来",
        ParamMode.Process => "点击屏幕上的窗口，把它的进程名填进来",
        ParamMode.Folder => "打开文件夹选择框，选监听目录",
        _ => "选择…",
    };

    /// <summary>参数行右侧的「选…」按钮：一律先走点击窗口选取 / 系统对话框。</summary>
    private void PickParamValue(ParamMode mode, TextBox box)
    {
        switch (mode)
        {
            case ParamMode.WindowTitle:
            {
                var info = SystemPickerDialog.PickWindow(this);
                if (!string.IsNullOrWhiteSpace(info?.Title)) box.Text = info!.Title;
                break;
            }
            case ParamMode.WindowClass:
            {
                var info = SystemPickerDialog.PickWindow(this);
                if (!string.IsNullOrWhiteSpace(info?.ClassName)) box.Text = info!.ClassName;
                break;
            }
            case ParamMode.Process:
            {
                string? name = SystemPickerDialog.PickProcess(this);
                if (!string.IsNullOrWhiteSpace(name)) box.Text = name!;
                break;
            }
            case ParamMode.Folder:
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择监听目录" };
                if (dlg.ShowDialog() == true) box.Text = dlg.FolderName;
                break;
            }
        }
    }

    /// <summary>收集参数行；空值不写进 Params（= 该项不限制）。</summary>
    private Dictionary<string, string>? CollectParams()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (spec, read) in _paramReads)
        {
            string value = read().Trim();
            if (value.Length > 0) result[spec.Key] = value;
        }
        return result.Count > 0 ? result : null;
    }

    // ============ 作用范围 / 变量插入（GUI 点选） ============

    private void ScopeProcess_Click(object sender, RoutedEventArgs e)
        => SystemPickerDialog.AppendLine(ProcessBox, SystemPickerDialog.PickProcess(this));

    private void ScopeTitle_Click(object sender, RoutedEventArgs e)
        => SystemPickerDialog.AppendLine(TitleBox, SystemPickerDialog.PickWindow(this)?.Title);

    private void ScopeClass_Click(object sender, RoutedEventArgs e)
        => SystemPickerDialog.AppendLine(ClassBox, SystemPickerDialog.PickWindow(this)?.ClassName);

    /// <summary>「插入变量 ▾」：按当前事件类型列出可用变量，点一下插进光标处。</summary>
    private void InsertVariable_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var names = new List<string>();
        if (EventVars.TryGetValue(SelectedEventType(), out string? line))
            names.AddRange(line.Replace("可用变量：", "")
                .Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries));
        names.Add("triggerEventType");
        names.Add("triggerEventTime");
        names.Add("triggerParam");

        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string captured = name;
            var item = new MenuItem { Header = "${" + captured + "}", Tag = captured };
            item.Click += (_, _) =>
            {
                ActionParamBox.SelectedText = "${" + captured + "}";
                ActionParamBox.Focus();
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = (FrameworkElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ============ 动作 ============

    private void PickAction_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ActionPoolPicker(this, "选择动作 / 功能", _actionRef);
        if (picker.ShowDialog() != true || string.IsNullOrWhiteSpace(picker.Result)) return;
        _actionRef = picker.Result!;
        ActionBox.Text = ActionName(_actionRef);
    }

    private static string ActionName(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return "";
        if (!BindingRef.Exists(reference)) return $"（{reference}）已不存在";
        return BindingRef.Describe(reference);
    }

    // ============ 保存 ============

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string tag = SelectedKind();

        if (string.IsNullOrWhiteSpace(_actionRef) || !BindingRef.Exists(_actionRef))
        {
            ShowError("请选择一个存在的动作或功能。");
            return;
        }

        string? eventType = null;
        Dictionary<string, string>? eventParams = null;
        if (tag == "event")
        {
            eventType = SelectedEventType();
            eventParams = CollectParams();
            // 文件系统事件的 path 必填（§13.5：路径不存在 / 无权限时规则停用并提示，不静默失败）。
            if (eventType == "file.change" &&
                (eventParams == null || !eventParams.TryGetValue("path", out var p) || string.IsNullOrWhiteSpace(p)))
            {
                ShowError("文件系统变化规则必须填「监听目录」。");
                return;
            }
        }

        int? intervalMs = null;
        string? dailyAt = null;
        string? cron = null;
        int delaySeconds = 0;
        // 两个启动选项只对「启动后」有意义：切到别的触发方式时不把残留勾选带进配置。
        bool onlyFirstStart = tag == "startup" && OnlyFirstStartBox.IsChecked == true;
        bool onlyOnAutoStart = tag == "startup" && OnlyOnAutoStartBox.IsChecked == true;

        if (tag == "timer")
        {
            // 三种时间源互斥且优先级 Cron > 间隔 > 每天时刻（§13.8）。
            string cronText = CronBox.Text.Trim();
            string intervalText = IntervalBox.Text.Trim();
            string dailyText = DailyBox.Text.Trim();

            if (cronText.Length > 0)
            {
                if (intervalText.Length > 0 || dailyText.Length > 0)
                {
                    ShowError("Cron 与间隔 / 每天时刻三选一：填了 Cron 请清空上面两项。");
                    return;
                }
                if (!CronSchedule.TryParse(cronText, out string cronError))
                {
                    ShowError("Cron 表达式无效：" + cronError);
                    return;
                }
                cron = cronText;
            }
            else if (intervalText.Length > 0)
            {
                if (!double.TryParse(intervalText, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes)
                    || minutes <= 0 || minutes * 60000 > int.MaxValue)
                {
                    ShowError("间隔请填大于 0 的分钟数（可带小数）。");
                    return;
                }
                intervalMs = (int)Math.Round(minutes * 60000);
            }
            else if (dailyText.Length > 0)
            {
                if (!TryDaily(dailyText, out int _, out int _))
                {
                    ShowError("每天时刻请填 HH:mm（如 09:00），小时 0–23、分钟 0–59。");
                    return;
                }
                dailyAt = dailyText;
            }
            else
            {
                ShowError("定时规则至少要填「间隔分钟」「每天时刻」或「Cron 表达式」之一。");
                return;
            }
        }
        else if (tag == "startup")
        {
            if (!int.TryParse(DelaySecondsBox.Text.Trim(), out delaySeconds) || delaySeconds < 0)
            {
                ShowError("延迟请填 0 或正整数秒数。");
                return;
            }
        }

        if (!int.TryParse(DelayMsBox.Text.Trim(), out int delayMs) || delayMs < 0
            || !int.TryParse(DebounceBox.Text.Trim(), out int debounceMs) || debounceMs < 0
            || !int.TryParse(ThrottleBox.Text.Trim(), out int throttleMs) || throttleMs < 0)
        {
            ShowError("高级参数请填 0 或正整数（毫秒）。");
            return;
        }

        var scope = new AppFilter
        {
            Processes = Lines(ProcessBox),
            Titles = Lines(TitleBox),
            Classes = Lines(ClassBox),
        };

        Result = new TriggerRule
        {
            Id = _rule.Id,
            Note = NoteBox.Text.Trim(),
            Enabled = EnabledBox.IsChecked == true,
            Kind = KindFromTag(),
            IntervalMs = intervalMs,
            DailyAt = dailyAt,
            Cron = cron,
            DelaySeconds = delaySeconds,
            OnlyFirstStartToday = onlyFirstStart,
            OnlyOnAutoStart = onlyOnAutoStart,
            EventType = eventType,
            Params = eventParams,
            ActionRef = _actionRef,
            ActionParam = tag == "event" && !string.IsNullOrWhiteSpace(ActionParamBox.Text)
                ? ActionParamBox.Text.Trim() : null,
            Scope = scope.IsEmpty ? null : scope,
            SkipFurther = tag == "event" && SkipFurtherBox.IsChecked == true,
            DelayMs = delayMs,
            DebounceMs = debounceMs,
            ThrottleMs = throttleMs,
            LastEditTimeUtc = DateTime.UtcNow.Ticks,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static List<string> Lines(TextBox box) => box.Text
        .Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n')
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .ToList();

    /// <summary>解析 <c>HH:mm</c>（与 TriggerScheduler.TryDaily 同规则）。</summary>
    private static bool TryDaily(string text, out int hh, out int mm)
    {
        hh = mm = 0;
        var parts = text.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out hh) || !int.TryParse(parts[1], out mm)) return false;
        return hh is >= 0 and <= 23 && mm is >= 0 and <= 59;
    }
}
