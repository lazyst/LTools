using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features.CapturePro;
using LTools.Views.Controls;
using ShapePath = System.Windows.Shapes.Path;

namespace LTools.Views.CapturePro;

/// <summary>
/// 截图覆盖层的浮动工具栏：工具切换、样式参数、撤销 / 重做与完成动作。
/// </summary>
/// <remarks>
/// <para>
/// 工具格子按 <see cref="ToolCatalog.All"/> + <see cref="ToolLayout"/> 生成、参数格按
/// <see cref="ToolCatalog.Uses"/> 显隐：新增工具只改目录表，**别在这里另写一份清单**。
/// </para>
/// <para>
/// 一格可以放多个工具（同格再点一次循环切换，右下角一个小点表示「这里不止一个」），
/// 不用的工具可以收起来（右键格子 / 右键工具栏空白），布局只在 <c>configs/ui.json</c> 里
/// （<see cref="ToolLayoutStore"/>）。工具栏自身只改 <see cref="ToolStyle"/> 并抛事件，
/// 文档与画面由覆盖层负责。
/// </para>
/// </remarks>
public partial class CaptureToolbar : UserControl
{
    /// <summary>一个格子：组内的工具 + 当前显示哪一个 + 图标。</summary>
    private sealed class ToolCell
    {
        public ToolCell(List<CaptureTool> tools, RadioButton button, ShapePath icon)
        {
            Tools = tools;
            Button = button;
            Icon = icon;
            Current = tools[0];
        }

        public List<CaptureTool> Tools { get; }
        public RadioButton Button { get; }
        public ShapePath Icon { get; }
        public CaptureTool Current { get; set; }
    }

    private readonly List<ToolCell> _cells = new();
    private readonly List<FrameworkElement> _actionParts = new();   // 完成动作那一截（EditOnly 时整组收起）
    private Border? _selectSeparator;        // 「选择」与其它工具之间的竖线（照 Snow Shot 的 select-separator）
    private readonly Dictionary<ToolParam, FrameworkElement> _paramHosts = new();
    private readonly List<Action> _refreshers = new();
    private readonly List<RadioButton> _colorChips = new();

    private ToolLayout _layout = ToolLayout.Default();
    private ToolStyle? _style;
    private bool _built;
    private bool _syncing;

    private bool _dragging;
    private Point _dragStart;
    private double _dragLeft, _dragTop;
    private IInputElement? _dragCapture;

    /// <summary>用户切了工具。</summary>
    internal event Action<CaptureTool>? ToolChanged;

    /// <summary>工具条右半边那些「完成动作」（照 Snow Shot：一条里排完，不再单独一条动作栏）。</summary>
    public enum CaptureAction
    {
        Recognize,
        Pin,
        Live,
        Scroll,
        Record,
        RecordOptions,
        FullScreen,
        HistoryPrev,
        HistoryNext,
        HistoryWindow,
        Print,
        QuickSave,
        Save,
        Cancel,
        Copy,
    }

    /// <summary>点了某个完成动作（由覆盖层决定具体做什么）。</summary>
    public event Action<CaptureAction>? ActionRequested;

    /// <summary>样式（颜色 / 线宽 / 字号 / 块 / 透明度）变了。</summary>
    public event Action? StyleChanged;

    public event Action? UndoRequested;
    public event Action? RedoRequested;

    /// <summary>双击标题条要复位：宿主重新按当前选区摆一次（工具栏自己不知道选区在哪）。</summary>
    public event Action? ResetRequested;

    internal CaptureTool CurrentTool { get; private set; } = CaptureTool.Select;

    public CaptureToolbar()
    {
        InitializeComponent();
    }

    /// <summary>用户自己拖过位置（之后不再跟着默认落点跑；双击复位会清掉）。</summary>
    public bool UserMoved { get; private set; }

    /// <summary>按样式与当前工具建一次按钮（覆盖层构造完调）。</summary>
    internal void Init(ToolStyle style, CaptureTool current)
    {
        if (_built) return;
        _built = true;
        _style = style;

        BuildCells(ToolLayoutStore.Current);
        BuildActions();
        BuildParams();

        Sync(current);
        RefreshValues();
    }

