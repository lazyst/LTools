using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Features.Panel;

namespace LTools.Views;

/// <summary>
/// 动作池选择器：左列表 + 右预览。
/// <para>
/// 左列表一行一个动作（图标 + 名称 + 步数 / 宏），顶部「最近使用」在前；
/// 右侧给出这个动作的步骤与变量——名字叫「2」「清理选中文…」也看得出是什么。
/// 单击选中，双击 / Enter 直接确定；↑↓ 在搜索框里也能换行。
/// </para>
/// <para>供「菜单组」页和「超级面板」槽位共用。</para>
/// </remarks>
public partial class ActionPoolPicker : Window
{
    /// <summary>「最近使用」保留几条。</summary>
    private const int RecentMax = 6;

    /// <summary>预览里最多列几步（太多的动作没必要全铺开）。</summary>
    private const int PreviewSteps = 10;

    private readonly List<ActionRow> _all = new();
    private ListCollectionView? _view;
    private List<string> _recent = new();
    private ActionRow? _current;

    /// <summary>打开时指定的动作已不在池里：不自动改选，免得按一次确定就把槽位换掉。</summary>
    private bool _preselectMissing;

    /// <summary>选中的动作 Id；取消（Esc / 关闭）后为 null。</summary>
    public string? Result { get; private set; }

    /// <summary>是否按过「确定」（双击 / Enter 也算）。取消时把 <see cref="Result"/> 清空，调用方只看它。</summary>
    private bool _accepted;

