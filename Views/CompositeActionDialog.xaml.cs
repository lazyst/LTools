using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Views.Controls;
using Condition = LTools.Features.Condition;

namespace LTools.Views;

/// <summary>
/// 组合动作编辑器（计划 §10.1）：左「动作来源」· 中「流程编排」· 右「组合属性」。
/// </summary>
/// <remarks>
/// <para>
/// 流程是**嵌套块**：<c>如果</c> / <c>循环</c> 带子步骤，缩进表示层级；标记行
/// 「否则 / 结束如果 / 结束循环」用于定位插入点，本身不是数据。
/// 不提供 goto——重复执行用循环 + 跳出 / 继续表达。
/// </para>
/// <para>
/// 步骤**内嵌动作快照**（与动作池解耦，见 §12）：加入步骤即深拷贝，改池动作不影响已加步骤；
/// 宏步骤只存 <see cref="ActionDto.MacroId"/> 引用，事件本体留在 <c>macros/</c>（随备份一起走）。
/// 禁止嵌套组合（左栏动作池已排除组合动作，＋新建步骤类型也不含组合）。
/// </para>
/// <para>旧配置的 <see cref="ActionDto.Steps"/> 由 <see cref="FlowMigration"/> 打开时迁移，保存后写 Flow 并清空 Steps。</para>
/// </remarks>
public partial class CompositeActionDialog : Window
{
    private readonly ActionDto _draft;
    private readonly List<FlowNode> _flow;
    private readonly ObservableCollection<FlowRow> _rows = new();
    private readonly ActionKindSwitch _kindSwitch = new();

    // 左栏分组折叠状态（写回 LTools.json，下次打开保持）与当前搜索词。
    private readonly HashSet<string> _collapsed;
    private string _query = "";
    private readonly DispatcherTimer _varHintTimer;

    // 左栏拖拽（阈值触发，避免与双击冲突）
    private LeftItem? _leftDragCandidate;
    private Point _leftDragOrigin;
    private static LeftItem? _dragLeftItem;   // DoDragDrop 期间供中间区 Drop 读取