    /// <summary>界面跟着工具的默认状态对齐（覆盖层切工具时调，会同步按钮的选中态）。</summary>
    internal void Sync(CaptureTool tool)
    {
        CurrentTool = tool;

        // 选中哪个工具，它所在的那一格就显示哪个（同格里切换时图标跟着换）
        var cell = CellOf(tool);
        if (cell != null) cell.Current = tool;

        _syncing = true;
        foreach (var c in _cells)
        {
            ShowCellIcon(c);
            c.Button.IsChecked = ReferenceEquals(c, cell);
        }
        _syncing = false;

        foreach (var (param, host) in _paramHosts)
            host.Visibility = ToolCatalog.Uses(tool, param) ? Visibility.Visible : Visibility.Collapsed;

        ParamBar.Visibility = ToolCatalog.Of(tool).Params.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshValues();
    }

    /// <summary>换一份布局（右键菜单 / 自检夹具调）：重建格子，当前工具的选中态跟着走。</summary>
    internal void ApplyLayout(ToolLayout layout)
    {
        foreach (var cell in _cells) ToolRow.Children.Remove(cell.Button);
        _cells.Clear();
        if (_selectSeparator != null) ToolRow.Children.Remove(_selectSeparator);

        BuildCells(layout);
        Sync(CurrentTool);
        Remeasure();
    }

    /// <summary>撤销 / 重做按钮的可用状态。</summary>
    public void SetUndo(bool canUndo, bool canRedo)
    {
        if (_undoBtn != null) _undoBtn.IsEnabled = canUndo;
        if (_redoBtn != null) _redoBtn.IsEnabled = canRedo;
    }

    /// <summary>放到屏幕底部中间（没有选区上下文时的兜底；双击标题栏复位也走宿主重新摆）。</summary>
    public void PlaceDefault(double width, double height)
    {
        FitWidth(width);
        Remeasure();
        double left = Math.Max(8, (width - What(ActualWidth, DesiredSize.Width)) / 2);
        double top = Math.Max(8, height - What(ActualHeight, DesiredSize.Height) - 20);
        if (Parent is Canvas)
        {
            Canvas.SetLeft(this, left);
            Canvas.SetTop(this, top);
        }
    }

    /// <summary>
    /// 贴到选区<b>下方</b>并水平居中（照 snow-shot：工具条跟着框走，不再钉在屏幕底部）。
    /// 下方放不下就翻到选区上方。
    /// </summary>
    /// <param name="selection">选区（窗口 DIP 坐标）。</param>
    /// <param name="width">窗口宽（DIP）。</param>
    /// <param name="height">窗口高（DIP）。</param>
    public void PlaceNear(Rect selection, double width, double height)
    {
        FitWidth(width);
        Remeasure();
        double w = What(ActualWidth, DesiredSize.Width);
        double h = What(ActualHeight, DesiredSize.Height);

        // Snow Shot：工具条贴选区外沿 6px（`kToolbarGap`），不是 8。
        const double gap = 6;
        const double edge = 8;

        double left = selection.Left + (selection.Width - w) / 2;
        left = Math.Clamp(left, edge, Math.Max(edge, width - w - edge));

        double top = selection.Bottom + gap;

        if (top + h > height - edge)
        {
            double above = selection.Top - h - gap;
            top = above >= edge ? above : top;               // 下方放不下 → 翻到上方
        }

        top = Math.Clamp(top, edge, Math.Max(edge, height - h - edge));

        if (Parent is Canvas)
        {
            Canvas.SetLeft(this, left);
            Canvas.SetTop(this, top);
        }
    }

    /// <summary>
    /// 窗口比工具栏还窄时按窗口收窄，上排的 <c>WrapPanel</c> 才会折成两行 / 多行。
    /// 画布里的子元素不限宽：不收的话右边那几个按钮被窗口裁掉（贴图内标注的小窗口实测）。
    /// </summary>
    private void FitWidth(double width)
    {
        double cap = Math.Max(240, width - 16);
        if (double.IsNaN(MaxWidth) || Math.Abs(MaxWidth - cap) > 0.5) MaxWidth = cap;
    }