    /// <param name="owner">可以为 null（超级面板先关面板再开选择器）；无 Owner 时按置顶处理。</param>
    public ActionPoolPicker(Window? owner, string title, string? preselectId = null)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);
        Title = title;
        // 继承 owner 的 Topmost：从超级面板（Topmost）打开时盖住同时置顶的设置窗口，
        // 避免被设置盖住 + 设置又被模态禁用的死锁（设置页 3 处调用点同此理）。
        // 没有 owner（面板已收）时就直接置顶。
        Topmost = owner?.Topmost ?? true;

        _recent = LoadRecent();
        BuildRows();
        ApplyFilter("");

        if (!string.IsNullOrEmpty(preselectId))
        {
            if (_all.Any(r => r.Id == preselectId))
            {
                Result = preselectId;
                SelectById(preselectId!);
            }
            else
            {
                // 动作池里已经没有它了：原样保留 Id（旧行为），但不自动选第一项
                Result = preselectId;
                _preselectMissing = true;
            }
        }

        EnsureSelection();
        UpdateHint();

        Loaded += (_, _) => SearchBox.Focus();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ============ 数据 ============

    private void BuildRows()
    {
        _all.Clear();
        foreach (var a in ActionRegistry.All
                     .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            ActionProgram? program = a.Program;
            string? macroId = MacroAction.MacroIdOf(a);
            bool macro = macroId != null;

            var row = new ActionRow
            {
                Id = a.Id,
                Name = string.IsNullOrWhiteSpace(a.Name) ? a.Id : a.Name,
                Glyph = IconCatalog.GetGlyph(a.Icon),
                IsMacro = macro,
                TypeLabel = macro ? "宏" : "动作",
                Description = macro
                    ? MacroAction.SummaryOf(a) ?? "宏文件缺失"
                    : program?.Description ?? "",
                StepCount = program?.Steps.Count ?? 0,
            };

            if (program != null)
            {
                foreach (var step in program.Steps.Take(PreviewSteps))
                    row.Steps.Add((StepSummary.Title(step), StepSummary.Describe(step)));
                foreach (var variable in program.Variables) row.Variables.Add(variable.Key);
            }

            row.RightText = macro ? "宏" : row.StepCount > 0 ? $"{row.StepCount} 步" : "";
            row.Summary = BuildSearchText(row);
            _all.Add(row);
        }

        // 内置功能（常用功能 / LTools 功能）：不是动作、不可编辑，选中后直接放进面板 / 快捷键
        foreach (var def in FeatureCatalog.All)
        {
            var row = new ActionRow
            {
                Id = def.DefaultReference,
                Name = def.Name,
                Glyph = IconCatalog.GetGlyph(def.Icon),
                TypeLabel = def.Group == FeatureGroup.Ltools ? "LTools 功能" : "功能 · " + def.Category,
                Description = def.Description,
                IsFeature = true,
                Section = def.Group == FeatureGroup.Ltools
                    ? "LTools 功能"
                    : "常用功能 · " + def.Category,
            };
            foreach (var p in def.Params) row.Params.Add($"{p.Label}：{p.Default}");
            row.RightText = def.HasParams ? $"{def.Params.Length} 个参数" : "";
            row.Summary = BuildSearchText(row);
            _all.Add(row);
        }
    }

    /// <summary>搜索匹配的文本：名称 + 说明 + 类型 + 每步的标题与参数 + 变量名。</summary>
    private static string BuildSearchText(ActionRow row)
    {
        var text = new System.Text.StringBuilder(row.Name);
        text.Append(' ').Append(row.TypeLabel);
        if (row.Description.Length > 0) text.Append(' ').Append(row.Description);
        foreach (var (title, detail) in row.Steps)
            text.Append(' ').Append(title).Append(' ').Append(detail);
        foreach (string key in row.Variables) text.Append(' ').Append(key);
        foreach (string param in row.Params) text.Append(' ').Append(param);
        return text.ToString();
    }

    private void ApplyFilter(string? keyword)
    {
        string kw = (keyword ?? "").Trim();
        var rows = new List<ActionRow>();

        if (kw.Length == 0)
        {
            // 「最近使用」在前，其余进「全部动作」（不在两组里重复出现）
            var recentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in _recent)
            {
                if (recentIds.Count >= RecentMax) break;
                var row = _all.FirstOrDefault(x => x.Id == id);
                if (row == null) continue;
                row.Group = "最近使用";
                rows.Add(row);
                recentIds.Add(id);
            }

            var rest = _all.Where(x => !recentIds.Contains(x.Id)).ToList();
            int actionCount = rest.Count(x => !x.IsFeature);
            string actionsGroup = $"全部动作 · {actionCount}";
            foreach (var row in rest)
            {
                row.Group = row.IsFeature ? row.Section : actionsGroup;
                rows.Add(row);
            }
        }
        else
        {
            rows = _all
                .Where(x => x.Summary.Contains(kw, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            string group = $"搜索结果 · {rows.Count}";
            foreach (var row in rows) row.Group = group;
        }

        _view = new ListCollectionView(rows);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActionRow.Group)));
        ActionList.ItemsSource = _view;

        CountText.Text = kw.Length == 0 ? $"{_all.Count} 项" : $"匹配 {rows.Count} / {_all.Count}";
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>筛选后保持选中：原选中还在就留着，否则选第一项（没有动作时清空预览）。</summary>
    private void EnsureSelection()
    {
        if (_view == null) return;
        var rows = _view.Cast<ActionRow>().ToList();

        if (_preselectMissing && _current == null)
        {
            UpdatePreview();
            return;
        }

        ActionRow? target = null;
        if (!string.IsNullOrEmpty(Result)) target = rows.FirstOrDefault(r => r.Id == Result);
        target ??= rows.FirstOrDefault();

        if (target == null)
        {
            _current = null;
            UpdatePreview();
            return;
        }

        ActionList.SelectedItem = target;
        ActionList.ScrollIntoView(target);
    }

    private void SelectById(string id)
    {
        var row = _view?.Cast<ActionRow>().FirstOrDefault(r => r.Id == id);
        if (row == null) return;
        ActionList.SelectedItem = row;
        ActionList.ScrollIntoView(row);
    }

    // ============ 预览 ============

    private void UpdatePreview()
    {
        PreviewHost.Children.Clear();

        if (_current == null)
        {
            PreviewHost.Children.Add(new TextBlock
            {
                Text = "在左侧选一个动作，这里会显示它的步骤、变量和已经放到的面板位置。",
                Foreground = Res("HintTextBrush"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400,
                HorizontalAlignment = HorizontalAlignment.Left,
            });
            return;
        }

        PreviewHost.Children.Add(BuildHeader(_current));

        if (!string.IsNullOrWhiteSpace(_current.Description))
        {
            PreviewHost.Children.Add(new TextBlock
            {
                Text = _current.Description,
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("SecondaryTextBrush"),
            });
        }

        if (_current.IsFeature)
        {
            var def = FeatureCatalog.Find(FeatureRef.IdOf(_current.Id));
            AddSection(def is { HasParams: true } ? $"参数（{def.Params.Length}）" : "参数");
            if (def is not { HasParams: true })
            {
                AddMuted("这个功能没有可调参数，放到面板 / 快捷键后点一下就直接执行。");
            }
            else
            {
                foreach (var p in def.Params)
                {
                    string value = p.Default.Length > 0 ? p.Default : "（空）";
                    string detail = p.Kind switch
                    {
                        FeatureParamKind.Choice => p.Options?
                            .FirstOrDefault(o => o.Value == p.Default)?.Label ?? value,
                        _ => value,
                    };
                    PreviewHost.Children.Add(new TextBlock
                    {
                        Text = $"· {p.Label}：{detail}",
                        Margin = new Thickness(0, 2, 0, 2),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = p.Hint,
                    });
                }
                AddMuted("加到面板 / 快捷键时会先弹出这个参数表，可以按位置填不同值。");
            }

            AddPickerHint();
            return;
        }

        if (_current.IsMacro)
        {
            // 宏动作只有一个「回放宏」步骤，把宏的时长 / 事件数说清楚比列步骤有用。
            AddSection("键鼠宏");
            AddMuted(_current.Description.Length > 0 ? _current.Description : "宏文件缺失");
            AddPickerHint();
            return;
        }

        AddSection(_current.StepCount > 0 ? $"步骤（{_current.StepCount}）" : "步骤");
        if (_current.Steps.Count == 0)
        {
            AddMuted("这个动作还没有步骤，可以在编辑器里添加。");
        }
        else
        {
            for (int i = 0; i < _current.Steps.Count; i++)
                PreviewHost.Children.Add(BuildStepRow(i + 1, _current.Steps[i]));
            if (_current.StepCount > _current.Steps.Count)
                AddMuted($"…… 还有 {_current.StepCount - _current.Steps.Count} 步");
        }

        if (_current.Variables.Count > 0)
        {
            AddSection("变量");
            var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            foreach (string key in _current.Variables) wrap.Children.Add(BuildChip("${" + key + "}"));
            PreviewHost.Children.Add(wrap);
        }

        AddPickerHint();
    }

    private void AddPickerHint() => PreviewHost.Children.Add(new TextBlock
    {
        Text = "双击列表项可直接确定，↑↓ 切换",
        Margin = new Thickness(0, 18, 0, 0),
        FontSize = 11.5,
        Foreground = Res("HintTextBrush"),
    });

    private UIElement BuildHeader(ActionRow row)
    {
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var chip = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(10),
            Background = Res("SurfaceAltBrush"),
            BorderBrush = Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = row.Glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        head.Children.Add(chip);

        var text = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Children.Add(new TextBlock
        {
            Text = row.Name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = MetaText(row),
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 11.5,
            Foreground = Res("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 1);
        head.Children.Add(text);
        return head;
    }

    /// <summary>「动作 · 已用于 全局 · 常用」——放没放到面板、放在哪，一眼看到。</summary>
    private static string MetaText(ActionRow row)
    {
        var parts = new List<string> { row.TypeLabel };
        List<PanelLibrary.PanelReference> refs;
        try { refs = PanelLibrary.FindReferences(row.Id); }
        catch (Exception) { refs = new List<PanelLibrary.PanelReference>(); }

        if (refs.Count == 0) parts.Add("未放到面板");
        else
        {
            var firstRef = refs[0];
            string one = $"已用于 {firstRef.Area} · {firstRef.SceneName} · {firstRef.GroupName}";
            parts.Add(refs.Count > 1 ? $"{one} 等 {refs.Count} 处" : one);
        }

        return string.Join(" · ", parts);
    }

    private UIElement BuildStepRow(int index, (string Title, string Detail) step)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Res("SurfaceBrush"),
            BorderBrush = Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = index.ToString(),
                FontSize = 10,
                Foreground = Res("HintTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        grid.Children.Add(badge);

        var line = new TextBlock
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = step.Detail.Length > 0 ? $"{step.Title} {step.Detail}" : step.Title,
        };
        line.Inlines.Add(new Run(step.Title) { Foreground = Res("TextBrush") });
        if (step.Detail.Length > 0)
        {
            line.Inlines.Add(new Run(" " + step.Detail) { Foreground = Res("SecondaryTextBrush") });
        }
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);
        return grid;
    }

    private Border BuildChip(string text) => new()
    {
        Background = Res("SurfaceAltBrush"),
        BorderBrush = Res("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 1),
        Margin = new Thickness(0, 0, 6, 6),
        Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = Res("SecondaryTextBrush") },
    };

    private void AddSection(string title) => PreviewHost.Children.Add(new TextBlock
    {
        Text = title,
        Margin = new Thickness(0, 16, 0, 4),
        FontSize = 11,
        Foreground = Res("HintTextBrush"),
    });

    private void AddMuted(string text) => PreviewHost.Children.Add(new TextBlock
    {
        Text = text,
        Foreground = Res("HintTextBrush"),
        TextWrapping = TextWrapping.Wrap,
    });

    private Brush Res(string key) => (Brush)FindResource(key);

    // ============ 事件 ============

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyFilter(SearchBox.Text);
        EnsureSelection();
    }

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActionList.SelectedItem is not ActionRow row) return;
        _current = row;
        Result = row.Id;
        _preselectMissing = false;
        UpdatePreview();
        UpdateHint();
    }

    /// <summary>双击某一行直接确定（点在空白处不动）。</summary>
    private void ActionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemsControl.ContainerFromElement(ActionList, (DependencyObject)e.OriginalSource) is not ListBoxItem)
            return;
        Confirm();
    }

    /// <summary>搜索框里的 ↑↓ 换选中行，Enter 确定（焦点留在搜索框里可以继续打字）。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && SearchBox.IsKeyboardFocusWithin)
        {
            MoveSelection(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.FocusedElement is not Button)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_view == null) return;
        var rows = _view.Cast<ActionRow>().ToList();
        if (rows.Count == 0) return;

        int index = _current == null ? -1 : rows.FindIndex(r => ReferenceEquals(r, _current));
        index = index < 0
            ? (delta > 0 ? 0 : rows.Count - 1)
            : Math.Clamp(index + delta, 0, rows.Count - 1);

        ActionList.SelectedItem = rows[index];
        ActionList.ScrollIntoView(rows[index]);
    }

    private void UpdateHint()
    {
        HintText.Text = _current == null
            ? "未选择动作"
            : $"已选：{_current.Name} · {_current.TypeLabel}"
              + (_current.StepCount > 0 ? $" · {_current.StepCount} 步" : "");
    }

    // ============ 新建 / 确定 ============

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dto = ActionEditor.Show(this, "新建动作", null);   // 池内新建可建组合（分发器自动开对应窗口）
        if (dto == null) return;

        ConfigStore.AddAction(dto);
        ConfigStore.Save();
        Result = dto.Id;
        _preselectMissing = false;

        BuildRows();
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Clear();   // 否则新动作可能被筛掉
        else ApplyFilter("");
        SelectById(dto.Id);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (string.IsNullOrEmpty(Result))
        {
            ConfirmDialog.Info(this, "选择动作", "请先在左侧列表中选择一个动作。");
            return;
        }

        string result = Result!;
        // 内置功能带参数：先让用户确认 / 修改，再作为引用返回（取消就留在选择器里）
        if (FeatureCatalog.IsFeatureRef(result))
        {
            var def = FeatureCatalog.Find(FeatureRef.IdOf(result));
            if (def is { HasParams: true })
            {
                string? updated = FeatureParamsDialog.Show(this, result);
                if (updated == null) return;
                result = updated;
            }
        }

        Result = result;
        SaveRecent(result);
        _accepted = true;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Esc / 关闭按钮 / 标题栏叉：没按确定就不算选择（否则「打开就自动选中第一项」会被当成结果）
        if (!_accepted) Result = null;
        base.OnClosed(e);
    }

    // ============ 最近使用 ============

    private static List<string> LoadRecent()
    {
        try { return ConfigStore.LoadConfig().RecentActionIds ?? new List<string>(); }
        catch (Exception ex)
        {
            AppLog.Warn("Picker", "读取「最近使用」失败：" + ex.Message);
            return new List<string>();
        }
    }

    private static void SaveRecent(string id)
    {
        try
        {
            UiStore.Modify(ui =>
            {
                var list = ui.RecentActionIds ??= new List<string>();
                list.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal));
                list.Insert(0, id);
                while (list.Count > RecentMax) list.RemoveAt(list.Count - 1);
            });
        }
        catch (Exception ex)
        {
            // 记不进「最近使用」不影响这次选择
            AppLog.Warn("Picker", "记录「最近使用」失败：" + ex.Message);
        }
    }

    // ============ 数据模型 ============

    private sealed class ActionRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Glyph { get; set; } = "";
        public string TypeLabel { get; set; } = "";
        public string Description { get; set; } = "";
        public string RightText { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Group { get; set; } = "";
        public string Section { get; set; } = "";
        public bool IsMacro { get; set; }
        public bool IsFeature { get; set; }
        public int StepCount { get; set; }
        public List<(string Title, string Detail)> Steps { get; } = new();
        public List<string> Variables { get; } = new();
        public List<string> Params { get; } = new();
    }
}