    public CompositeActionDialog(string title, ActionDto? existing)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);
        _draft = existing != null
            ? existing.Clone()
            : new ActionDto { Type = ActionType.composite, Icon = IconCatalog.Default };
        if (!string.IsNullOrEmpty(title)) Title = title;
        _flow = FlowMigration.Ensure(_draft);
        _collapsed = new HashSet<string>(
            ConfigStore.LoadConfig().CompositeCollapsedGroups ?? new List<string>(),
            StringComparer.Ordinal);

        NameBox.Text = _draft.Name;
        IconPicker.SelectedIcon = _draft.Icon;
        AbortBox.IsChecked = _draft.CompositeOnFail == OnFailStrategy.abort;
        // 标题栏的类型切换：本窗口固定「组合动作」，用户点「普通动作」即跳回普通编辑器。
        _kindSwitch.IsComposite = true;
        _kindSwitch.SelectionChanged += KindSwitch_SelectionChanged;
        TitleBar.ExtraContent = _kindSwitch;
        FlowList.ItemsSource = _rows;
        var varHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        varHintTimer.Tick += (_, _) =>
        {
            varHintTimer.Stop();
            VarHint.Text = "变量来自流程里的「设置变量」（名称可用中文）；点一下复制 ${变量}。";
        };
        _varHintTimer = varHintTimer;
        VarList.VariableInvoked += CopyVariable;
        RefreshVariables();
        ReloadRows();
        BuildSourceList();
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>返回值；取消时为 null。</summary>
    public ActionDto? Result { get; private set; }

    /// <summary>用户点了「改为普通动作…」：由 <see cref="ActionEditor"/> 改开普通编辑器（不带数据跳转）。</summary>
    public bool JumpToNormal { get; private set; }

    // ============ 左栏：动作来源 ============

    private void BuildSourceList()
    {
        _query = (SearchBox.Text ?? "").Trim();
        SourceList.Children.Clear();

        // 基础动作（10 种动作类型）→ 双击/拖入开预置类型的编辑器（§12）
        var basicDefs = new (ActionType Type, string Icon, string Label)[]
        {
            (ActionType.launchApp, "app", "启动程序"),
            (ActionType.openFile, "file", "打开文件"),
            (ActionType.openFolder, "folder", "打开文件夹"),
            (ActionType.openUrl, "globe", "打开网址"),
            (ActionType.runCommand, "terminal", "运行命令"),
            (ActionType.sendText, "send", "发送文本"),
            (ActionType.sendKeys, "keyboard", "模拟按键"),
            (ActionType.mouse, "mouse", "鼠标操作"),
            (ActionType.macro, "record", "键鼠宏"),
            (ActionType.@internal, "settings", "内部动作"),
        };
        var basics = basicDefs
            .Select(b => new LeftItem { Label = b.Label, Glyph = IconCatalog.GetGlyph(b.Icon), BasicType = b.Type })
            .Where(Match)
            .ToList();
        AddGroup("basic", "基础动作", basics);

        // 流程控制（嵌套块）→ set/if/loop 先开参数对话框；break/continue/stop 直接加入
        var flowDefs = new (FlowNodeKind Kind, string Icon, string Label)[]
        {
            (FlowNodeKind.set, "edit", "设置变量"),
            (FlowNodeKind.@if, "help", "如果"),
            (FlowNodeKind.loop, "refresh", "循环"),
            (FlowNodeKind.@break, "back", "跳出循环"),
            (FlowNodeKind.@continue, "forward", "进入下一次循环"),
            (FlowNodeKind.stop, "stop", "停止流程"),
        };
        var flowItems = flowDefs
            .Select(f => new LeftItem { Label = f.Label, Glyph = IconCatalog.GetGlyph(f.Icon), FlowKind = f.Kind })
            .Where(Match)
            .ToList();
        AddGroup("flow", "流程", flowItems);

        // 内部动作（内置命令）→ 内联 internal 快照（无需开编辑器）
        var internals = InternalActionRegistry.Commands
            .OrderBy(c => c, StringComparer.Ordinal)
            .Select(c => new LeftItem { Label = ActionEditorDialog.FormatCommand(c), Glyph = "\uE756", InternalCommand = c })
            .Where(Match)
            .ToList();
        AddGroup("internal", "内部动作", internals);

        // 动作池（排除组合动作，避免嵌套）→ 深拷贝快照
        var pool = ActionRegistry.All
            .Where(a => a.Type != ActionType.composite)
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .Select(a => new LeftItem { Label = a.Name, Glyph = IconCatalog.GetGlyph(a.Icon), PoolActionId = a.Id })
            .Where(Match)
            .ToList();
        AddGroup("pool", "动作池", pool);

        if (SourceList.Children.Count == 0)
            SourceList.Children.Add(new TextBlock
            {
                Text = "（无匹配）",
                Margin = new Thickness(12, 4, 0, 0),
                Foreground = (Brush)FindResource("HintTextBrush"),
            });
    }

    private bool Match(LeftItem i) =>
        _query.Length == 0 || i.Label.Contains(_query, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 加一个可折叠分组：标题按钮（▾/▸ + 名称 + 数量）+ 内容面板。
    /// 折叠状态写回 <c>LTools.json</c> 的 <c>CompositeCollapsedGroups</c>，下次打开保持；
    /// 搜索时强制展开（否则搜到的东西藏在折叠组里），但不改变已保存的折叠状态。
    /// </summary>
    private void AddGroup(string key, string title, List<LeftItem> items, string? emptyHint = null)
    {
        if (items.Count == 0 && (emptyHint == null || _query.Length > 0)) return;

        bool expanded = _query.Length > 0 || !_collapsed.Contains(key);

        var panel = new StackPanel
        {
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed,
        };
        foreach (var it in items) panel.Children.Add(BuildLeftButton(it));
        if (items.Count == 0 && emptyHint != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = emptyHint,
                Style = (Style)FindResource("HintLabel"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22, 2, 12, 6),
            });
        }

        var chevron = new TextBlock
        {
            Text = expanded ? "▾" : "▸",
            FontSize = 10,
            Width = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("HintTextBrush"),
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(chevron);
        content.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("SectionLabel"),
        });
        if (items.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = items.Count.ToString(CultureInfo.InvariantCulture),
                Style = (Style)FindResource("HintLabel"),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var header = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6, 6, 6, 0),
            Padding = new Thickness(6, 4, 6, 4),
            Cursor = Cursors.Hand,
            ToolTip = expanded ? "点击折叠" : "点击展开",
        };
        header.Click += (_, _) =>
        {
            bool nowExpanded = panel.Visibility != Visibility.Visible;
            panel.Visibility = nowExpanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = nowExpanded ? "▾" : "▸";
            header.ToolTip = nowExpanded ? "点击折叠" : "点击展开";
            PersistCollapsed(key, !nowExpanded);
        };

        SourceList.Children.Add(header);
        SourceList.Children.Add(panel);
    }

    /// <summary>把分组折叠状态写回配置（与设置窗口的防抖保存共用 ConfigIO 串行化，不会互相覆盖）。</summary>
    private void PersistCollapsed(string key, bool collapsed)
    {
        if (collapsed) _collapsed.Add(key);
        else _collapsed.Remove(key);

        try
        {
            string path = string.IsNullOrEmpty(ConfigStore.ConfigPath)
                ? ConfigLocator.FindPath()
                : ConfigStore.ConfigPath;
            ConfigIO.Modify(path, cfg =>
            {
                var list = cfg.CompositeCollapsedGroups ??= new List<string>();
                list.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
                if (collapsed) list.Add(key);
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("CompositeActionDialog.PersistCollapsed", ex);
        }
    }

    private Button BuildLeftButton(LeftItem item)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = item.Glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = item.Label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var btn = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(8, 1, 8, 1),
            MinHeight = 30,
            Cursor = Cursors.Hand,
            ToolTip = item.ToolTip,
        };
        btn.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) { AddFromLeft(item, CurrentInsertTarget()); _leftDragCandidate = null; return; }
            _leftDragCandidate = item;
            _leftDragOrigin = e.GetPosition(this);
        };
        return btn;
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // 把手排序：按下即记录，位移超阈值进入拖动（幽灵跟随 + 目标行高亮）
        if (_gripIdx != null)
        {
            GripDragTick(e);
            if (_gripDragging) e.Handled = true;
            return;
        }

        if (_leftDragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _leftDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _leftDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _leftDragCandidate;
        _leftDragCandidate = null;
        _dragLeftItem = item;
        try { DragDrop.DoDragDrop(this, new DataObject("clp.left", "1"), DragDropEffects.Copy); }
        catch { /* 拖拽取消静默 */ }
        finally { _dragLeftItem = null; }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => BuildSourceList();

    /// <summary>把左栏项变成流程节点并插入指定位置。</summary>
    private void AddFromLeft(LeftItem item, (List<FlowNode> Container, int Index) target)
    {
        FlowNode? node = null;

        if (item.InternalCommand != null)
        {
            node = ActionNode(new ActionDto
            {
                Type = ActionType.@internal,
                Command = item.InternalCommand,
                Name = item.Label,
                Icon = IconCatalog.Default,
            });
        }
        else if (item.PoolActionId != null)
        {
            var a = ActionRegistry.FindById(item.PoolActionId);
            if (a != null && a.Type != ActionType.composite)
            {
                var copy = a.Clone();
                copy.Id = "";
                node = ActionNode(copy);
            }
        }
        else if (item.BasicType != null)
        {
            // 双击/拖入均开编辑器（类型已预置），填完字段后作为内嵌步骤
            var draft = new ActionDto { Id = "", Name = "", Icon = IconCatalog.Default, Type = item.BasicType.Value };
            var dto = ActionEditorDialog.Show(this, "新建步骤", draft, allowComposite: false, requireName: false);
            BuildSourceList();   // 编辑期间可能录了新宏，刷新左栏动作池
            if (dto == null || dto.Type == ActionType.composite) return;   // 取消 / 防御
            var c = dto.Clone();
            c.Id = "";
            node = ActionNode(c);
        }
        else if (item.FlowKind != null)
        {
            node = BuildFlowNode(item.FlowKind.Value);
            if (node == null) return;   // 取消对话框
        }

        if (node == null) return;
        InsertNode(target, node);
    }

    private static FlowNode ActionNode(ActionDto action) =>
        new() { Kind = FlowNodeKind.action, Action = action };

    /// <summary>构造流程节点；set/if/loop 先弹参数编辑器，取消返回 null。</summary>
    private FlowNode? BuildFlowNode(FlowNodeKind kind)
    {
        FlowNode node = kind switch
        {
            FlowNodeKind.set => new FlowNode
            {
                Kind = FlowNodeKind.set,
                Set = new SetNode { Var = "", Probe = new Probe { Kind = ProbeKind.text } },
            },
            FlowNodeKind.@if => new FlowNode
            {
                Kind = FlowNodeKind.@if,
                If = new IfNode
                {
                    Condition = new Condition
                    {
                        Probe = new Probe { Kind = ProbeKind.text },
                        Op = CompareOp.contains,
                    },
                },
            },
            FlowNodeKind.loop => new FlowNode
            {
                Kind = FlowNodeKind.loop,
                Loop = new LoopNode { Mode = LoopMode.count, Count = 3 },
            },
            _ => new FlowNode { Kind = kind },
        };

        if (kind is FlowNodeKind.set or FlowNodeKind.@if or FlowNodeKind.loop)
        {
            var dlg = new FlowNodeDialog(node, FlowVariables.Collect(_flow)) { Owner = this, Topmost = Topmost };
            if (dlg.ShowDialog() != true || dlg.Result == null) return null;
            return dlg.Result;
        }
        return node;
    }

    private void InsertNode((List<FlowNode> Container, int Index) target, FlowNode node)
    {
        int idx = Math.Clamp(target.Index, 0, target.Container.Count);
        target.Container.Insert(idx, node);
        ReloadRows(node);
        BuildSourceList();   // 编辑期间可能录了新宏，刷新左栏动作池
    }

    /// <summary>当前选中行的插入位置；无选中 → 根层末尾。</summary>
    private (List<FlowNode> Container, int Index) CurrentInsertTarget()
    {
        var row = FlowList.SelectedItem as FlowRow;
        if (row == null) return (_flow, _flow.Count);

        switch (row.Kind)
        {
            case RowKind.Node:
                if (row.Node!.Kind == FlowNodeKind.@if)
                    return (EnsureThen(row.Node), 0);
                if (row.Node.Kind == FlowNodeKind.loop)
                    return (EnsureBody(row.Node), 0);
                return (row.Container, row.Index + 1);

            case RowKind.ElseMarker:
                return (EnsureElse(row.Owner!), 0);

            case RowKind.EndIf:
            case RowKind.EndLoop:
                return (row.Container, row.Index + 1);

            default:
                return (_flow, _flow.Count);
        }
    }

    // ============ 中间：流程编排 ============

    private void ReloadRows(FlowNode? select = null)
    {
        _rows.Clear();
        var strategies = new List<StrategyItem>
        {
            new("继续下一步", null),
            new("中止组合", OnFailStrategy.abort),
        };
        var macroNames = MacroStore.NameMap();
        int number = 0;
        BuildRows(_flow, depth: 0, strategies, macroNames, ref number);

        EmptyHint.Visibility = _rows.Any(r => !r.IsMarker) ? Visibility.Collapsed : Visibility.Visible;

        if (select != null)
        {
            var target = _rows.FirstOrDefault(r => ReferenceEquals(r.Node, select));
            if (target != null) FlowList.SelectedItem = target;
        }

        RefreshVariables();
    }

    /// <summary>右侧变量面板跟上流程里「设置变量 / 存回变量」的变化。</summary>
    private void RefreshVariables() => VarList.SetCustom(FlowVariables.Collect(_flow));

    private void BuildRows(List<FlowNode> nodes, int depth,
                           IReadOnlyList<StrategyItem> strategies,
                           IReadOnlyDictionary<string, string> macroNames,
                           ref int number)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            FlowNode n = nodes[i];
            number++;
            _rows.Add(new FlowRow(RowKind.Node, n, nodes, i, depth, owner: null,
                number.ToString(CultureInfo.InvariantCulture) + ".",
                FlowText.Badge(n), TitleOf(n, macroNames), strategies));

            if (n.Kind == FlowNodeKind.@if && n.If != null)
            {
                BuildRows(n.If.Then, depth + 1, strategies, macroNames, ref number);

                // 「否则」始终显示：它既是 Else 的入口，也是 Then 的边界（插入点在它之前 = Then 末尾）。
                _rows.Add(new FlowRow(RowKind.ElseMarker, null, nodes, i, depth, n,
                    "", "", "否则", strategies));
                if (n.If.Else != null)
                    BuildRows(n.If.Else, depth + 1, strategies, macroNames, ref number);

                _rows.Add(new FlowRow(RowKind.EndIf, null, nodes, i, depth, n,
                    "", "", "结束如果", strategies));
            }
            else if (n.Kind == FlowNodeKind.loop && n.Loop != null)
            {
                BuildRows(n.Loop.Body, depth + 1, strategies, macroNames, ref number);
                _rows.Add(new FlowRow(RowKind.EndLoop, null, nodes, i, depth, n,
                    "", "", "结束循环", strategies));
            }
        }
    }

    private static string TitleOf(FlowNode n, IReadOnlyDictionary<string, string> macroNames)
    {
        if (n.Kind == FlowNodeKind.action && n.Action?.Type == ActionType.macro)
        {
            string id = n.Action.MacroId ?? "";
            if (MacroStore.Exists(id) && macroNames.TryGetValue(id, out var name))
                return "键鼠宏：" + name;
            return "键鼠宏（宏文件缺失）";
        }
        return FlowText.Summary(n);
    }

    private void FlowList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource)) return;   // 别把延迟框/下拉的双击当成编辑行
        if (FlowList.SelectedItem is FlowRow row) EditRow(row);
    }

    private void EditRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is FlowRow row) EditRow(row);
    }

    private void EditRow(FlowRow row)
    {
        if (row.IsMarker || row.Node == null) return;

        if (row.Node.Kind == FlowNodeKind.action)
        {
            if (row.Node.Action == null) return;
            var dto = ActionEditorDialog.Show(this, "编辑步骤", row.Node.Action, allowComposite: false, requireName: false);
            if (dto == null || dto.Type == ActionType.composite) return;
            var a = dto.Clone();
            a.Id = "";
            row.Node.Action = a;
            ReloadRows(row.Node);
            BuildSourceList();   // 编辑期间可能录了新宏，刷新左栏动作池
            return;
        }

        if (row.Node.Kind is FlowNodeKind.set or FlowNodeKind.@if or FlowNodeKind.loop)
        {
            var dlg = new FlowNodeDialog(row.Node, FlowVariables.Collect(_flow)) { Owner = this, Topmost = Topmost };
            if (dlg.ShowDialog() != true || dlg.Result == null) return;

            int idx = row.Container.IndexOf(row.Node);
            if (idx < 0) return;
            row.Container[idx] = dlg.Result;
            ReloadRows(dlg.Result);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.DataContext is not FlowRow row) return;
        if (row.IsMarker || row.Node == null) return;

        bool hasChildren = row.Node.Kind switch
        {
            FlowNodeKind.@if => row.Node.If != null &&
                (row.Node.If.Then.Count > 0 || (row.Node.If.Else?.Count ?? 0) > 0),
            FlowNodeKind.loop => (row.Node.Loop?.Body.Count ?? 0) > 0,
            _ => false,
        };
        if (hasChildren &&
            !ConfirmDialog.Confirm(this, "删除步骤", "该步骤带有子步骤，删除后一并移除。确定？", danger: true))
            return;

        int idx = row.Container.IndexOf(row.Node);
        if (idx < 0) return;
        row.Container.RemoveAt(idx);
        ReloadRows();
    }

    private void Delay_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    // —— 拖拽（左栏项拖入 = 插入；把手拖动 = 移动，幽灵跟随，§12）——
    // 把手排序走窗口级鼠标跟踪，而非 OLE DoDragDrop：避免系统 drag image 与幽灵叠加。

    private int? _gripIdx;                    // 把手按下时记录的行索引
    private Point _gripOrigin;                // 按下坐标（进入拖动态的阈值判定）
    private bool _gripDragging;               // 是否已进入拖动态
    private FrameworkElement? _gripSource;    // 把手元素（鼠标捕获载体）
    private ListBoxItem? _gripSrcContainer;   // 源行（半透明占位）

    private void Grip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not FlowRow row) return;
        if (row.IsMarker) return;
        int idx = _rows.IndexOf(row);
        if (idx < 0) return;
        FlowList.SelectedIndex = idx;
        _gripSource = fe;
        _gripIdx = idx;
        _gripOrigin = e.GetPosition(this);
        _gripDragging = false;
        fe.CaptureMouse();      // 捕获：拖出窗口仍能收到移动 / 释放
        e.Handled = true;       // 吞掉，别让 ListBox 抢走选中 / 滚动
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_gripIdx is not int src || !_gripDragging) { EndReorderDrag(); return; }

        int hover = GripHoverRow(e);
        int insertAt = hover >= 0 ? GripInsertAt(e, hover) : -1;
        EndReorderDrag();
        if (insertAt >= 0) ApplyReorder(src, insertAt);
        e.Handled = true;
    }

    /// <summary>位移超阈值 → 显示幽灵跟随光标；目标行高亮。不在这里重排（见 <see cref="ApplyReorder"/>）。</summary>
    private void GripDragTick(MouseEventArgs e)
    {
        if (_gripIdx is not int idx) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndReorderDrag(); return; }

        var pos = e.GetPosition(this);
        if (!_gripDragging)
        {
            if (Math.Abs(pos.X - _gripOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _gripOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _gripDragging = true;
            ShowGhost(idx);
        }

        UpdateGhost(pos);
        PositionInsertLine(e);
    }

    /// <summary>把插入线定位到光标所停留行的上/下边缘（上半=该行前，下半=该行后）。</summary>
    private void PositionInsertLine(MouseEventArgs e)
    {
        int hover = GripHoverRow(e);
        if (hover < 0) { InsertLine.Visibility = Visibility.Collapsed; return; }
        var c = FlowList.ItemContainerGenerator.ContainerFromIndex(hover) as FrameworkElement;
        if (c == null) { InsertLine.Visibility = Visibility.Collapsed; return; }
        bool after = GripInsertAt(e, hover) > hover;
        var p = c.TransformToAncestor(GhostLayer).Transform(new Point(0, after ? c.ActualHeight : 0));
        Canvas.SetLeft(InsertLine, p.X);
        Canvas.SetTop(InsertLine, p.Y - 1.5);
        InsertLine.Width = c.ActualWidth > 0 ? c.ActualWidth : FlowList.ActualWidth;
        InsertLine.Visibility = Visibility.Visible;
    }

    private void ShowGhost(int index)
    {
        var row = _rows[index];
        GhostBadge.Text = row.Badge;
        GhostName.Text = row.Title;
        DragGhost.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.SizeAll;
        if (FlowList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem c)
        {
            c.Opacity = 0.35;
            _gripSrcContainer = c;
        }
    }

    private void UpdateGhost(Point windowPos)
    {
        var rel = TranslatePoint(windowPos, GhostLayer);   // 窗口坐标 → Canvas 坐标
        double w = DragGhost.ActualWidth > 0 ? DragGhost.ActualWidth : DragGhost.MinWidth;
        double h = DragGhost.ActualHeight > 0 ? DragGhost.ActualHeight : 34;
        Canvas.SetLeft(DragGhost, rel.X - w / 2);
        Canvas.SetTop(DragGhost, rel.Y - h / 2);
    }

    private void EndReorderDrag()
    {
        bool wasDragging = _gripDragging;
        _gripSource?.ReleaseMouseCapture();
        _gripSource = null;
        _gripIdx = null;
        _gripDragging = false;
        if (!wasDragging) return;      // 只是点了下把手（未拖动）：不碰光标与幽灵
        DragGhost.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = null;
        if (_gripSrcContainer != null) { _gripSrcContainer.Opacity = 1; _gripSrcContainer = null; }
        InsertLine.Visibility = Visibility.Collapsed;
    }

    /// <summary>拖动中的目标行：先按坐标判定是否落在列表视口内（列表外 → -1），
    /// 再找命中的行；命中列表内但没落在行上（项间空隙 / ItemsPanel）→ 末行。</summary>
    private int GripHoverRow(MouseEventArgs e)
    {
        var p = e.GetPosition(FlowList);
        if (p.X < 0 || p.Y < 0 || p.X > FlowList.ActualWidth || p.Y > FlowList.ActualHeight)
            return -1;                       // 列表外（左栏 / 右栏 / 视口之外）

        if (FlowList.InputHitTest(p) is DependencyObject d)
        {
            var container = ItemsControl.ContainerFromElement(FlowList, d) as ListBoxItem;
            if (container != null)
            {
                int i = FlowList.ItemContainerGenerator.IndexFromContainer(container);
                if (i >= 0) return i;
            }
        }
        return _rows.Count - 1;             // 列表内但未命中行 → 末行（下半即追加到末尾）
    }

    /// <summary>命中行 → 插入位置（0.._rows.Count）：上半 = 该行之前，下半 = 该行之后。</summary>
    private int GripInsertAt(MouseEventArgs e, int hover)
    {
        var container = FlowList.ItemContainerGenerator.ContainerFromIndex(hover) as ListBoxItem;
        if (container == null) return hover;
        return e.GetPosition(container).Y < container.ActualHeight / 2 ? hover : hover + 1;
    }

    /// <summary>释放：把源节点移到插入位置。</summary>
    private void ApplyReorder(int srcRow, int insertAtFlat)
    {
        if (srcRow < 0 || srcRow >= _rows.Count) return;
        var src = _rows[srcRow];
        if (src.IsMarker || src.Node == null) return;

        var dest = ResolveFlatInsert(insertAtFlat);
        // 不能把节点拖进自己的子树。
        if (ContainsContainer(src.Node, dest.Container)) return;

        int srcIdx = src.Container.IndexOf(src.Node);
        if (srcIdx < 0) return;

        int destIdx = dest.Index;
        if (ReferenceEquals(src.Container, dest.Container) && destIdx > srcIdx) destIdx--;

        src.Container.RemoveAt(srcIdx);
        destIdx = Math.Clamp(destIdx, 0, dest.Container.Count);
        dest.Container.Insert(destIdx, src.Node);
        ReloadRows(src.Node);
    }

    /// <summary>把「第 flat 行之前」解析为真实的（容器, 下标）。</summary>
    private (List<FlowNode> Container, int Index) ResolveFlatInsert(int flat)
    {
        if (flat <= 0) return (_flow, 0);
        if (flat >= _rows.Count) return (_flow, _flow.Count);

        var row = _rows[flat];
        switch (row.Kind)
        {
            case RowKind.Node:
                return (row.Container, row.Index);

            case RowKind.ElseMarker:
                // 「否则」之前 = Then 块末尾。
                return (EnsureThen(row.Owner!), row.Owner!.If!.Then.Count);

            case RowKind.EndIf:
                // 「结束如果」之前 = Else 块末尾（Else 未显示内容时即其起点）。
                return (EnsureElse(row.Owner!), row.Owner!.If!.Else!.Count);

            case RowKind.EndLoop:
                return (EnsureBody(row.Owner!), row.Owner!.Loop!.Body.Count);

            default:
                return (_flow, _flow.Count);
        }
    }

    /// <summary>节点子树里是否包含指定容器（禁止拖入自身内部）。</summary>
    private static bool ContainsContainer(FlowNode node, List<FlowNode> container)
    {
        if (node.Kind == FlowNodeKind.@if && node.If != null)
        {
            if (ReferenceEquals(node.If.Then, container)) return true;
            if (node.If.Else != null && ReferenceEquals(node.If.Else, container)) return true;
            foreach (var child in node.If.Then)
                if (ContainsContainer(child, container)) return true;
            if (node.If.Else != null)
                foreach (var child in node.If.Else)
                    if (ContainsContainer(child, container)) return true;
        }
        else if (node.Kind == FlowNodeKind.loop && node.Loop != null)
        {
            if (ReferenceEquals(node.Loop.Body, container)) return true;
            foreach (var child in node.Loop.Body)
                if (ContainsContainer(child, container)) return true;
        }
        return false;
    }

    private static List<FlowNode> EnsureThen(FlowNode ifNode)
    {
        ifNode.If ??= new IfNode();
        return ifNode.If.Then;
    }

    private static List<FlowNode> EnsureElse(FlowNode ifNode)
    {
        ifNode.If ??= new IfNode();
        return ifNode.If.Else ??= new List<FlowNode>();
    }

    private static List<FlowNode> EnsureBody(FlowNode loopNode)
    {
        loopNode.Loop ??= new LoopNode();
        return loopNode.Loop.Body;
    }

    /// <summary>OLE 拖放只剩「左栏项拖入」一路——把手排序已改走窗口级鼠标跟踪（<see cref="GripDragTick"/>）。</summary>
    private void FlowList_DragOver(object sender, DragEventArgs e)
    {
        if (_dragLeftItem != null) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }

    private void FlowList_Drop(object sender, DragEventArgs e)
    {
        if (_dragLeftItem != null)
        {
            AddFromLeft(_dragLeftItem, DropTarget(e));
            e.Handled = true;
        }
    }

    /// <summary>落点：命中行的上/下半 → 该行前 / 后；空白 → 根层末尾。</summary>
    private (List<FlowNode> Container, int Index) DropTarget(DragEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit)
        {
            var container = ItemsControl.ContainerFromElement(FlowList, hit) as ListBoxItem;
            if (container != null)
            {
                int i = FlowList.ItemContainerGenerator.IndexFromContainer(container);
                if (i >= 0)
                {
                    bool after = e.GetPosition(container).Y >= container.ActualHeight / 2;
                    return ResolveFlatInsert(after ? i + 1 : i);
                }
            }
        }
        return (_flow, _flow.Count);
    }

    // ============ 右栏 / 底部 ============

    /// <summary>右栏变量可点：复制 <c>${name}</c>，方便粘到步骤编辑窗口。</summary>
    private void CopyVariable(string name)
    {
        string token = "${" + name + "}";
        try
        {
            Clipboard.SetText(token);
            _varHintTimer.Stop();
            VarHint.Text = "已复制 " + token;
            _varHintTimer.Start();
        }
        catch (Exception ex)
        {
            VarHint.Text = "复制失败：" + ex.Message;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { ConfirmDialog.Info(this, "组合动作", "名称不能为空。"); NameBox.Focus(); return; }
        if (!_rows.Any(r => !r.IsMarker))
        {
            ConfirmDialog.Info(this, "组合动作", "组合动作至少需要一个步骤。");
            return;
        }

        if (FindMisplacedLoopControl(_flow, inLoop: false) is string bad)
        {
            ConfirmDialog.Info(this, "组合动作", $"「{bad}」只能放在循环体内。");
            return;
        }

        PruneEmptyElse(_flow);

        _draft.Name = name;
        _draft.Type = ActionType.composite;
        _draft.Icon = IconPicker.SelectedIcon ?? IconCatalog.Default;
        _draft.Flow = _flow;
        _draft.Steps = null;                 // 已迁移到 Flow，避免两套数据并存
        _draft.CompositeOnFail = AbortBox.IsChecked == true ? OnFailStrategy.abort : null;
        Result = _draft;
        DialogResult = true;
    }

    /// <summary>找出不在循环体内的跳出 / 继续（编辑器拦住，避免运行到根层才发现无效）。</summary>
    private static string? FindMisplacedLoopControl(List<FlowNode> nodes, bool inLoop)
    {
        foreach (var n in nodes)
        {
            if (!inLoop && n.Kind is FlowNodeKind.@break or FlowNodeKind.@continue)
                return FlowText.Badge(n);

            if (n.Kind == FlowNodeKind.@if && n.If != null)
            {
                if (FindMisplacedLoopControl(n.If.Then, inLoop) is string t) return t;
                if (n.If.Else != null && FindMisplacedLoopControl(n.If.Else, inLoop) is string f) return f;
            }
            else if (n.Kind == FlowNodeKind.loop && n.Loop != null)
            {
                if (FindMisplacedLoopControl(n.Loop.Body, true) is string b) return b;
            }
        }
        return null;
    }

    /// <summary>空的 Else 分支落盘时去掉，保持配置干净。</summary>
    private static void PruneEmptyElse(List<FlowNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.Kind == FlowNodeKind.@if && n.If != null)
            {
                PruneEmptyElse(n.If.Then);
                if (n.If.Else != null)
                {
                    PruneEmptyElse(n.If.Else);
                    if (n.If.Else.Count == 0) n.If.Else = null;
                }
            }
            else if (n.Kind == FlowNodeKind.loop && n.Loop != null)
            {
                PruneEmptyElse(n.Loop.Body);
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>标题栏切到「普通动作」：置跳转标志，由 <see cref="ActionEditor"/> 改开普通编辑器（不带数据）。</summary>
    private void KindSwitch_SelectionChanged(object? sender, EventArgs e)
    {
        if (_kindSwitch.IsComposite) return;   // 点在本段（组合动作）上：无动作
        JumpToNormal = true;
        DialogResult = false;
    }

    // ============ 辅助 ============

    /// <summary>原始命中的元素是否落在交互控件（TextBox/ComboBox/Button）内——用于避免误触行双击编辑。</summary>
    private static bool IsInteractive(object? src)
    {
        var d = src as DependencyObject;
        while (d != null)
        {
            if (d is TextBox || d is ComboBox || d is Button) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null;
        }
        return false;
    }

    private sealed class LeftItem
    {
        public string Label { get; set; } = "";
        public string Glyph { get; set; } = "";
        public string? ToolTip { get; set; }
        public string? InternalCommand { get; set; }   // 内部动作组：内置命令
        public string? PoolActionId { get; set; }       // 动作池组：复用（深拷贝）
        public ActionType? BasicType { get; set; }       // 基础动作组：动作类型（需开编辑器填字段）
        public FlowNodeKind? FlowKind { get; set; }      // 流程组：控制节点
    }

    private enum RowKind
    {
        Node,
        ElseMarker,
        EndIf,
        EndLoop,
    }

    /// <summary>流程行展示模型；node 行的属性直接读写底层 <see cref="FlowNode"/>，绑定编辑即落回数据。</summary>
    private sealed class FlowRow
    {
        public FlowRow(RowKind kind, FlowNode? node, List<FlowNode> container, int index, int depth,
                       FlowNode? owner, string number, string badge, string title,
                       IReadOnlyList<StrategyItem> strategies)
        {
            Kind = kind;
            Node = node;
            Container = container;
            Index = index;
            Depth = depth;
            Owner = owner;
            Number = number;
            Badge = badge;
            Title = title;
            StrategyOptions = strategies;
        }

        public RowKind Kind { get; }
        public FlowNode? Node { get; }
        public List<FlowNode> Container { get; }
        public int Index { get; }
        public int Depth { get; }
        public FlowNode? Owner { get; }
        public string Number { get; }
        public string Badge { get; }
        public string Title { get; }
        public IReadOnlyList<StrategyItem> StrategyOptions { get; }

        public bool IsMarker => Kind != RowKind.Node;

        /// <summary>有可编辑参数（动作 / 设置 / 如果 / 循环）；跳出、继续、停止只有语义。</summary>
        public bool CanEdit => !IsMarker && Node != null &&
            Node.Kind is FlowNodeKind.action or FlowNodeKind.set
                or FlowNodeKind.@if or FlowNodeKind.loop;

        /// <summary>缩进：每层 18px。</summary>
        public Thickness IndentMargin => new(Depth * 18.0, 1, 0, 1);

        public int DelayMs
        {
            get => Node?.DelayMs ?? 0;
            set { if (Node != null) Node.DelayMs = value; }
        }

        public int OnFailIndex
        {
            get => Node?.OnFail == OnFailStrategy.abort ? 1 : 0;
            set { if (Node != null) Node.OnFail = value == 1 ? OnFailStrategy.abort : null; }
        }
    }

    private sealed record StrategyItem(string Label, OnFailStrategy? Value)
    {
        public override string ToString() => Label;
    }
}
