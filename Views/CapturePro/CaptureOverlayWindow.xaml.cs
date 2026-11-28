using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 截图覆盖层：冻结画面 + 框选 + 标注 + 完成动作。
/// </summary>
/// <remarks>
/// <para>
/// 窗口正好盖住**所有显示器的并集**（跨屏选区）：所有坐标在窗口里是 DIP，
/// 交出去之前统一换算成「屏幕物理像素 + 左上角偏移」，与截图 / 找图 / 鼠标注入同一套坐标系。
/// 画面部分见 <see cref="CaptureCanvas"/>，标注模型见 <see cref="CaptureDocument"/>。
/// </para>
/// <para>
/// 本文件只放生命周期、键盘、完成动作；框选拖动在 <c>.Selection.cs</c>，
/// 窗口 / 控件探测在 <c>.Hover.cs</c>，界面元素的摆放与显隐在 <c>.Chrome.cs</c>。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow : Window
{
    private const double MinSize = 4;      // 选区最小边长（DIP）

    private BitmapSource _shot;
    private readonly System.Drawing.Rectangle _area;   // 覆盖的物理矩形（多屏时是整个虚拟桌面）
    private readonly double _sx, _sy;      // 物理像素 / DIP
    private readonly CaptureDocument _doc;
    private readonly List<Rectangle> _grips = new();

    private IntPtr _hwnd;

    private Rect? _sel;                    // 选区（窗口 DIP）
    private DragMode _drag;
    private FrameHandle _handle;
    private Point _origin;                 // 本次拖动起点
    private Rect _originRect;              // 本次拖动开始时的选区

    private SelectionMode _shape = SelectionMode.Rect;   // 下一次拖框画什么形状（打开时按设置里的「选区形状」恢复）
    private readonly List<SelectionRegion> _regions = new();   // 已确定的选区（窗口 DIP；加区并、减区挖）
    private List<SelectionRegion>? _originRegions;       // 本次移动 / 缩放开始时的每一块（取消时还原）
    private RegionMode _mode;                            // 本次拖出来的是加区还是减区
    private Rect? _liveRect;                             // 正在拖的那一块矩形（窗口 DIP）
    private bool _squareDrag;                            // 还没有选区时 Shift 锁正方形
    private bool _replaceOnCommit;                       // 无修饰键拖到选区外 = 松手整块重换
    private bool _moveFromShadow;                        // 这一下是从压暗的阴影上按下去「挪整块选区」的
    private readonly List<Point> _lasso = new();         // 自由选区的点（按住拖动时）
    private readonly List<Point> _selPoly = new();       // 折线选区的点（逐点单击）
    private Point? _selPolyCursor;                       // 折线还没落下的那一段（跟手虚线）
    private bool _selPolyActive;

    /// <summary>导出用的成品（按选区重绘）。确认之前为 null。</summary>
    public BitmapSource? ResultImage { get; private set; }

    /// <summary>完成动作选了「贴图」：覆盖层关掉之后由 <see cref="CaptureSession"/> 建贴图窗口。</summary>
    public bool PinRequested { get; private set; }

    /// <summary>完成动作选了「识别」：覆盖层关掉之后由 <see cref="CaptureSession"/> 开识别结果窗。</summary>
    public bool RecognizeRequested { get; private set; }

    /// <summary>完成动作选了「识别并复制」：识别结果窗算完直接把全文写进剪贴板。</summary>
    public bool RecognizeCopyRequested { get; private set; }

    /// <summary>
    /// 文件名模板里 <c>{AN}</c> / <c>{WN}</c> 的取值：抓图那一刻的前台程序 / 窗口
    /// （由 <see cref="CaptureSession"/> 在收起自己的窗口之前记下来）。
    /// </summary>
    public ExportNameContext ExportNames { get; set; } = ExportNameContext.Empty;

    /// <summary>完成动作选了「识别并翻译」：识别结果窗算完自动翻一遍（照上游 <c>captureAndTranslateText</c>）。</summary>
    public bool TranslateRequested { get; private set; }

    /// <summary>长截图完成：<see cref="ResultImage"/> 就是拼好的长图，覆盖层关掉之后由 <see cref="CaptureSession"/> 复制走。</summary>
    public bool ScrollRequested { get; private set; }

    /// <summary>完成动作选了「实时」：覆盖层关掉之后贴一块跟着源窗口刷新的图。</summary>
    public bool LiveRequested { get; private set; }

    /// <summary>
    /// 完成动作选了「录屏」：这次不是截图，只是借覆盖层挑一块区域。
    /// 覆盖层关掉之后由 <see cref="CaptureSession"/> 按 <see cref="Document"/> 的区域去开录
    /// （自己留在画面上会被一起录进去），<b>不记采集历史</b>。
    /// </summary>
    public bool RecordRequested { get; private set; }

    /// <summary>这一次是怎么完成的（采集历史记「完成动作」用）。</summary>
    internal FinishKind Finish { get; private set; } = FinishKind.Copy;

    /// <summary>
    /// 快捷入口带进来的「选完就做」动作（照 Snow Shot 的 <c>PendingSelectionAction</c>）：
    /// 「截图并贴图 / 另存为 / 快速保存 / 识别」这类入口不在工具条上等人点，框一落定就执行。
    /// </summary>
    /// <remarks>
    /// <see cref="PendingAction.None"/> = 普通截图（工具条 + 标注，Enter / 双击走「复制」）。
    /// 动作失败（另存为被取消、识别没开起来）时**留在覆盖层**并退回普通编辑 ——
    /// 不会出现「工具条被收起、又没完成」的死角。
    /// </remarks>
    public enum PendingAction { None, Copy, Pin, Save, QuickSave, Recognize, RecognizeCopy, Translate, Scrolling }

    private readonly PendingAction _pending;

    /// <summary>这一次是「识别并翻译」入口：用户即便手动按 Enter 进结果窗，也算一次翻译请求。</summary>
    /// <remarks>「截图并翻译」的快捷键（<c>text_translation</c>）会在确认前把它打开。</remarks>
    private bool _translate;

    /// <summary>右键取消 / 指针动作的「按下武装、抬起才做」（照上游 MouseReleaseActionController）。</summary>
    private readonly MouseReleaseGate _release;

    /// <summary>试过一次了：失败后立刻退回普通编辑（工具条回来，Enter 仍走它自己的出口）。</summary>
    private bool _pendingDone;

    /// <summary>还有没执行过的「选完就做」：工具条先不摆出来。</summary>
    internal bool HasPending => _pending != PendingAction.None && !_pendingDone;

    /// <summary>快捷入口这一次的默认出口（Enter / 双击 / 直接确认）。</summary>
    internal FinishKind DefaultFinish => _pending switch
    {
        PendingAction.Pin => FinishKind.Pin,
        PendingAction.Save => FinishKind.Save,
        PendingAction.QuickSave => FinishKind.QuickSave,
        PendingAction.Recognize => FinishKind.Recognize,
        PendingAction.RecognizeCopy => FinishKind.RecognizeCopy,
        PendingAction.Translate => FinishKind.Recognize,
        _ => FinishKind.Copy,
    };

    /// <summary>内容真源：底图 + 选区 + 标注 + 撤销栈。</summary>
    internal CaptureDocument Document => _doc;

    public CaptureOverlayWindow(BitmapSource shot, System.Drawing.Rectangle area, double sx, double sy,
        bool editOnly = false, PendingAction pending = PendingAction.None, bool selectionOnly = false)
    {
        InitializeComponent();

        _shot = shot;
        _area = area;
        _sx = sx > 0 ? sx : 1;
        _sy = sy > 0 ? sy : 1;
        _pending = editOnly || selectionOnly ? PendingAction.None : pending;
        _translate = _pending == PendingAction.Translate;
        _doc = new CaptureDocument(shot, new Int32Rect(area.Left, area.Top, area.Width, area.Height), _sx);
        Surface.Document = _doc;
        EditOnly = editOnly;
        SelectionOnly = selectionOnly;
        InitSelectionStyle();
        InitSmartSelection();
        // 上次用的选区形状（照上游 screenshot_selection/region_type：跨会话记住）
        _shape = SelectionStore.RegionType;

        // 按目标显示器的 DPI 把物理矩形换算成 DIP：窗口正好盖住它（Loaded 里再按物理像素钉一次）
        Left = area.Left / _sx;
        Top = area.Top / _sy;
        Width = Math.Max(1, area.Width / _sx);
        Height = Math.Max(1, area.Height / _sy);

        // 「贴图内标注」：图就是这张贴图，没有框选这一步——选区一开始就框满整张
        if (editOnly)
        {
            _doc.Area = new Int32Rect(area.Left, area.Top, area.Width, area.Height);
            SetSingleRect(new Rect(0, 0, Width, Height));
        }

        BuildGrips();
        InitOverlayUi();
        // 参数框选只要「这一块原样的像素」：把用户存过的截图外观（圆角 / 阴影 / 边框）清掉，
        // 否则取回去的模板图会多出一圈留白，找图匹配直接用不了。
        if (selectionOnly) _doc.Style = SelectionStyle.None;
        // 「记住上次工具」（默认关）：新会话直接从上次的绘图工具开始，否则仍是「选择」
        _tool = CaptureToolMemory.Start(CaptureTool.Select);
        WireToolbar();
        // 鼠标指针单独存一份（照上游 screenshot/capture_cursor）：底图保持干净，覆盖层里能随时开关
        LoadCursorSnapshot(ScreenshotBehavior.CaptureCursor);
        InitQr();
        DpiChanged += (_, _) => { PinToMonitor(); SyncDocumentScale(); };
        _release = new MouseReleaseGate(this);
        UpdateCursorForTool();
    }

    /// <summary>
    /// 「贴图内标注」：覆盖层只当这张图的编辑器——完成动作那一截收起，完成时只把成品交回调用方
    /// （不复制、不贴图、不识别、不记采集历史，见 <see cref="CaptureSession.TryEdit"/>）。
    /// </summary>
    internal bool EditOnly { get; }

    /// <summary>
    /// 「只取选区」：给参数框选（屏幕区域 / 模板图）用——这张图不是成品，
    /// 所以不出完成动作、不复制、不贴图、不识别、不记采集历史，
    /// Enter / 双击只把区域和一张 png 交回调用方（见 <see cref="CaptureSession.TryCapture"/>）。
    /// </summary>
    internal bool SelectionOnly { get; }

    // ============ 生命周期 ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        PinToMonitor();
        SyncDocumentScale();

        Activate();
        Focus();

        UpdateLayout();
        PlaceHint();

        // 窗口树枚举很快（几百个句柄），打开时一次即可；UIA 的那一层在悬停时按需补
        try
        {
            _windowTargets.AddRange(ScreenSnapTargets.FromWindows(_area));
            RebuildSnapLines();
        }
        catch { /* 探测失败不影响手动框选 */ }

        UpdateChrome();

        // 打开时鼠标本来就停在某个窗口上：先算一次悬停，省得用户要动一下才看见高亮
        if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip))
        {
            var at = Clamp(dip);
            _guideCursor = at;
            Surface.GuideCursor = at;
            Surface.InvalidateVisual();
            UpdateHover(at);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // 文字输入中：Enter / Esc / 方向键都归输入框（本处理器在隧道阶段，比输入框先到）
        if (_textEditing) return;

        // Alt / Shift 轻按有独立语义（参考线开关 / 换色值格式），按下先记一笔
        TrackModifierDown(e);

        // 长截图模式：只有「完成 / 放弃 / 自动滚动」几个出口，别的键一律吃掉（别让工具快捷键改状态）
        if (_scrollMode)
        {
            bool ctrlDown = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.Key == Key.Enter || (ctrlDown && e.Key == Key.C)) FinishScroll();
            else if (e.Key == Key.Escape) CancelScroll();
            else if (e.Key == Key.Space) ToggleAutoScroll();
            e.Handled = true;
            return;
        }

        // 二维码气泡：Esc 只关气泡并把焦点还给标记；焦点在气泡里时按键全交给文本框
        // （Ctrl+C 复制选中文本，别被下面的「复制整张截图」抢走）。照上游 eventFilter 的 Escape 分支。
        if (_qrBubbleOpen)
        {
            if (e.Key == Key.Escape)
            {
                var marker = _qrBubbleMarker;
                HideQrBubble();
                if (marker != null) Keyboard.Focus(marker);
                e.Handled = true;
                return;
            }

            if (QrBubbleCard.IsKeyboardFocusWithin) return;
        }

        // 焦点在二维码标记上（Esc 关掉气泡之后）：Enter / 空格重新打开气泡
        if (!_qrBubbleOpen && Keyboard.FocusedElement is Button focusedQr && _qrMarkers.Contains(focusedQr) &&
            e.Key is Key.Enter or Key.Space)
        {
            ShowQrBubble(focusedQr, focus: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            // 比例下拉开着：Esc 只关下拉，别顺手把选区 / 覆盖层也退掉
            if (IsAspectMenuOpen)
            {
                CloseAspectMenu();
                e.Handled = true;
                return;
            }

            // 上游 Esc = cancel_screenshot：直接退出这次截图。**不是**「退一步」——
            // 右键才是逐级往回收（上游 handleRightClick：预选态退出 / 有选区时回 Move）。
            RequestCancelViaShortcut();
            e.Handled = true;
            return;
        }

        // , / . 翻采集历史、Tab 切智能选区目标都由快捷键表派发（见下面 TryCaptureShortcut）

        if (e.Key is Key.Delete or Key.Back)
        {
            if (_selectedIndex >= 0)
            {
                DeleteSelectedMark();
                e.Handled = true;
            }
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (e.Key == Key.Enter)
        {
            if (_selPolyActive) CompleteSelectionPolyline();     // 折线选区：Enter 收尾
            else if (_polyActive) CompletePolyline();            // 折线：Enter 收尾
            else if (_sel is Rect enterSel && IsValid(enterSel)) Accept(DefaultFinish);
            else if (_hoverTarget is SnapBox hover)
            {
                // 还没框选时按 Enter = 选中当前高亮的窗口 / 控件，再按一次才确定
                SetSingleRect(ToDipRect(hover.Bounds));
                UpdateChrome();
            }
            e.Handled = true;
            return;
        }

        // 可配置的快捷键（覆盖层 + 绘图工具）：默认值照 Snow Shot，用户改过就按用户的。
        // 放在 Enter / Delete 之后，所以「确认」「删标注」这两个最常用的不受重绑影响。
        if (TryCaptureShortcut(e))
        {
            e.Handled = true;
            return;
        }

        // ---- LTools 自己的固定入口（上游没有对应项）----

        // Ctrl+A = 全屏（上游也是固定绑定 screenshot.select_current_screen）
        if (ctrl && e.Key == Key.A)
        {
            SelectWholeScreen();
            e.Handled = true;
            return;
        }

        // Ctrl+W = 实时贴图（跟着源窗口刷新；与 Ctrl+Shift+S 这类不相邻，避免手误）
        if (ctrl && e.Key == Key.W)
        {
            Accept(FinishKind.Live);
            e.Handled = true;
            return;
        }

        if (_sel is not Rect sel || _drag != DragMode.None) return;

        // 预选态的方向键不挪这块选区（上游那是「移动光标」的快捷键，LTools 没有光标导航）
        if (SmartPreviewActive && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            e.Handled = true;
            return;
        }

        // 方向键逐像素微调；按住 Shift 每次 10px
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        Vector delta = e.Key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => default,
        };
        if (delta == default) return;

        var moved = MoveTo(sel, delta);
        var shift = new Vector(moved.X - sel.X, moved.Y - sel.Y);
        for (int i = 0; i < _regions.Count; i++) _regions[i] = _regions[i].Offset(shift);
        _sel = moved;

        UpdateChrome();
        e.Handled = true;
    }

    /// <summary>修饰键抬起：Alt 轻按切参考线，Shift 轻按在取色器里换色值格式。</summary>
    private void Window_KeyUp(object sender, KeyEventArgs e) => TrackModifierUp(e);

    // ============ 完成动作 ============

    internal enum FinishKind { Copy, Save, QuickSave, Print, Pin, Recognize, RecognizeCopy, Scroll, Live, Record }

    /// <summary>
    /// 工具条右半边那些完成动作（照 Snow Shot：工具、撤销、完成动作在**一条**里，
    /// 用竖线分组）。键盘入口（Enter / Ctrl+S…）走各自的快捷键，这里只服务鼠标。
    /// </summary>
    private void OnToolbarAction(CaptureToolbar.CaptureAction action)
    {
        switch (action)
        {
            case CaptureToolbar.CaptureAction.Copy: Accept(FinishKind.Copy); break;            // ✓ = Enter / 双击的默认出口
            case CaptureToolbar.CaptureAction.Pin: Accept(FinishKind.Pin); break;              // 贴图（Ctrl+T）
            case CaptureToolbar.CaptureAction.Live: Accept(FinishKind.Live); break;            // 实时贴图（Ctrl+W）
            case CaptureToolbar.CaptureAction.Recognize: Accept(FinishKind.Recognize); break;  // 识别（Ctrl+R）
            // 识别那一叠的其它入口：同一个结果窗，只是打开就停在不同页（识别本身在窗口里跑）
            case CaptureToolbar.CaptureAction.RecognizeTable:
                _resultTab = "table";
                Accept(FinishKind.Recognize);
                break;
            case CaptureToolbar.CaptureAction.RecognizeDocument:
                _resultTab = "document";
                Accept(FinishKind.Recognize);
                break;
            case CaptureToolbar.CaptureAction.RecognizeBarcode:
                _resultTab = "qr";
                Accept(FinishKind.Recognize);
                break;
            case CaptureToolbar.CaptureAction.ConvertMarkdown:
                _resultTab = "markdown";
                Accept(FinishKind.Recognize);
                break;
            case CaptureToolbar.CaptureAction.ConvertLatex:
                _resultTab = "latex";
                Accept(FinishKind.Recognize);
                break;
            case CaptureToolbar.CaptureAction.ConvertHtml:
                _resultTab = "html";
                Accept(FinishKind.Recognize);
                break;
            case CaptureToolbar.CaptureAction.Translate:
                // 「识别并翻译」：结果窗算完自动按设置里的目标语言翻一遍（上游 captureAndTranslateText）
                _translate = true;
                Accept(FinishKind.Recognize);
                break;
            case CaptureToolbar.CaptureAction.Save: Accept(FinishKind.Save); break;            // 另存为…（Ctrl+S）
            case CaptureToolbar.CaptureAction.QuickSave: Accept(FinishKind.QuickSave); break;  // 快速保存（Ctrl+Shift+S）
            case CaptureToolbar.CaptureAction.Print: Accept(FinishKind.Print); break;          // 打印（Ctrl+P）
            case CaptureToolbar.CaptureAction.Scroll: StartScrollMode(); break;                // 长截图：覆盖层不关，用户自己滚
            case CaptureToolbar.CaptureAction.Record: Accept(FinishKind.Record); break;        // 录屏（Ctrl+Shift+R）：选完区域去开录
            case CaptureToolbar.CaptureAction.RecordOptions:                                  // 录屏选项…（默认声音 / 摄像头 / 特效）
                RecordOptionsWindow.Show(this);
                break;
            case CaptureToolbar.CaptureAction.FullScreen: SelectWholeScreen(); break;          // 全屏（Ctrl+A）
            case CaptureToolbar.CaptureAction.HistoryPrev: StepHistory(-1); break;            // 采集历史：上一张（,）
            case CaptureToolbar.CaptureAction.HistoryNext: StepHistory(1); break;             // 采集历史：下一张（.）
            case CaptureToolbar.CaptureAction.HistoryWindow: OpenHistoryWindow(); break;      // 采集历史窗口…
            case CaptureToolbar.CaptureAction.Cancel: Cancel(); break;
        }
    }

    private void Window_Cancel(object sender, MouseButtonEventArgs e)
    {
        // 右键落在浮动工具栏上：那是它的布局菜单（格子 / 收起来的工具），不是「取消」。
        // 这里必须**不**置 Handled：置了隧道路由就断在窗口这一层，工具栏收不到这次右键。
        if (IsOnToolbar(e.OriginalSource as DependencyObject)) return;

        // 信息条上的右键不取消选区（字段只认左键 / 滚轮）
        if (IsOnSelectionBar(e.OriginalSource as DependencyObject)) return;

        // 比例下拉里的右键：只收起菜单，别顺手退选区
        if (IsOnAspectMenu(e.OriginalSource as DependencyObject))
        {
            CloseAspectMenu();
            e.Handled = true;
            return;
        }

        // 二维码气泡上右键：只收气泡（别当成「退一步」）
        if (IsOnQrBubble(e.OriginalSource as DependencyObject))
        {
            HideQrBubble();
            e.Handled = true;
            return;
        }

        // 二维码标记上右键：什么都不做（左键才是它的入口）
        if (IsOnQr(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        // 长截图模式：右键（面板上除外）= 放弃这次长截图，回到普通框选
        if (_scrollMode)
        {
            if (IsOnScrollPanel(e.OriginalSource as DependencyObject)) return;
            e.Handled = true;
            CancelScroll();
            return;
        }

        e.Handled = true;

        // 打字中右键 = 放弃这段文字；其余逐级往回退（右键在有选区时直接取消这次框选）
        if (_textEditing) { CancelText(); return; }

        // 「退出这次截图」是最后一档：和上游一样只**武装**，按钮抬起之后一拍才真的关
        // （按下就关的话，抬起还没发出去，下一次点击就落到别的窗口上了）。
        if (StepBack()) _release.Arm(PointerButton.Right, Cancel);
    }

    /// <summary>
    /// 指针动作（双击 / 中键）按设置完成这次截图；<see cref="OverlayPointerAction.None"/> = 什么都不做。
    /// </summary>
    /// <remarks>
    /// 照上游 <c>executeConfiguredCompletionAction</c>：<c>ocr_copy</c> 复用识别入口，
    /// 只是多带一个「算完就复制」的标记；<c>cancel</c> 等于 Esc。
    /// </remarks>
    private void CompleteFromPointer(OverlayPointerAction action)
    {
        switch (action)
        {
            case OverlayPointerAction.None:
                return;

            case OverlayPointerAction.Cancel:
                Cancel();
                return;

            case OverlayPointerAction.Copy:
                Accept(FinishKind.Copy);
                return;

            case OverlayPointerAction.Save:
                Accept(FinishKind.Save);
                return;

            case OverlayPointerAction.QuickSave:
                Accept(FinishKind.QuickSave);
                return;

            case OverlayPointerAction.Pin:
                Accept(FinishKind.Pin);
                return;

            case OverlayPointerAction.Ocr:
                Accept(FinishKind.Recognize);
                return;

            case OverlayPointerAction.OcrCopy:
                Accept(FinishKind.RecognizeCopy);
                return;
        }
    }

    /// <summary>先按当前选区重绘成品，再按出口复制 / 贴图 / 识别 / 另存为；任一步失败都留在覆盖层里继续改。</summary>
    private void Accept(FinishKind kind)
    {
        // 「贴图内标注」只有「完成」一个出口：别的（另存为 / 打印 / 贴图 / 识别 / 长截图）在这里没意义
        if (EditOnly && kind != FinishKind.Copy) return;
        // 「只取选区」同理：这次不是成品图，任何出口都等同于「确定」
        if (SelectionOnly && kind != FinishKind.Copy) return;
        // 用户自己按了出口（Enter / 工具条 / 快捷键）：快捷入口的「选完就做」不再重复执行
        if (_pending != PendingAction.None) _pendingDone = true;

        if (_sel is not Rect sel || !IsValid(sel)) return;

        // 减区把选区挖空了：没有可导出的内容，说清楚而不是导出一张全透明的图
        if (SelectionGeometry() == null)
        {
            ToastService.Show("选区被减区挖空了，没有能导出的内容：再拖一块加区，或右键取消选区",
                "截图", 2600, ToastKind.warning, key: "capture-region-empty");
            return;
        }

        SyncArea();
        if (!_doc.HasArea) return;

        RememberSelection();          // 记「上次选区」：这次提交的几何 + 外观

        // 录屏：这次不导出成品（更不记采集历史），只要区域的物理像素矩形；
        // 覆盖层关掉、自己的窗口还原之后由 CaptureSession 去开录。
        if (kind == FinishKind.Record)
        {
            RecordRequested = true;
            CloseWithResult(kind, null);
            return;
        }

        BitmapSource image;
        try
        {
            image = _doc.Render();
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.Render", ex);
            TrayService.Notify("截图导出失败：" + ex.Message);
            return;
        }

        if (EditOnly || SelectionOnly)
        {
            // 贴图内标注 / 只取选区：只把成品交回调用方（不复制 / 不贴图 / 不识别 / 不记历史）
            CloseWithResult(kind, image);
            return;
        }

        switch (kind)
        {
            case FinishKind.Save:
                if (!CaptureSave.TrySave(this, image, null, ExportNames, _doc.Area)) return;   // 取消另存为 = 留在覆盖层，继续改
                break;

            case FinishKind.QuickSave:
                // 快速保存不弹框：失败就留在覆盖层（提示已经由 CaptureSave 发出）
                if (!CaptureSave.TryQuickSave(image, out _, ExportNames, _doc.Area)) return;
                break;

            case FinishKind.Print:
                if (!CaptureSave.TryPrint(this, image)) return;   // 取消打印 = 留在覆盖层
                break;

            case FinishKind.Pin:
                // 贴图窗口由 CaptureSession 在覆盖层关掉之后建：这里只留出口，
                // 免得「窗口刚建好就被 ShowDialog 关掉时的还原动作拖走」。
                PinRequested = true;
                break;

            case FinishKind.Recognize:
                // 识别结果窗同理：覆盖层关掉、还原自己的窗口之后再开（识别本身在窗口里跑）
                RecognizeRequested = true;
                if (_translate) TranslateRequested = true;      // 「截图并翻译」：算完自动翻一遍
                break;

            case FinishKind.RecognizeCopy:
                // 「识别并复制」：同一个结果窗，算完之后直接把全文写进剪贴板（照上游 text_recognition_copy）
                RecognizeRequested = true;
                RecognizeCopyRequested = true;
                break;

            case FinishKind.Live:
                // 实时贴图：同样等覆盖层关掉之后再建（要取选区中心下面是哪个窗口）
                LiveRequested = true;
                break;

            default:
                // 复制到剪贴板：顺带处理「执行完自动保存」与「把图片文件也复制到剪贴板」；
                // 同时带上外观 / 落位元数据，让「贴剪贴板」能贴回原位置与原外观（P7-e）
                var look = PinAppearance.FromStyle(_doc.Style, _doc.Scale,
                    _doc.Area.Width, _doc.Area.Height);
                var capture = new ClipboardCapture(
                    look.WindowRect(_doc.Area, image.PixelWidth, image.PixelHeight), look);
                if (!CaptureSave.CopyImage(image, ExportSettings.ScenarioCopy, ExportNames, capture, _doc.Area))
                {
                    TrayService.Notify("复制截图失败：剪贴板被占用");
                    return;
                }
                break;
        }

        CloseWithResult(kind, image);
    }

    /// <summary>
    /// 快捷入口（截图并贴图 / 另存为 / 快速保存 / 识别）：选区一落定就执行它的默认动作。
    /// </summary>
    /// <remarks>
    /// 照上游 <c>handleSelectionConfirmed</c> 的 pending action：这几个入口在 Snow Shot 里
    /// 是「拖完直接执行」，工具条根本不摆出来，所以这里也不给标注机会。
    /// 失败（另存为取消 / 识别没开起来）留在覆盖层，下一次 <see cref="UpdateChrome"/> 会把工具条放回来。
    /// </remarks>
    private void FirePendingAction()
    {
        if (!HasPending) return;
        _pendingDone = true;

        // 「截图并长截图」不是导出一个成品，而是把这次框选直接交给长截图模式；
        // 进不去（太小 / 找不到可滚的窗口）就退回普通框选，原因 StartScrollMode 已经说过
        if (_pending == PendingAction.Scrolling)
        {
            if (!StartScrollMode()) UpdateChrome();
            return;
        }

        Accept(DefaultFinish);
        if (_confirmed) return;
        UpdateChrome();
    }

    /// <summary>确认出口：记下结果并按「对话框 / 非模态」两种方式关窗。</summary>
    private void CloseWithResult(FinishKind kind, BitmapSource? image)
    {
        ResultImage = image;
        Finish = kind;
        _confirmed = true;
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); }   // 非模态打开时 DialogResult 不可用
    }

    private bool _confirmed;

    private void Cancel()
    {
        _confirmed = false;
        try { DialogResult = false; }
        catch (InvalidOperationException) { Close(); }
    }

    /// <summary>
    /// Esc（截图快捷键）退出这次截图；设置里开了「退出前确认」时先问一句。
    /// </summary>
    /// <remarks>
    /// 照上游 <c>screenshotshortcutexitconfirmation.cpp</c>：标题「退出截图？」、
    /// 正文「当前截图会被丢弃」、危险按钮「退出」。只有快捷键 / Esc 走这条；
    /// 右键退一步、工具条上的取消按钮都是用户明确点的，不弹确认（上游同样只包住快捷键出口）。
    /// </remarks>
    private void RequestCancelViaShortcut()
    {
        if (!ScreenshotBehavior.ConfirmBeforeExitViaShortcut)
        {
            Cancel();
            return;
        }

        if (!ConfirmDialog.Confirm(this, "退出截图？",
                "当前截图会被丢弃。\n可以在「设置 → 通用 → 截图 Pro · 截图行为」里关掉这个确认。",
                danger: true, okText: "退出"))
            return;

        Cancel();
    }

    /// <summary>确认过（不是 Esc / 右键退出）。</summary>
    public bool Confirmed => _confirmed;
}
