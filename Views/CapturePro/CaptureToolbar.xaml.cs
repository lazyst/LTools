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
using LTools.Features.CapturePro.ImageEdit;
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
    private ToolbarMode _mode = ToolbarMode.Capture;

    /// <summary>参数框选（<c>SelectionOnly</c>）：工具条右端多一组「使用这块区域 · 取消」。</summary>
    private bool _pickMode;

    /// <summary>图片编辑窗的「平移」工具（上游 <c>MoveToolPresentation::Pan</c>）：本窗独有的第 0 格。</summary>
    private RadioButton? _panBtn;

    /// <summary>图片编辑窗的两个裁剪模式（可勾，互斥；再点一次 = 退出裁剪）。</summary>
    private ToggleButton? _cropRowsBtn, _cropColumnsBtn;

    /// <summary>
    /// 当前样式：选中标注时是**那一条的显示样式**（参数行显示它自己的值），
    /// 否则是当前工具那一族（<c>_tool</c> 由 <see cref="Sync"/> 维护）。
    /// 选中态的改动只落在临时副本上，由覆盖层合并回该族与那条标注。
    /// </summary>
    private ToolStyle? _style => _styleOverride ?? _styles?.For(_tool);

    /// <summary>选中标注时的显示样式（覆盖层给的临时副本；取消选中 / 换工具就丢掉）。</summary>
    private ToolStyle? _styleOverride;

    /// <summary>选中标注时参数行按哪个工具排（这一族的代表工具；工具按钮仍停在原处）。</summary>
    private CaptureTool? _toolOverride;

    /// <summary>
    /// 多选时**同一族每一条**各自的显示样式：参数行据此把「各条不一样」的那几项显示成
    /// 「多个值」（照上游 <c>Mixed</c> 位）。空 / 只有一条 = 没有混合态。
    /// </summary>
    private IReadOnlyList<ToolStyle>? _stylePeers;

    /// <summary>参数表的主人：选中标注时是那一族的代表工具，否则就是当前工具。</summary>
    private CaptureTool ParamTool => _toolOverride ?? _tool;

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
        Translate,
        Cancel,
        Copy,

        /// <summary>全屏画布：退出（照上游 <c>globalCanvasExitRequested</c>）。</summary>
        Exit,
    }

    /// <summary>点了某个完成动作（由覆盖层决定具体做什么）。</summary>
    public event Action<CaptureAction>? ActionRequested;

    /// <summary>全屏画布：点了激光笔（纯开关，切换由宿主做）。</summary>
    internal event Action? LaserToggleRequested;

    /// <summary>全屏画布：点了点击穿透（纯开关，切换由宿主做）。</summary>
    internal event Action? ClickThroughToggleRequested;

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

    /// <summary>「显示二维码」被点：参数 = 勾上还是取消（照上游 <c>qrCodeVisibilityRequested</c>）。</summary>
    public event Action<bool>? QrVisibilityRequested;

    /// <summary>图片编辑窗点了「平移」：宿主进入平移模式（绘图工具全部取消选中）。</summary>
    internal event Action? PanRequested;

    /// <summary>图片编辑窗勾了裁剪行 / 裁剪列（null = 退出裁剪，回到默认工具）。</summary>
    internal event Action<CropAxis?>? CropRequested;

    internal CaptureTool CurrentTool { get; private set; } = CaptureTool.Select;

    public CaptureToolbar()
    {
        InitializeComponent();
    }

    /// <summary>用户自己拖过位置（之后不再跟着默认落点跑；双击复位会清掉）。</summary>
    public bool UserMoved { get; private set; }

    /// <summary>按样式与当前工具建一次按钮（覆盖层构造完调）。</summary>
    /// <param name="mode">
    /// <see cref="ToolbarMode.Capture"/> = 截图覆盖层（画标注 + 完成动作）；
    /// <see cref="ToolbarMode.Edit"/> = 图片编辑窗（照上游 <c>editorOptions()</c>：多一个「平移」工具与
    /// 裁剪行 / 裁剪列两格，完成动作是识别 / 复制 / 贴图 / 打印 / 快速保存 / 另存为，
    /// 没有长截图 / 录屏 / 取消）。
    /// </param>
    internal void Init(ToolStyleSet styles, CaptureTool current, ToolbarMode mode = ToolbarMode.Capture,
        bool pickMode = false)
    {
        if (_built) return;
        _built = true;
        _styles = styles;
        _mode = mode;
        _pickMode = pickMode;

        if (_mode == ToolbarMode.Edit) BuildPanButton();

        BuildCells(ToolLayoutStore.Current);
        BuildActions();
        BuildParams();

        Sync(current);
        RefreshValues();
    }

    /// <summary>工具栏的两种形态（截图覆盖层 / 图片编辑窗 / 全屏画布）。</summary>
    internal enum ToolbarMode
    {
        Capture,
        Edit,

        /// <summary>
        /// 全屏画布（照上游 <c>canvasOptions()</c>）：只有不依赖底图的绘图工具，
        /// 完成动作是撤销 / 重做 → 激光笔 / 点击穿透 → 退出。
        /// </summary>
        GlobalCanvas,
    }

    /// <summary>界面跟着工具的默认状态对齐（覆盖层切工具时调，会同步按钮的选中态）。</summary>
    internal void Sync(CaptureTool tool)
    {
        _tool = tool;
        CurrentTool = tool;

        // 回到「按工具创建」的样式：选中标注时的临时显示样式作废
        _styleOverride = null;
        _toolOverride = null;
        _stylePeers = null;

        // 选中哪个工具，它所在的那一格就显示哪个（同格里切换时图标跟着换）
        var cell = CellOf(tool);
        if (cell != null) cell.Current = tool;

        _syncing = true;
        foreach (var c in _cells)
        {
            ShowCellIcon(c);
            c.Button.IsChecked = ReferenceEquals(c, cell);
        }
        if (_panBtn != null) _panBtn.IsChecked = false;    // 选了绘图工具 = 退出平移
        _syncing = false;

        ApplyParamVisibility(tool);
        ApplyQrVisibility();
        RefreshValues();
    }

    /// <summary>
    /// 选中一条标注：参数行切到它那一族，显示**这一条自己的值**（上游 <c>Selected*</c> 样式来源）。
    /// 工具按钮**不动**（还停在「选择」），只切参数条——与上游「选中元素时工具不变、样式来源变」一致。
    /// 多选时 <paramref name="peers"/> 是同一族每一条的显示样式，用来把互相不一样的项显示成「多个值」。
    /// </summary>
    internal void SyncSelection(CaptureTool tool, ToolStyle style, IReadOnlyList<ToolStyle>? peers = null)
    {
        _toolOverride = tool;
        _styleOverride = style;
        _stylePeers = peers is { Count: > 1 } ? peers : null;
        ApplyParamVisibility(tool);
        RefreshValues();
    }

    /// <summary>取消选中：参数行还给当前工具（丢掉临时的显示样式与混合态）。</summary>
    internal void ClearSelectionStyle()
    {
        if (_styleOverride == null && _toolOverride == null && _stylePeers == null) return;
        _styleOverride = null;
        _toolOverride = null;
        _stylePeers = null;
        ApplyParamVisibility(_tool);
        RefreshValues();
    }

    /// <summary>参数行按哪几格显示（<paramref name="tool"/> = 参数表的主人，不一定等于当前工具）。</summary>
    private void ApplyParamVisibility(CaptureTool tool)
    {
        foreach (var (param, host) in _paramHosts)
            host.Visibility = ToolCatalog.Uses(tool, param) ? Visibility.Visible : Visibility.Collapsed;

        ParamBar.Visibility = ToolCatalog.Of(tool).Params.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>改主色：选中标注时只改显示样式（合并回创建样式由覆盖层做，含「共享颜色」传播）。</summary>
    private void SetPrimaryColor(Color color)
    {
        if (_styleOverride != null)
        {
            _styleOverride.Color = color;
            return;
        }

        _styles?.SetColor(StyleFamilies.Of(_tool), color);
    }

    /// <summary>换一份布局（右键菜单 / 自检夹具调）：重建格子，当前工具的选中态跟着走。</summary>
    internal void ApplyLayout(ToolLayout layout)
    {
        foreach (var popup in _groupPopups.Values) popup.IsOpen = false;
        _groupPopups.Clear();

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
    internal void FitWidth(double width)
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

}