    /// <summary>
    /// 把工具栏收回可见区。切工具会改高度（「只有一排」↔「再加参数条」），
    /// 不变高的那一次放置会让底部那截被画布裁掉。
    /// </summary>
    public void ClampInside(double width, double height)
    {
        if (Parent is not Canvas) return;

        FitWidth(width);
        Remeasure();
        double w = What(ActualWidth, DesiredSize.Width);
        double h = What(ActualHeight, DesiredSize.Height);
        double left = Canvas.GetLeft(this), top = Canvas.GetTop(this);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        Canvas.SetLeft(this, Math.Clamp(left, 0, Math.Max(0, width - w)));
        Canvas.SetTop(this, Math.Clamp(top, 0, Math.Max(0, height - h)));
    }

    /// <summary>
    /// 现跑一次布局再量。只 <c>Measure</c> 不行：参数条刚切显隐时子树的 <c>IsMeasureValid</c>
    /// 还是 true，WPF 直接把「只有一排」的旧 <c>DesiredSize</c> 还回来，
    /// 于是按矮的高度贴底、底下一截被画布裁掉。
    /// </summary>
    private void Remeasure() => UpdateLayout();

    /// <summary>布局跑过就用实际尺寸，没有（还没显示 / 还没量）才退回 <c>DesiredSize</c>。</summary>
    private static double What(double actual, double desired) => actual > 0.5 ? actual : desired;

    // ============ 构建 ============

