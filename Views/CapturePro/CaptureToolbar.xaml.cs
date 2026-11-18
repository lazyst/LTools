using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.AutoFilter;
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
    private Button? _fillRegionsButton;      // 自动打码「填充区域」下拉（没识别结果时置灰）
    private Popup? _fontPopup;               // 字体族弹层（自检 / 冒烟要能点开它选字体）

    private ToolLayout _layout = ToolLayout.Default();
    private ToolStyleSet? _styles;

    /// <summary>当前工具的样式（换工具就换一份；<c>_tool</c> 由 <see cref="Sync"/> 维护）。</summary>
    private ToolStyle? _style => _styles?.For(_tool);

    private CaptureTool _tool = CaptureTool.Select;
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

    /// <summary>自动打码：点了「填充区域 → 某类型」要一次填满（参数 = 上游的区域类型键）。</summary>
    public event Action<string>? AutoFilterFillRequested;

    /// <summary>测距「实测值」提交（参数 = 用户填的数值；覆盖层换算成系数）。</summary>
    public event Action<double>? DistanceValueSubmitted;

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
    internal void Init(ToolStyleSet styles, CaptureTool current)
    {
        if (_built) return;
        _built = true;
        _styles = styles;

        BuildCells(ToolLayoutStore.Current);
        BuildActions();
        BuildParams();

        Sync(current);
        RefreshValues();
    }

    /// <summary>界面跟着工具的默认状态对齐（覆盖层切工具时调，会同步按钮的选中态）。</summary>
    internal void Sync(CaptureTool tool)
    {
        _tool = tool;
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
        // 主色：共享颜色开着时改一处 = 全改（由 ToolStyleSet 统一处理）
        AddHost(ToolParam.Color, ColorRow("颜色", "captureColor",
            () => _style?.Color ?? ToolStyle.Palette[0],
            c => _styles?.SetColor(StyleFamilies.Of(_tool), c)));

        // 文字族（上游 buildTextFamily）：字色 → 字号 + 字体族 → 粗 / 斜 → 水平对齐 →
        // 描边宽度 + 描边色 → 背景填充色 + 填充方式 → 背景圆角。
        // 顺序就是上游从左到右的顺序；别的工具用不到的格子收起来不占位。
        AddHost(ToolParam.TextSize, FontSizeRow());

        AddHost(ToolParam.FontFamily, FontPicker());

        AddHost(ToolParam.TextEmphasis, Emphasis());

        AddHost(ToolParam.TextAlign, Chips("对齐",
            new[] { TextAlign.Left, TextAlign.Center, TextAlign.Right },
            () => _style?.TextAlign ?? TextAlign.Left,
            v => { if (_style != null) _style.TextAlign = v; },
            ShapeLabel.Chip, ShapeLabel.Name, "textAlign"));

        AddHost(ToolParam.TextStrokeWidth, Step("描边", 0, TextInk.MaxStrokeWidth, 1,
            () => _style?.TextStrokeWidth ?? 0,
            v => { if (_style != null) _style.TextStrokeWidth = v; },
            v => v.ToString("0"), 0));

        AddHost(ToolParam.TextStrokeColor, ColorRow("描边色", "captureTextStroke",
            () => _style?.StrokeColor ?? TextInk.DefaultStroke,
            c => { if (_style != null) _style.StrokeColor = c; }));

        // 线宽 / 字号 / 块大小 / 透明度
        AddHost(ToolParam.Width, Step("线宽", 1, 50, 1,
            () => _style?.Width ?? 4, v => { if (_style != null) _style.Width = v; },
            v => v.ToString("0"), 4));

        AddHost(ToolParam.FontSize, WatermarkFontSizeRow());

        AddHost(ToolParam.Block, Step("块大小", 4, 24, 2,
            () => _style?.Block ?? 8, v => { if (_style != null) _style.Block = v; },
            v => v.ToString("0"), 8));

        AddHost(ToolParam.Opacity, Step("透明度", 10, 90, 5,
            () => (_style?.Opacity ?? 0.35) * 100, v => { if (_style != null) _style.Opacity = v / 100; },
            v => v.ToString("0") + "%", 35));

        // 形状族（上游 buildShapeFamily）：形状选择 / 描边线型 / 笔触风格 / 填充 / 圆角
        AddHost(ToolParam.ShapeKind, Chips("形状",
            new[] { ShapeKind.Rectangle, ShapeKind.Ellipse, ShapeKind.Diamond, ShapeKind.ThoughtBubble },
            () => _style?.ShapeKind ?? ShapeKind.Rectangle,
            v => { if (_style != null) _style.ShapeKind = v; },
            ShapeLabel.Chip, ShapeLabel.Name, "shapeKind",
            // 思考气泡只在气泡工具里出现（上游 buildShapeSelector 的第 4 项）
            v => v != ShapeKind.ThoughtBubble || _tool == CaptureTool.SpeechBubble));

        AddHost(ToolParam.StrokeStyle, Chips("描边",
            new[] { StrokeKind.Solid, StrokeKind.Dashed, StrokeKind.Dotted },
            () => _style?.StrokeStyle ?? StrokeKind.Solid,
            v => { if (_style != null) _style.StrokeStyle = v; },
            ShapeLabel.Chip, ShapeLabel.Name));

        AddHost(ToolParam.Rendering, Chips("笔触",
            new[] { StrokeRendering.Architect, StrokeRendering.Artist, StrokeRendering.Cartoonist },
            () => _style?.Rendering ?? StrokeRendering.Architect,
            v => { if (_style != null) _style.Rendering = v; },
            ShapeLabel.Chip, ShapeLabel.Name));

        AddHost(ToolParam.FillColor, ColorRow("填充色", "captureFillColor",
            () => _style?.FillColor ?? ToolStyle.DefaultFillColor,
            c => { if (_style != null) _style.FillColor = c; }));

        AddHost(ToolParam.FillStyle, Chips("填充",
            new[] { FillKind.None, FillKind.Solid, FillKind.Line, FillKind.CrossLine },
            () => _style?.FillStyle ?? FillKind.None,
            v => { if (_style != null) _style.FillStyle = v; },
            ShapeLabel.Chip, ShapeLabel.Name));

        AddHost(ToolParam.CornerRadius, Step("圆角", 0, TextInk.MaxCornerRadius, 2,
            () => _style?.CornerRadius ?? 6, v => { if (_style != null) _style.CornerRadius = v; },
            v => v.ToString("0"), 6));

        AddHost(ToolParam.LineType, Chips("线型",
            new[] { LineType.Straight, LineType.Curve },
            () => _style?.LineType ?? LineType.Curve,
            v => { if (_style != null) _style.LineType = v; },
            ShapeLabel.Chip, ShapeLabel.Name));

        AddHost(ToolParam.BrushMode, Chips("画笔",
            new[] { BrushMode.Regular, BrushMode.PressureSensitive },
            () => _style?.BrushMode ?? BrushMode.Regular,
            v => { if (_style != null) _style.BrushMode = v; },
            ShapeLabel.Chip, ShapeLabel.Name));

        // 序号的徽标类型与数字写法（枚举用文字胶囊：图标认不出「空心方 / 罗马数字」）
        AddHost(ToolParam.BadgeShape, Chips("徽标",
            new[]
            {
                SerialBadge.OutlinedCircle, SerialBadge.SolidCircle,
                SerialBadge.OutlinedSquare, SerialBadge.SolidSquare, SerialBadge.Circle,
            },
            () => _style?.Badge ?? SerialBadge.OutlinedCircle,
            v => { if (_style != null) _style.Badge = v; },
            SerialLabel.Chip, SerialLabel.Name, "badgeShape"));

        AddHost(ToolParam.Numbering, Chips("写法",
            new[]
            {
                SerialNumbering.Arabic, SerialNumbering.Roman, SerialNumbering.LowercaseLetters,
                SerialNumbering.UppercaseLetters, SerialNumbering.Chinese,
            },
            () => _style?.Numbering ?? SerialNumbering.Arabic,
            v => { if (_style != null) _style.Numbering = v; },
            SerialLabel.Chip, v => "数字写法：" + SerialLabel.Name(v), "numbering"));

        AddHost(ToolParam.SerialNumber, NumberBox("序号",
            () => _style?.SerialNumber ?? 1,
            v => { if (_style != null) _style.SerialNumber = Math.Max(0, v); },
            "下一个序号（滚轮微调，落一个自动 +1）"));

        AddHost(ToolParam.SerialFontSize, Step("字号", SerialSpec.MinFontSize, SerialSpec.MaxFontSize, 2,
            () => _style?.FontSize ?? 24, v => { if (_style != null) _style.FontSize = v; },
            v => v.ToString("0"), 24));

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
            () => _style?.FilterStrength ?? 50, v => { if (_style != null) _style.FilterStrength = v; },
            v => v.ToString("0"), 50));

        // 自动打码：类型（不含智能擦除，跟矩形滤镜共用同一份样式状态）+ 强度 + 「填充区域」下拉
        AddHost(ToolParam.AutoFilterKind, Chips("类型",
            new[]
            {
                FilterKind.Mosaic, FilterKind.Blur, FilterKind.Grayscale, FilterKind.Invert,
                FilterKind.Emboss, FilterKind.Brighten,
            },
            () => _style?.FilterForAutoFill ?? FilterKind.Mosaic,
            v => { if (_style != null) _style.FilterMode = v; },
            SerialLabel.Name, v => "打码方式：" + SerialLabel.Name(v), "autoFilterKind"));

        AddHost(ToolParam.AutoFilterStrength, Step("强度", 0, 100, 5,
            () => _style?.FilterStrength ?? 50, v => { if (_style != null) _style.FilterStrength = v; },
            v => v.ToString("0"), 50));

        AddHost(ToolParam.FillRegions, FillRegions());

        // ============ 放大镜族（上游 buildMagnifierFamily） ============
        // 顺序：形状（3，没有思考气泡）→ 色 → 线宽 → 倍率 → 连接方式 → 源轮廓 →
        // 箭头比例 → 引导线端点 → 圆角
        AddHost(ToolParam.Factor, Step("倍率", MagnifierSpec.MinFactor, MagnifierSpec.MaxFactor, 0.5,
            () => _style?.Factor ?? 2, v => { if (_style != null) _style.Factor = v; },
            v => v.ToString("0.#") + "×", 2));

        AddHost(ToolParam.Connection, Chips("连接",
            new[] { MagnifierConnection.None, MagnifierConnection.LeaderLine, MagnifierConnection.Gradient },
            () => _style?.Connection ?? MagnifierConnection.Gradient,
            v => { if (_style != null) _style.Connection = v; },
            MagnifierLabel.Chip, MagnifierLabel.Name, "magnifierConnection"));

        AddHost(ToolParam.SourceOutline, Toggle("源轮廓",
            () => _style?.SourceOutline ?? true,
            v => { if (_style != null) _style.SourceOutline = v; },
            "画出源区域的外框（与镜头重合时会被镜头盖住）"));

        AddHost(ToolParam.ArrowRatio, Step("箭头比例", ArrowInk.MinRatio, ArrowInk.MaxRatio, 0.1,
            () => _style?.ArrowRatio ?? 1, v => { if (_style != null) _style.ArrowRatio = v; },
            v => v.ToString("0.0") + "×", 1));

        AddHost(ToolParam.LeaderArrowhead, Picker("箭头",
            DistanceEndpoints,
            () => _style?.EndpointStyle ?? Arrowhead.None,
            v => { if (_style != null) _style.EndpointStyle = v; },
            MeasureLabel.Chip, MeasureLabel.Name));

        // ============ 箭头族（上游 buildArrowFamily） ============
        // 顺序：类型（3）→ 外描边（宽 + 色）→ 箭头比例 → 起止端点 → 杆型
        AddHost(ToolParam.ArrowKind, Chips("类型",
            new[] { ArrowType.Straight, ArrowType.Curve, ArrowType.Elbow },
            () => _style?.ArrowKind ?? ArrowType.Curve,
            v => { if (_style != null) _style.ArrowKind = v; },
            ShapeLabel.Chip, ShapeLabel.Name, "arrowKind"));

        AddHost(ToolParam.ArrowOutlineWidth, Step("外描边", 0, ArrowSpec.MaxOutlineWidth, 1,
            () => _style?.ArrowOutlineWidth ?? 0,
            v => { if (_style != null) _style.ArrowOutlineWidth = v; },
            v => v.ToString("0"), 0));

        AddHost(ToolParam.ArrowOutlineColor, ColorRow("描边色", "captureArrowOutline",
            () => _style?.ArrowOutlineColor ?? ArrowSpec.DefaultOutline,
            c => { if (_style != null) _style.ArrowOutlineColor = c; }));

        AddHost(ToolParam.StartArrowhead, Picker("起点",
            ArrowGeometry.Heads,
            () => _style?.StartArrowhead ?? Arrowhead.None,
            v => { if (_style != null) _style.StartArrowhead = v; },
            MeasureLabel.Chip, MeasureLabel.Name));

        AddHost(ToolParam.ArrowShaftType, Chips("杆型",
            new[] { ArrowShaft.Plain, ArrowShaft.Tapered },
            () => _style?.ArrowShaftType ?? ArrowShaft.Plain,
            v => { if (_style != null) _style.ArrowShaftType = v; },
            ShapeLabel.Chip, ShapeLabel.Name, "arrowShaft"));

        AddHost(ToolParam.EndArrowhead, Picker("终点",
            ArrowGeometry.Heads,
            () => _style?.EndArrowhead ?? Arrowhead.Arrow,
            v => { if (_style != null) _style.EndArrowhead = v; },
            MeasureLabel.Chip, MeasureLabel.Name));

        // ============ 水印族（上游 buildWatermarkFamily） ============
        // 顺序：色 → 文字 → 字号 + 字体族 → 模板 → 角度 → 间距 → 不透明度
        AddHost(ToolParam.WatermarkText, Text("文字",
            () => _style?.WatermarkText ?? string.Empty,
            v => { if (_style != null) _style.WatermarkText = v; },
            "水印文字（回车或失焦生效；留空就什么都不画）", 150));

        AddHost(ToolParam.WatermarkTemplate, WatermarkTemplates());

        AddHost(ToolParam.WatermarkAngle, Step("角度", WatermarkSpec.MinAngle, WatermarkSpec.MaxAngle, 15,
            () => _style?.WatermarkAngle ?? 30, v => { if (_style != null) _style.WatermarkAngle = v; },
            v => v.ToString("0") + "°", 30));

        AddHost(ToolParam.WatermarkGap, Step("间距", WatermarkSpec.MinGap, WatermarkSpec.MaxGap, 8,
            () => _style?.WatermarkGap ?? 56, v => { if (_style != null) _style.WatermarkGap = v; },
            v => v.ToString("0"), 56));

        AddHost(ToolParam.WatermarkOpacity, Step("不透明度", 0, 100, 4,
            () => (_style?.WatermarkOpacity ?? 0.16) * 100,
            v => { if (_style != null) _style.WatermarkOpacity = v / 100; },
            v => v.ToString("0") + "%", 16));

        // ============ 测距族（上游 buildDistanceFamily） ============
        // 顺序：描边（色 / 线宽）→ 实测值 → 单位 → 小数位 → 端点比例 → 端点样式
        AddHost(ToolParam.DistanceValue, MeasureValueRow());

        AddHost(ToolParam.DistanceUnit, Chips("单位",
            new[]
            {
                DistanceUnit.Px, DistanceUnit.Cm, DistanceUnit.M, DistanceUnit.Km, DistanceUnit.Mm,
                DistanceUnit.Pt, DistanceUnit.In, DistanceUnit.Ft, DistanceUnit.Um,
            },
            () => _style?.DistanceUnit ?? DistanceUnit.Cm,
            v => { if (_style != null) _style.DistanceUnit = v; },
            MeasureLabel.Chip, MeasureLabel.Name, "distanceUnit"));

        AddHost(ToolParam.DistanceDecimals, DecimalsRow("小数位",
            () => _style?.DistanceDecimals ?? 0,
            v => { if (_style != null) _style.DistanceDecimals = v; }, "distanceDecimals"));

        AddHost(ToolParam.EndpointScale, Step("端点比例", ArrowInk.MinRatio, ArrowInk.MaxRatio, 0.1,
            () => _style?.EndpointScale ?? 1, v => { if (_style != null) _style.EndpointScale = v; },
            v => v.ToString("0.0") + "×", 1));

        AddHost(ToolParam.EndpointStyle, Picker("端点",
            DistanceEndpoints,
            () => _style?.EndpointStyle ?? Arrowhead.Bar,
            v => { if (_style != null) _style.EndpointStyle = v; },
            MeasureLabel.Chip, MeasureLabel.Name));

        // ============ 测角族（上游 buildAngleFamily） ============
        AddHost(ToolParam.AngleKind, Chips("类型",
            new[] { AngleKind.Directed, AngleKind.Interior },
            () => _style?.AngleKind ?? AngleKind.Directed,
            v => { if (_style != null) _style.AngleKind = v; },
            MeasureLabel.Chip, MeasureLabel.Name, "angleKind"));

        AddHost(ToolParam.AngleUnit, Chips("单位",
            new[] { AngleUnit.Degrees, AngleUnit.Radians },
            () => _style?.AngleUnit ?? AngleUnit.Degrees,
            v => { if (_style != null) _style.AngleUnit = v; },
            MeasureLabel.Chip, MeasureLabel.Name, "angleUnit"));

        AddHost(ToolParam.AngleDecimals, DecimalsRow("小数位",
            () => _style?.AngleDecimals ?? 0,
            v => { if (_style != null) _style.AngleDecimals = v; }, "angleDecimals"));

        // ============ 橡皮族（上游 buildEraserFamily） ============
        // 三种模式共用一个选择器：点另一个模式就是换工具（上游 modeSelector 同一语义）
        AddHost(ToolParam.EraserMode, Chips("模式",
            new[] { CaptureTool.Eraser, CaptureTool.RectEraser, CaptureTool.BrushEraser },
            () => _tool,
            v => { if (v != _tool) ToolChanged?.Invoke(v); },
            EraserLabel.Chip, EraserLabel.Name, "eraserMode"));
    }

    /// <summary>端点样式下拉的选项：上游 distance 家族的 14 种（不含方块 / 倒三角）。</summary>
    private static readonly Arrowhead[] DistanceEndpoints =
    {
        Arrowhead.None, Arrowhead.Arrow, Arrowhead.Bar, Arrowhead.Dot, Arrowhead.Circle,
        Arrowhead.CircleOutline, Arrowhead.IndentedTriangle, Arrowhead.Triangle,
        Arrowhead.TriangleOutline, Arrowhead.Diamond, Arrowhead.DiamondOutline,
        Arrowhead.CrowfootOne, Arrowhead.CrowfootMany, Arrowhead.CrowfootOneOrMany,
    };

    /// <summary>
    /// 实测值一格：选中一条测距时可直接填它「应该是多少」，写回的是系数
    /// （上游 <c>setDistanceValue</c>：<c>factor = 值 / 实测长度</c>，钳 0.01–1000）。
    /// 没选中时置灰、显示占位 <c>—</c>。
    /// </summary>
    private FrameworkElement MeasureValueRow()
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label("实测值"));

        var box = new TextBox
        {
            Width = 74,
            Height = 22,
            FontSize = 12,
            Padding = new Thickness(6, 1, 6, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
            ToolTip = "选中一条测距后可以填实测值（回车生效），会换算成系数",
        };
        box.LostFocus += (_, _) => CommitMeasured(box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitMeasured(box);
            e.Handled = true;
        };

        _measuredBox = box;
        _refreshers.Add(RefreshMeasured);
        RefreshMeasured();

        host.Children.Add(box);
        return host;
    }

    private TextBox? _measuredBox;

    /// <summary>选中那条测距的实测长度（屏幕物理像素）；0 = 没有可编辑的目标。</summary>
    private double _measuredLength;

    private void CommitMeasured(TextBox box)
    {
        if (_syncing || _measuredLength <= 0) return;
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
            !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            RefreshMeasured();
            return;
        }

        if (!double.IsFinite(value) || value <= 0) { RefreshMeasured(); return; }
        DistanceValueSubmitted?.Invoke(value);
        RefreshMeasured();
    }

    private void RefreshMeasured()
    {
        if (_measuredBox == null) return;
        if (_measuredLength <= 0)
        {
            _measuredBox.IsEnabled = false;
            if (_measuredBox.Text.Length > 0) _measuredBox.Text = string.Empty;
            _measuredBox.ToolTip = "先选中一条测距，才能填它的实测值";
            return;
        }

        _measuredBox.IsEnabled = true;
        _measuredBox.ToolTip = "选中一条测距后可以填实测值（回车生效），会换算成系数";
        if (_measuredBox.IsKeyboardFocusWithin) return;
        double factor = _style?.DistanceFactor ?? 1;
        _measuredBox.Text = (_measuredLength * factor).ToString("0.###", CultureInfo.CurrentCulture);
    }

    /// <summary>覆盖层告诉工具栏「选中的这条测距有多长」（物理像素；0 = 没有）。</summary>
    internal void SetMeasuredDistance(double lengthPx)
    {
        _measuredLength = double.IsFinite(lengthPx) && lengthPx > 0 ? lengthPx : 0;
        RefreshMeasured();
    }

    /// <summary>小数位（0–3）：四个胶囊比加减号快。</summary>
    private FrameworkElement DecimalsRow(string label, Func<int> get, Action<int> set, string group) =>
        Chips(label, new[] { 0, 1, 2, 3 }, get, set,
            v => v == 0 ? "整数" : v + " 位", v => v == 0 ? "不保留小数" : $"保留 {v} 位小数", group);

    /// <summary>
    /// 选项太多的参数（端点 14 种）：一个按钮 + <see cref="ContextMenu"/>，比一排胶囊省地方。
    /// 点按钮不抢焦点、菜单项带勾选，选中即写回并抛 <see cref="StyleChanged"/>。
    /// </summary>
    private FrameworkElement Picker<T>(string label, T[] values, Func<T> get, Action<T> set,
        Func<T, string> text, Func<T, string> tip)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label(label));

        var button = new Button
        {
            Style = (Style)FindResource("StepBtn"),
            MinWidth = 58,
            Content = text(get()),
            ToolTip = tip(get()),
        };

        button.Click += (_, _) =>
        {
            var current = get();
            var menu = new ContextMenu();
            foreach (var value in values)
            {
                var item = new MenuItem
                {
                    Header = text(value),
                    IsCheckable = true,
                    IsChecked = EqualityComparer<T>.Default.Equals(value, current),
                    ToolTip = tip(value),
                };
                var captured = value;
                item.Click += (_, _) =>
                {
                    set(captured);
                    RefreshValues();
                    StyleChanged?.Invoke();
                };
                menu.Items.Add(item);
            }

            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };

        _refreshers.Add(() =>
        {
            var current = get();
            button.Content = text(current);
            button.ToolTip = tip(current);
        });

        host.Children.Add(button);
        return host;
    }

    /// <summary>一格文字输入（水印内容）：失焦与回车都立刻写回样式，工具栏不自己存副本。</summary>
    private FrameworkElement Text(string label, Func<string> get, Action<string> set, string tip, double width = 110)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label(label));

        var box = new TextBox
        {
            Width = width,
            Height = 22,
            FontSize = 12,
            Padding = new Thickness(6, 1, 6, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
            ToolTip = tip,
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
    /// 一排颜色胶囊：读 <paramref name="get"/>、写 <paramref name="set"/>，<c>GroupName</c> 分开。
    /// 主色（跟共享颜色开关联动）/ 填充色 / 文字描边色各来一排，**同一份实现**——
    /// 别为某个颜色再写一遍「造胶囊 + _syncing 回填」。
    /// </summary>
    private FrameworkElement ColorRow(string label, string group, Func<Color> get, Action<Color> set)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label(label));

        var chips = new List<RadioButton>();
        foreach (var color in ToolStyle.Palette)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ColorChip"),
                GroupName = group,
                Background = new SolidColorBrush(color),
                Tag = color,
                ToolTip = $"#{(int)color.R:X2}{(int)color.G:X2}{(int)color.B:X2}",
            };
            chip.Checked += (_, _) =>
            {
                if (_syncing) return;
                set((Color)chip.Tag);
                StyleChanged?.Invoke();
            };
            chips.Add(chip);
            host.Children.Add(chip);
        }

        _refreshers.Add(() =>
        {
            var current = get();
            _syncing = true;
            foreach (var chip in chips) chip.IsChecked = (Color)chip.Tag == current;
            _syncing = false;
        });

        return host;
    }

    /// <summary>
    /// 字号：数字步进 + S / M / L / XL 四个预设（上游 <c>ScreenshotToolPaletteFontEditor</c> 的
    /// <c>fontSizes()</c> = 24 / 30 / 42 / 54，与画笔宽度预设共用同一组值）。
    /// </summary>
    private FrameworkElement FontSizeRow() => PresetSizeRow("字号",
        () => _style?.TextSize ?? TextInk.DefaultFontSize,
        v => { if (_style != null) _style.TextSize = v; },
        new (string, double)[] { ("S", 24), ("M", 30), ("L", 42), ("XL", 54) },
        "captureTextSize", TextInk.DefaultFontSize, TextInk.MinFontSize, TextInk.MaxFontSize);

    /// <summary>
    /// 水印字号：数字步进 + 四个预设（上游 <c>style_presets::watermarkFontSizes()</c> = 12 / 16 / 24 / 30）。
    /// </summary>
    private FrameworkElement WatermarkFontSizeRow() => PresetSizeRow("字号",
        () => _style?.FontSize ?? 16,
        v => { if (_style != null) _style.FontSize = v; },
        new (string, double)[] { ("12", 12), ("16", 16), ("24", 24), ("30", 30) },
        "captureWatermarkSize", 16, WatermarkSpec.MinFontSize, WatermarkSpec.MaxFontSize);

    /// <summary>字号一格：加减 / 滚轮 / 点数字复位 + 一排预设胶囊（文字族与水印族共用）。</summary>
    private FrameworkElement PresetSizeRow(string label, Func<double> get, Action<double> set,
        (string Label, double Size)[] presets, string group, double reset, double min, double max)
    {
        var row = Step(label, min, max, 2, get, set, v => v.ToString("0"), reset);
        if (row is not Panel host) return row;

        var chips = new List<RadioButton>();
        foreach (var (text, size) in presets)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChoiceChip"),
                GroupName = "capture" + group,
                Content = text,
                Tag = size,
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = $"{text}（{size:0}px）",
            };
            chip.Checked += (_, _) =>
            {
                if (_syncing) return;
                set(size);
                RefreshValues();
                StyleChanged?.Invoke();
            };
            chips.Add(chip);
            host.Children.Add(chip);
        }

        _refreshers.Add(() =>
        {
            double current = get();
            _syncing = true;
            foreach (var chip in chips) chip.IsChecked = Math.Abs((double)chip.Tag - current) < 0.01;
            _syncing = false;
        });

        return row;
    }

    /// <summary>单个开关胶囊（放大镜的「源轮廓」这类布尔参数）。</summary>
    private FrameworkElement Toggle(string label, Func<bool> get, Action<bool> set, string tip)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label(label));

        var chip = new ToggleButton
        {
            Style = (Style)FindResource("ToggleChip"),
            Content = "开",
            ToolTip = tip,
        };
        chip.Click += (_, _) =>
        {
            if (_syncing) return;
            set(chip.IsChecked == true);
            RefreshValues();
            StyleChanged?.Invoke();
        };

        host.Children.Add(chip);

        _refreshers.Add(() =>
        {
            bool on = get();
            _syncing = true;
            chip.IsChecked = on;
            chip.Content = on ? "开" : "关";
            _syncing = false;
        });

        return host;
    }

    /// <summary>整数参数一格（序号的「下一个序号」）：回车 / 失焦写回，滚轮 ±1。</summary>
    private FrameworkElement NumberBox(string label, Func<int> get, Action<int> set, string tip)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label(label));

        var box = new TextBox
        {
            Width = 54,
            Height = 22,
            FontSize = 12,
            Padding = new Thickness(6, 1, 6, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
            ToolTip = tip,
        };

        void Commit()
        {
            if (_syncing) return;
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value)
                && value >= 0)
            {
                set(value);
                StyleChanged?.Invoke();
            }
            RefreshValues();
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };
        box.PreviewMouseWheel += (_, e) =>
        {
            if (_syncing) return;
            set(Math.Max(0, get() + (e.Delta > 0 ? 1 : -1)));
            RefreshValues();
            StyleChanged?.Invoke();
            e.Handled = true;
        };

        _refreshers.Add(() =>
        {
            if (box.IsKeyboardFocusWithin) return;
            string text = get().ToString(CultureInfo.CurrentCulture);
            if (box.Text != text) box.Text = text;
        });

        host.Children.Add(box);
        return host;
    }

    /// <summary>
    /// 水印模板：一个胶囊 + <see cref="ContextMenu"/>（点名字套用、底部「添加模板」「删除模板」）。
    /// 套用时按上游规则展开 <c>{text}</c> 与时间占位，结果写进水印文字。
    /// </summary>
    private FrameworkElement WatermarkTemplates()
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label("模板"));

        var button = new Button
        {
            Style = (Style)FindResource("StepBtn"),
            MinWidth = 66,
            Content = "模板",
            ToolTip = "水印模板：选中即套用（" + WatermarkTemplateText.Hint + "）",
        };

        button.Click += (_, e) =>
        {
            e.Handled = true;
            var menu = new ContextMenu();

            foreach (var template in _styles?.WatermarkTemplates ?? Array.Empty<WatermarkTemplate>())
            {
                var captured = template;
                var item = new MenuItem { Header = captured.Name, ToolTip = captured.Value };
                item.Click += (_, _) => ApplyWatermarkTemplate(captured);
                menu.Items.Add(item);
            }

            if (menu.Items.Count > 0) menu.Items.Add(new Separator());

            var add = new MenuItem { Header = "添加模板…" };
            add.Click += (_, _) => AddWatermarkTemplate();
            menu.Items.Add(add);

            var removable = _styles?.WatermarkTemplates;
            if (removable is { Count: > 0 })
            {
                var delete = MenuFx.Submenu(menu, "删除模板");
                foreach (var template in removable)
                {
                    var captured = template.Name;
                    var item = new MenuItem { Header = captured };
                    item.Click += (_, _) => DeleteWatermarkTemplate(captured);
                    delete.Items.Add(item);
                }
            }

            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };

        host.Children.Add(button);
        return host;
    }

    /// <summary>套用一个模板：<c>{text}</c> 换成当前文字、时间占位换成现在（上游 resolution 同一套）。</summary>
    private void ApplyWatermarkTemplate(WatermarkTemplate template)
    {
        if (_style == null) return;
        _style.WatermarkText = WatermarkTemplateText.Resolve(template.Value, _style.WatermarkText,
            DateTime.Now);
        RefreshValues();
        StyleChanged?.Invoke();
    }

    /// <summary>添加模板：先问名字、再问内容（内容里可以写 <c>{text}</c> 与时间占位）。</summary>
    private void AddWatermarkTemplate()
    {
        if (_styles == null) return;

        string suggested = string.IsNullOrWhiteSpace(_style?.WatermarkText)
            ? "模板 " + (_styles.WatermarkTemplates.Count + 1)
            : _style!.WatermarkText;

        var name = InputDialog.Show(Window.GetWindow(this), "新建水印模板", "模板名称", suggested);
        if (!name.ok || string.IsNullOrWhiteSpace(name.value)) return;

        string defaultValue = string.IsNullOrWhiteSpace(_style?.WatermarkText)
            ? "{text}"
            : _style!.WatermarkText;
        var value = InputDialog.Show(Window.GetWindow(this), "新建水印模板", "模板内容（" +
            WatermarkTemplateText.Hint + "）", defaultValue);
        if (!value.ok || string.IsNullOrWhiteSpace(value.value)) return;

        _styles.AddWatermarkTemplate(name.value, value.value);
        CaptureStyleStore.SaveSoon();
        ApplyWatermarkTemplate(new WatermarkTemplate(name.value.Trim(), value.value.Trim()));
    }

    private void DeleteWatermarkTemplate(string name)
    {
        if (_styles == null) return;
        if (!_styles.RemoveWatermarkTemplate(name)) return;
        CaptureStyleStore.SaveSoon();
    }


    /// <summary>
    /// 粗体 / 斜体：两个可以同时按下的胶囊（上游 <c>ScreenshotToolPaletteTextEmphasisEditor</c>）。
    /// </summary>
    private FrameworkElement Emphasis()
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label("粗细"));

        var bold = new ToggleButton
        {
            Style = (Style)FindResource("ToggleChip"),
            Content = "B",
            FontWeight = FontWeights.Bold,
            ToolTip = "粗体",
        };
        var italic = new ToggleButton
        {
            Style = (Style)FindResource("ToggleChip"),
            Content = "I",
            FontStyle = FontStyles.Italic,
            ToolTip = "斜体",
        };

        // 用 Click 而不是 Checked / Unchecked：一套处理，`_syncing` 回填不会触发它
        bold.Click += (_, _) =>
        {
            if (_syncing) return;
            if (_style != null) _style.TextBold = bold.IsChecked == true;
            StyleChanged?.Invoke();
        };
        italic.Click += (_, _) =>
        {
            if (_syncing) return;
            if (_style != null) _style.TextItalic = italic.IsChecked == true;
            StyleChanged?.Invoke();
        };

        host.Children.Add(bold);
        host.Children.Add(italic);

        _refreshers.Add(() =>
        {
            _syncing = true;
            bold.IsChecked = _style?.TextBold ?? false;
            italic.IsChecked = _style?.TextItalic ?? false;
            _syncing = false;
        });

        return host;
    }

    /// <summary>字体族下拉里「没有指定」的那一项（= 上游空 <c>fontFamily</c>）。</summary>
    private const string DefaultFontLabel = "默认（微软雅黑 UI）";

    /// <summary>系统字体族：量一次就缓存（<see cref="Fonts.SystemFontFamilies"/> 在部分机器上要几十毫秒）。</summary>
    private static IReadOnlyList<string>? _fontFamilies;

    private static IReadOnlyList<string> FontFamilies()
    {
        if (_fontFamilies != null) return _fontFamilies;

        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            foreach (var family in Fonts.SystemFontFamilies)
            {
                string name = family.Source;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
            }
        }
        catch (Exception ex)
        {
            LTools.Core.AppLog.Warn("capture", "枚举系统字体失败：" + ex.Message);
        }

        _fontFamilies = names.ToList();
        return _fontFamilies;
    }

    /// <summary>
    /// 字体族：一个胶囊 + 可搜索的弹层（上游 <c>FontEditor</c> 的 searchable family select）。
    /// </summary>
    private FrameworkElement FontPicker()
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label("字体"));

        var caption = new TextBlock
        {
            Text = DefaultFontLabel,
            FontSize = 12,
            MaxWidth = 120,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
        };
        var chevron = new ShapePath
        {
            Data = (Geometry)FindResource("IconChevronDown"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x59, 0x59, 0x59)),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(caption);
        content.Children.Add(chevron);

        var button = new Button
        {
            Style = (Style)FindResource("BarPill"),
            Content = content,
            ToolTip = "字体（可以搜索）",
        };

        var search = new TextBox
        {
            Height = 24,
            FontSize = 12,
            Padding = new Thickness(6, 1, 6, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
            ToolTip = "输入字体名筛选（Enter 选第一个，Esc 关闭）",
        };

        var list = new ListBox
        {
            MaxHeight = 220,
            Width = 220,
            Margin = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 12,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

        var stack = new StackPanel();
        stack.Children.Add(search);
        stack.Children.Add(list);

        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = stack,
        };

        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Child = card,
        };

        bool filling = false;

        void Fill(string filter)
        {
            filling = true;
            list.Items.Clear();
            list.Items.Add(DefaultFontLabel);
            foreach (string name in FontFamilies())
                if (filter.Length == 0 || name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    list.Items.Add(name);
            filling = false;
        }

        void Close() => popup.IsOpen = false;

        void Choose(string? name)
        {
            if (name == null) return;
            if (_style != null) _style.FontFamily = name == DefaultFontLabel ? string.Empty : name;
            Close();
            RefreshValues();
            StyleChanged?.Invoke();
        }

        button.Click += (_, e) =>
        {
            e.Handled = true;
            if (popup.IsOpen)
            {
                Close();
                return;
            }

            search.Text = string.Empty;
            Fill(string.Empty);
            list.SelectedItem = null;
            popup.IsOpen = true;
            search.Focus();
        };

        search.TextChanged += (_, _) => Fill(search.Text.Trim());
        search.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (list.Items.Count > 0)
                        Choose((list.SelectedItem as string) ?? list.Items[0] as string);
                    e.Handled = true;
                    break;
                case Key.Down:
                case Key.Up:
                {
                    int delta = e.Key == Key.Down ? 1 : -1;
                    int next = Math.Clamp(list.SelectedIndex + delta, 0, Math.Max(0, list.Items.Count - 1));
                    if (list.Items.Count > 0) list.SelectedIndex = next;
                    e.Handled = true;
                    break;
                }
            }
        };

        list.SelectionChanged += (_, _) =>
        {
            if (filling || !popup.IsOpen) return;
            if (list.SelectedItem is not string name) return;
            Choose(name);
        };

        host.Children.Add(button);
        _fontPopup = popup;

        _refreshers.Add(() =>
        {
            string family = _style?.FontFamily ?? string.Empty;
            caption.Text = string.IsNullOrWhiteSpace(family) ? DefaultFontLabel : family;
            caption.ToolTip = caption.Text;
        });

        return host;
    }

    /// <summary>
    /// 枚举型参数：一排文字胶囊，点了就换（单选）。
    /// 与颜色胶囊同一套「_syncing 防回环」写法，别让刷新把用户刚点的值又切回去。
    /// <paramref name="visible"/> 非空时按它显隐单个胶囊（思考气泡只在气泡工具里出现）。
    /// </summary>
    private FrameworkElement Chips<T>(string label, T[] values, Func<T> get, Action<T> set,
        Func<T, string> text, Func<T, string> tip, string? group = null, Func<T, bool>? visible = null)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label(label));

        var chips = new List<(RadioButton Button, T Value)>();
        foreach (var value in values)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChoiceChip"),
                GroupName = "captureChoice" + (group ?? label),
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
            {
                bool shown = visible?.Invoke(value) ?? true;
                button.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                button.IsChecked = shown && EqualityComparer<T>.Default.Equals(value, current);
            }
            _syncing = false;
        });

        return host;
    }

    /// <summary>
    /// 自动打码的「填充区域」下拉：按识别到的类型一次填满
    /// （上游 <c>available()</c> 不成立时置灰；菜单选完即关，它是一次性命令，不是持久开关）。
    /// </summary>
    private FrameworkElement FillRegions()
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label("填充"));

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var caption = new TextBlock
        {
            Text = "填充区域",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
        };
        var chevron = new ShapePath
        {
            Data = (Geometry)FindResource("IconChevronDown"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x59, 0x59, 0x59)),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(caption);
        content.Children.Add(chevron);

        var button = new Button
        {
            Style = (Style)FindResource("BarPill"),
            Content = content,
            // 首帧就是「还没有识别结果」（上游 available() = false）：别等第一次 UpdateChrome
            IsEnabled = false,
            Opacity = 0.45,
            ToolTip = "还没有识别结果：先在选区里等一下（识别中会扫过一道光带）",
        };
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : 0.45;
        button.Click += (_, e) =>
        {
            if (!button.IsEnabled) return;

            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Top };
            foreach (var category in RegionCategories.All)
            {
                string picked = category;
                var item = new MenuItem { Header = RegionCategories.Label(category) };
                item.Click += (_, _) => AutoFilterFillRequested?.Invoke(picked);
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
            e.Handled = true;
        };

        _fillRegionsButton = button;
        return host;
    }

    /// <summary>
    /// 「填充区域」能不能点：识别到区域且选区没变过（上游 <c>available()</c>）。
    /// 工具栏不认识识别结果，由覆盖层在检测完成 / 选区变化时告诉它。
    /// </summary>
    internal void SetAutoFilterState(bool available)
    {
        if (_fillRegionsButton == null) return;
        _fillRegionsButton.IsEnabled = available;
        _fillRegionsButton.ToolTip = available
            ? "按类型一次填满所有识别到的区域（用当前打码方式与强度）"
            : "还没有识别结果：先在选区里等一下（识别中会扫过一道光带）";
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
            set(Math.Clamp(RoundTo(get() - step, step), min, max));
            RefreshValues();
            StyleChanged?.Invoke();
        };
        plus.Click += (_, _) =>
        {
            set(Math.Clamp(RoundTo(get() + step, step), min, max));
            RefreshValues();
            StyleChanged?.Invoke();
        };

        // 滚轮在数值上直接微调（照 Snow Shot：光标停在参数上滚就是调它）；
        // 点一下数值复位——「调过头了」比「找不到恢复按钮」常见得多。
        host.MouseWheel += (_, e) =>
        {
            double next = Math.Clamp(RoundTo(get() + (e.Delta > 0 ? step : -step), step), min, max);
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

    /// <summary>按当前样式刷新所有参数格（覆盖层改了样式——如序号自增——也要能把界面拉回来）。</summary>
    public void RefreshValues()
    {
        if (_style == null) return;
        foreach (var refresh in _refreshers) refresh();
    }

    /// <summary>按步长取整（步长 &lt; 1 时保留小数，别被 <c>Math.Round</c> 吃掉 0.1）。</summary>
    private static double RoundTo(double value, double step) =>
        step >= 1 ? Math.Round(value) : Math.Round(Math.Round(value / step) * step, 3);

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