    /// <summary>按布局建格子（插在 <c>ToolRow</c> 最前面；动作按钮与分隔条在后面）。</summary>
    private void BuildCells(ToolLayout layout)
    {
        _layout = layout;
        int index = 0;

        foreach (var group in layout.Groups)
        {
            var tools = group.ToList();
            var icon = new ShapePath { Style = (Style)FindResource("IconPath") };

            // 同格不止一个工具：右下角一个灰点（别用数字——小图标里认不出是几）
            var badge = new Ellipse
            {
                Width = 4,
                Height = 4,
                Margin = new Thickness(0, 0, 3, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Fill = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)),
                Visibility = tools.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
                IsHitTestVisible = false,
            };

            var grid = new Grid();
            grid.Children.Add(icon);
            grid.Children.Add(badge);

            var button = new RadioButton
            {
                Style = (Style)FindResource("ToolBtn"),
                GroupName = "captureTool",
                Content = grid,
            };

            var cell = new ToolCell(tools, button, icon);
            button.Tag = cell;
            button.Click += (_, _) => OnCellClick(cell);

            // 右键在按下时装菜单（与贴图同一个坑：ContextMenuOpening 里现赋第一次弹不出来）
            button.PreviewMouseRightButtonDown += (_, e) =>
            {
                e.Handled = true;
                OpenCellMenu(cell, button);
            };

            _cells.Add(cell);
            ToolRow.Children.Insert(index++, button);
            ShowCellIcon(cell);

            // 「选择」后面一条竖线（照 Snow Shot 的 select-separator）：只建一次，重建时搬位置
            if (_cells.Count == 1 && layout.Groups.Count > 1)
            {
                _selectSeparator ??= new Border { Style = (Style)FindResource("SeparatorBar") };
                if (_selectSeparator.Parent is Panel old) old.Children.Remove(_selectSeparator);
                ToolRow.Children.Insert(index++, _selectSeparator);
            }
        }
    }

    /// <summary>格子的图标与提示跟着「这一格现在显示哪个工具」走。</summary>
    private void ShowCellIcon(ToolCell cell)
    {
        var info = ToolCatalog.Of(cell.Current);
        cell.Icon.Data = (Geometry)FindResource("Icon" + info.Icon);
        cell.Button.ToolTip = cell.Tools.Count <= 1
            ? $"{info.Name}（{info.Shortcut}）\n{info.Hint}"
            : $"{info.Name}（{info.Shortcut}）· 这一格还有 " + string.Join(" / ",
                cell.Tools.Where(t => t != cell.Current)
                    .Select(t => ToolCatalog.Of(t).Name + "（" + ToolCatalog.Of(t).Shortcut + "）"))
              + "，再点一次切换\n" + info.Hint;
    }

    /// <summary>点格子：单工具直接切；同格再点一次循环到组内下一个。</summary>
    private void OnCellClick(ToolCell cell)
    {
        if (cell.Tools.Count > 1 && CurrentTool == cell.Current)
            cell.Current = cell.Tools[(cell.Tools.IndexOf(cell.Current) + 1) % cell.Tools.Count];

        ToolChanged?.Invoke(cell.Current);
    }

    private ToolCell? CellOf(CaptureTool tool) => _cells.FirstOrDefault(c => c.Tools.Contains(tool));

    // ============ 布局编辑（堆叠 / 收起） ============

    /// <summary>改布局：落盘 → 重建格子 → 当前工具被收起来时退回「选择」（别拿着一个看不见的工具）。</summary>
    private void ChangeLayout(Func<ToolLayout, ToolLayout> change)
    {
        var next = change(_layout);
        if (next.SameAs(_layout)) return;

        ToolLayoutStore.Save(next);
        ApplyLayout(next);

        if (next.IsHidden(CurrentTool)) ToolChanged?.Invoke(CaptureTool.Select);
    }

    /// <summary>格子右键：收起这个工具 / 拆成单独一格 / 并到左边一格 / 恢复默认。</summary>
    /// <remarks>
    /// 返回菜单是为了让冒烟能直接检查条目；<paramref name="open"/> = false 只建菜单不弹出
    /// （冒烟的环境里不该留下悬着的弹层）。
    /// </remarks>
    private ContextMenu OpenCellMenu(ToolCell cell, FrameworkElement target, bool open = true)
    {
        var tool = cell.Current;
        var info = ToolCatalog.Of(tool);
        var menu = new ContextMenu { PlacementTarget = target };

        var hide = new MenuItem { Header = $"收起「{info.Name}」" };
        hide.Click += (_, _) => ChangeLayout(l => l.Hide(tool));
        menu.Items.Add(hide);

        if (cell.Tools.Count > 1)
        {
            var split = new MenuItem { Header = $"把「{info.Name}」拆成单独一格" };
            split.Click += (_, _) => ChangeLayout(l => l.Split(tool));
            menu.Items.Add(split);
        }

        var merge = new MenuItem
        {
            Header = "并到左边一格",
            IsEnabled = _cells.IndexOf(cell) > 0,      // 第一格左边没东西
        };
        merge.Click += (_, _) => ChangeLayout(l => l.MergeLeft(tool));
        menu.Items.Add(merge);

        menu.Items.Add(new Separator());
        menu.Items.Add(ResetItem());

        if (open) menu.IsOpen = true;
        return menu;
    }

    /// <summary>工具栏空白处右键：把收起来的工具放回去 / 恢复默认。</summary>
    /// <remarks>
    /// 返回菜单是为了让冒烟能直接检查条目；<paramref name="open"/> = false 只建菜单不弹出
    /// （冒烟的环境里不该留下悬着的弹层）。
    /// </remarks>
    private ContextMenu OpenBackgroundMenu(FrameworkElement target, bool open = true)
    {
        var menu = new ContextMenu { PlacementTarget = target };

        var hidden = _layout.HiddenTools;
        if (hidden.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "（没有收起来的工具）", IsEnabled = false });
        }
        else
        {
            var restore = MenuFx.Submenu(menu, $"收起来的工具（{hidden.Count}）");
            foreach (var tool in hidden)
            {
                var info = ToolCatalog.Of(tool);
                var item = new MenuItem { Header = $"{info.Name}（{info.Shortcut}）" };
                item.Click += (_, _) => ChangeLayout(l => l.Insert(tool));
                restore.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(ResetItem());

        if (open) menu.IsOpen = true;
        return menu;
    }

    private MenuItem ResetItem()
    {
        var reset = new MenuItem { Header = "恢复默认工具栏" };
        reset.Click += (_, _) => ChangeLayout(_ => ToolLayout.Default());
        return reset;
    }

    /// <summary>工具栏空白处右键 = 布局菜单（点在格子上时是格子自己的菜单）。</summary>
    private void Chrome_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnControl(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        OpenBackgroundMenu(this);
    }

    /// <summary>
    /// 绘制工具右边排的是撤销 / 重做 + **完成动作**（照 Snow Shot：工具、撤销、完成动作在**一条**里，
    /// 用竖线分组；以前完成动作另起一条，两条叠在一起看着像两排工具）。
    /// </summary>
    private void BuildActions()
    {
        ToolRow.Children.Add(new Border { Style = (Style)FindResource("SeparatorBar") });

        _undoBtn = AddAction("IconUndo", "撤销（Ctrl+Z）", () => UndoRequested?.Invoke());
        _redoBtn = AddAction("IconRedo", "重做（Ctrl+Y）", () => RedoRequested?.Invoke());

        // 撤销 / 重做与完成动作之间那条线：它属于「完成动作」这一截，整组收起时一起收（
        // 贴图内标注还要能撤销画上去的那几笔）。
        var line = new Border { Style = (Style)FindResource("SeparatorBar") };
        _actionParts.Add(line);
        ToolRow.Children.Add(line);

        AddGroup(new[]
        {
            ActionButton("IconRecognize", "识别文字 / 表格 / 二维码（Ctrl+R）", CaptureAction.Recognize),
        });

        AddGroup(new[]
        {
            ActionButton("IconPin", "贴图到屏幕（Ctrl+T）", CaptureAction.Pin),
            ActionButton("IconLive", "实时贴图：贴一块跟着这个窗口刷新的图（Ctrl+W）", CaptureAction.Live),
            ActionButton("IconScroll", "长截图：滚着接成一张长图（Ctrl+L）", CaptureAction.Scroll),
            ActionButton("IconRecord", "录屏：把当前选区录成 MP4（3 秒倒计时后开始，控制条上可暂停 / 完成 / 取消）",
                CaptureAction.Record),
        });

        AddGroup(new[]
        {
            ActionButton("IconPrint", "打印（Ctrl+P）", CaptureAction.Print),
            ActionButton("IconQuickSave", "快速保存到图片\\LTools 截图，不弹框（Ctrl+Shift+S）", CaptureAction.QuickSave),
            ActionButton("IconSave", "另存为…（Ctrl+S）", CaptureAction.Save),
        });

        // 「更多」：不常用的入口收进一个菜单（全屏、采集历史翻页 / 历史窗），
        // 常驻按钮只留高频的那几个，免得工具条横着长成一整排。
        AddGroup(new[] { MoreButton() });

        AddGroup(new[]
        {
            ActionButton("IconClose", "取消（Esc）", CaptureAction.Cancel, danger: true),
            ActionButton("IconCopy", "复制到剪贴板并关闭（Enter）", CaptureAction.Copy, primary: true),
        });
    }

    /// <summary>「更多」按钮：点它弹出 <see cref="ShowMoreMenu"/>（本身不直接是个完成动作）。</summary>
    private Button MoreButton()
    {
        var button = new Button
        {
            Style = (Style)FindResource("ActBtn"),
            ToolTip = "更多：全屏（Ctrl+A）/ 采集历史：上一张 · 下一张（, .）/ 采集历史窗口…",
            Content = new ShapePath
            {
                Data = (Geometry)FindResource("IconMore"),
                Style = (Style)FindResource("IconPath"),
            },
        };
        button.Click += (_, _) => ShowMoreMenu(button);
        return button;
    }

    /// <summary>「更多」菜单：只放**低频但已实现**的入口，点完仍旧走 <see cref="ActionRequested"/>。</summary>
    private void ShowMoreMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };

        MenuItem Item(string header, CaptureAction action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => ActionRequested?.Invoke(action);
            return item;
        }

        menu.Items.Add(Item("全屏（Ctrl+A）", CaptureAction.FullScreen));
        menu.Items.Add(Item("采集历史：上一张（,）", CaptureAction.HistoryPrev));
        menu.Items.Add(Item("采集历史：下一张（.）", CaptureAction.HistoryNext));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("录屏选项…（声音 / 摄像头 / 特效 / 编码）", CaptureAction.RecordOptions));
        menu.Items.Add(Item("采集历史窗口…", CaptureAction.HistoryWindow));

        menu.IsOpen = true;
    }

    /// <summary>一组动作按钮前面加一条竖线（第一条不加）；分组同时记进 <see cref="_actionParts"/>，便于整组收起。</summary>
    private void AddGroup(IReadOnlyList<FrameworkElement> buttons)
    {
        if (_actionParts.Count > 0)
        {
            var line = new Border { Style = (Style)FindResource("SeparatorBar") };
            _actionParts.Add(line);
            ToolRow.Children.Add(line);
        }

        foreach (var button in buttons)
        {
            _actionParts.Add(button);
            ToolRow.Children.Add(button);
        }
    }

    private Button ActionButton(string icon, string tip, CaptureAction action, bool primary = false,
        bool danger = false)
    {
        var button = new Button
        {
            Style = (Style)FindResource(primary ? "BarBtnPrimary" : danger ? "BarBtnDanger" : "ActBtn"),
            ToolTip = tip,
            Content = new ShapePath
            {
                Data = (Geometry)FindResource(icon),
                Style = (Style)FindResource("IconPath"),
            },
        };
        button.Click += (_, _) => ActionRequested?.Invoke(action);
        return button;
    }

    /// <summary>
    /// 「完成动作」这一截显不显示：贴图内标注（`EditOnly`）没有出口栏——完成是 Enter / 双击，
    /// 结果交回那张贴图，别摆一排按了会被忽略的按钮。
    /// </summary>
    internal void SetActionsVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var part in _actionParts) part.Visibility = visibility;
    }

    private Button? _undoBtn, _redoBtn;

    private Button AddAction(string icon, string tip, Action click)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ActBtn"),
            ToolTip = tip,
            Content = new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource(icon),
                Style = (Style)FindResource("IconPath"),
            },
        };
        button.Click += (_, _) => click();
        ToolRow.Children.Add(button);
        return button;
    }

    private void BuildParams()
    {
        // 颜色
        var colors = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        colors.Children.Add(Label("颜色"));
        foreach (var color in ToolStyle.Palette)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ColorChip"),
                GroupName = "captureColor",
                Background = new SolidColorBrush(color),
                Tag = color,
                ToolTip = $"#{(int)color.R:X2}{(int)color.G:X2}{(int)color.B:X2}",
            };
            chip.Checked += (_, _) =>
            {
                if (_syncing || _style == null) return;
                _style.Color = (Color)chip.Tag;
                StyleChanged?.Invoke();
            };
            _colorChips.Add(chip);
            colors.Children.Add(chip);
        }
        AddHost(ToolParam.Color, colors);

        // 线宽 / 字号 / 块大小 / 透明度
        AddHost(ToolParam.Width, Step("线宽", 1, 50, 1,
            () => _style?.Width ?? 4, v => { if (_style != null) _style.Width = v; },
            v => v.ToString("0"), 4));

        AddHost(ToolParam.FontSize, Step("字号", 8, 72, 2,
            () => _style?.FontSize ?? 20, v => { if (_style != null) _style.FontSize = v; },
            v => v.ToString("0"), 20));

        AddHost(ToolParam.Block, Step("块大小", 4, 24, 2,
            () => _style?.Block ?? 8, v => { if (_style != null) _style.Block = v; },
            v => v.ToString("0"), 8));

        AddHost(ToolParam.Opacity, Step("透明度", 10, 90, 5,
            () => (_style?.Opacity ?? 0.35) * 100, v => { if (_style != null) _style.Opacity = v / 100; },
            v => v.ToString("0") + "%", 35));

        // 序号的形状与数字写法（枚举用文字胶囊：图标认不出「圆角方 / 罗马数字」）
        AddHost(ToolParam.Shape, Chips("形状",
            new[] { SerialShape.Circle, SerialShape.Square, SerialShape.Rounded },
            () => _style?.Shape ?? SerialShape.Circle,
            v => { if (_style != null) _style.Shape = v; },
            SerialLabel.Name, v => "徽标形状：" + SerialLabel.Name(v)));

        AddHost(ToolParam.Numbering, Chips("编号",
            new[]
            {
                SerialNumbering.Arabic, SerialNumbering.RomanUpper, SerialNumbering.RomanLower,
                SerialNumbering.AlphaUpper, SerialNumbering.AlphaLower, SerialNumbering.Chinese,
            },
            () => _style?.Numbering ?? SerialNumbering.Arabic,
            v => { if (_style != null) _style.Numbering = v; },
            SerialLabel.Chip, v => "数字写法：" + SerialLabel.Of(8, v)));

        // 滤镜类型与强度
        AddHost(ToolParam.FilterKind, Chips("类型",
            new[]
            {
                FilterKind.Blur, FilterKind.Grayscale, FilterKind.Invert, FilterKind.Emboss,
                FilterKind.Brighten, FilterKind.SmartErase,
            },
            () => _style?.FilterMode ?? FilterKind.Blur,
            v => { if (_style != null) _style.FilterMode = v; },
            SerialLabel.Name, v => "滤镜：" + SerialLabel.Name(v)));

        AddHost(ToolParam.FilterStrength, Step("强度", 0, 100, 5,
            () => _style?.FilterStrength ?? 40, v => { if (_style != null) _style.FilterStrength = v; },
            v => v.ToString("0"), 40));

        // 放大镜倍率 + 水印（文字 / 角度 / 间距）
        AddHost(ToolParam.Factor, Step("倍率", 1, 8, 1,
            () => _style?.Factor ?? 2, v => { if (_style != null) _style.Factor = v; },
            v => v.ToString("0") + "×", 2));

        AddHost(ToolParam.WatermarkText, Text("文字",
            () => _style?.WatermarkText ?? "LTools",
            v => { if (_style != null) _style.WatermarkText = v; }));

        AddHost(ToolParam.WatermarkAngle, Step("角度", -90, 90, 15,
            () => _style?.WatermarkAngle ?? -30, v => { if (_style != null) _style.WatermarkAngle = v; },
            v => v.ToString("0") + "°", -30));

        AddHost(ToolParam.WatermarkGap, Step("间距", 20, 400, 10,
            () => _style?.WatermarkGap ?? 120, v => { if (_style != null) _style.WatermarkGap = v; },
            v => v.ToString("0"), 120));
    }

    /// <summary>一格文字输入（水印内容）：失焦与回车都立刻写回样式，工具栏不自己存副本。</summary>
    private FrameworkElement Text(string label, Func<string> get, Action<string> set)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label(label));

        var box = new TextBox
        {
            Width = 110,
            Height = 22,
            FontSize = 12,
            Padding = new Thickness(6, 1, 6, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
            ToolTip = "水印文字（回车或失焦生效）",
        };

        void Commit()
        {
            if (_syncing) return;
            set(box.Text);
            StyleChanged?.Invoke();
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };

        _refreshers.Add(() =>
        {
            string value = get();
            if (!box.IsKeyboardFocusWithin && box.Text != value) box.Text = value;
        });

        host.Children.Add(box);
        return host;
    }

    /// <summary>
    /// 枚举型参数：一排文字胶囊，点了就换（单选）。
    /// 与颜色胶囊同一套「_syncing 防回环」写法，别让刷新把用户刚点的值又切回去。
    /// </summary>
    private FrameworkElement Chips<T>(string label, T[] values, Func<T> get, Action<T> set,
        Func<T, string> text, Func<T, string> tip)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label(label));

        var chips = new List<(RadioButton Button, T Value)>();
        foreach (var value in values)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChoiceChip"),
                GroupName = "captureChoice" + label,
                Content = text(value),
                ToolTip = tip(value),
                Tag = value,
            };
            chip.Checked += (_, _) =>
            {
                if (_syncing) return;
                set(value);
                StyleChanged?.Invoke();
            };
            chips.Add((chip, value));
            host.Children.Add(chip);
        }

        _refreshers.Add(() =>
        {
            var current = get();
            _syncing = true;
            foreach (var (button, value) in chips)
                button.IsChecked = EqualityComparer<T>.Default.Equals(value, current);
            _syncing = false;
        });

        return host;
    }

    private void AddHost(ToolParam param, FrameworkElement host)
    {
        _paramHosts[param] = host;
        ParamRow.Children.Add(host);
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)),
        FontSize = 12,
        Margin = new Thickness(10, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private FrameworkElement Step(string label, double min, double max, double step,
        Func<double> get, Action<double> set, Func<double, string> format, double reset)
    {
        // 透明底色：整格（不只是字形）都能滚轮 / 点击，不然「点在数字旁边空白处」没反应
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
        };
        host.Children.Add(Label(label));

        var value = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE7)),
            FontSize = 12,
            MinWidth = 40,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeWE,
            ToolTip = $"滚轮微调（{step:0.##}）· 点一下复位到 {format(reset)}",
        };

        Button Minus() => new()
        {
            Style = (Style)FindResource("StepBtn"),
            Content = "−",
        };

        var minus = Minus();
        var plus = Minus();
        plus.Content = "+";

        minus.Click += (_, _) =>
        {
            set(Math.Max(min, Math.Round(get() - step)));
            RefreshValues();
            StyleChanged?.Invoke();
        };
        plus.Click += (_, _) =>
        {
            set(Math.Min(max, Math.Round(get() + step)));
            RefreshValues();
            StyleChanged?.Invoke();
        };

        // 滚轮在数值上直接微调（照 Snow Shot：光标停在参数上滚就是调它）；
        // 点一下数值复位——「调过头了」比「找不到恢复按钮」常见得多。
        host.MouseWheel += (_, e) =>
        {
            double next = Math.Clamp(Math.Round(get() + (e.Delta > 0 ? step : -step)), min, max);
            if (Math.Abs(next - get()) < 1e-9) return;
            set(next);
            RefreshValues();
            StyleChanged?.Invoke();
            e.Handled = true;
        };
        value.MouseLeftButtonUp += (_, e) =>
        {
            set(reset);
            RefreshValues();
            StyleChanged?.Invoke();
            e.Handled = true;
        };

        host.Children.Add(minus);
        host.Children.Add(value);
        host.Children.Add(plus);

        _refreshers.Add(() => value.Text = format(get()));
        return host;
    }

    private void RefreshValues()
    {
        if (_style == null) return;
        foreach (var refresh in _refreshers) refresh();

        _syncing = true;
        foreach (var chip in _colorChips)
            chip.IsChecked = (Color)chip.Tag == _style.Color;
        _syncing = false;
    }

    // ============ 拖动 / 双击复位 ============

    private void Chrome_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnControl(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            UserMoved = false;
            if (ResetRequested != null) ResetRequested();                      // 宿主按选区重新摆
            else if (Parent is FrameworkElement parent) PlaceDefault(parent.ActualWidth, parent.ActualHeight);
            e.Handled = true;
            return;
        }

        if (Parent is not Canvas canvas) return;
        UserMoved = true;
        _dragging = true;
        _dragStart = e.GetPosition(canvas);
        _dragLeft = Canvas.GetLeft(this);
        _dragTop = Canvas.GetTop(this);
        if (double.IsNaN(_dragLeft)) _dragLeft = 0;
        if (double.IsNaN(_dragTop)) _dragTop = 0;

        // 捕获必须落在**接手势的那个元素**上（`Chrome` 那层 Border），不能是本 UserControl：
        // 捕获之后鼠标消息只发给「被捕获的元素 + 它的祖先」，Border 是它的**子**元素，
        // `Chrome_MouseMove` 一次都收不到——实测按住工具条拖动时它纹丝不动（像卡住），
        // 松手又一切正常。捕获 Border 之后消息照常冒泡到本类的方法。
        _dragCapture = sender as IInputElement ?? this;
        _dragCapture.CaptureMouse();
        e.Handled = true;
    }

    private void Chrome_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || Parent is not Canvas canvas) return;

        Point at = e.GetPosition(canvas);
        double left = Math.Clamp(_dragLeft + (at.X - _dragStart.X), 0, Math.Max(0, canvas.ActualWidth - ActualWidth));
        double top = Math.Clamp(_dragTop + (at.Y - _dragStart.Y), 0, Math.Max(0, canvas.ActualHeight - ActualHeight));
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        e.Handled = true;
    }

    private void Chrome_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _dragCapture?.ReleaseMouseCapture();
        _dragCapture = null;
        e.Handled = true;
    }

    /// <summary>点在按钮上时不要启动拖动（否则点工具会顺手把工具栏拖走）。</summary>
    private static bool IsOnControl(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or Thumb or Slider) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
