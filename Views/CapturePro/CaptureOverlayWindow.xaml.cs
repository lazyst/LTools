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

    private readonly BitmapSource _shot;
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

    /// <summary>导出用的成品（按选区重绘）。确认之前为 null。</summary>
    public BitmapSource? ResultImage { get; private set; }

    /// <summary>完成动作选了「贴图」：覆盖层关掉之后由 <see cref="CaptureSession"/> 建贴图窗口。</summary>
    public bool PinRequested { get; private set; }

    /// <summary>完成动作选了「识别」：覆盖层关掉之后由 <see cref="CaptureSession"/> 开识别结果窗。</summary>
    public bool RecognizeRequested { get; private set; }

    /// <summary>完成动作选了「长截图」：覆盖层关掉之后由 <see cref="CaptureSession"/> 滚屏拼接。</summary>
    public bool ScrollRequested { get; private set; }

    /// <summary>这一次是怎么完成的（采集历史记「完成动作」用）。</summary>
    internal FinishKind Finish { get; private set; } = FinishKind.Copy;

    /// <summary>内容真源：底图 + 选区 + 标注 + 撤销栈。</summary>
    internal CaptureDocument Document => _doc;

    public CaptureOverlayWindow(BitmapSource shot, System.Drawing.Rectangle area, double sx, double sy,
        bool editOnly = false)
    {
        InitializeComponent();

        _shot = shot;
        _area = area;
        _sx = sx > 0 ? sx : 1;
        _sy = sy > 0 ? sy : 1;
        _doc = new CaptureDocument(shot, new Int32Rect(area.Left, area.Top, area.Width, area.Height), _sx);
        Surface.Document = _doc;
        EditOnly = editOnly;

        // 按目标显示器的 DPI 把物理矩形换算成 DIP：窗口正好盖住它（Loaded 里再按物理像素钉一次）
        Left = area.Left / _sx;
        Top = area.Top / _sy;
        Width = Math.Max(1, area.Width / _sx);
        Height = Math.Max(1, area.Height / _sy);

        // 「贴图内标注」：图就是这张贴图，没有框选这一步——选区一开始就框满整张
        if (editOnly)
        {
            _doc.Area = new Int32Rect(area.Left, area.Top, area.Width, area.Height);
            _sel = new Rect(0, 0, Width, Height);
        }

        BuildGrips();
        WireToolbar();
        DpiChanged += (_, _) => { PinToMonitor(); SyncDocumentScale(); };
        UpdateCursorForTool();
    }

    /// <summary>
    /// 「贴图内标注」：覆盖层只当这张图的编辑器——动作栏收起，完成时只把成品交回调用方
    /// （不复制、不贴图、不识别、不记采集历史，见 <see cref="CaptureSession.TryEdit"/>）。
    /// </summary>
    internal bool EditOnly { get; }

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
            UpdateHover(Clamp(dip));
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // 文字输入中：Enter / Esc / 方向键都归输入框（本处理器在隧道阶段，比输入框先到）
        if (_textEditing) return;

        if (e.Key == Key.Escape)
        {
            StepBack();
            e.Handled = true;
            return;
        }

        // , / . 翻采集历史（Snow Shot 的「上一张 / 下一张」）；带 Ctrl 时留给别的用途
        if (e.Key is Key.OemComma or Key.OemPeriod &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            StepHistory(e.Key == Key.OemPeriod ? 1 : -1);
            e.Handled = true;
            return;
        }

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
            if (_polyActive) CompletePolyline();                 // 折线：Enter 收尾
            else if (_sel is Rect enterSel && IsValid(enterSel)) Accept(FinishKind.Copy);
            else if (_hoverTarget is SnapBox hover)
            {
                // 还没框选时按 Enter = 选中当前高亮的窗口 / 控件，再按一次才确定
                _sel = ToDipRect(hover.Bounds);
                UpdateChrome();
            }
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.C)
        {
            Accept(FinishKind.Copy);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            Accept(FinishKind.QuickSave);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.S)
        {
            Accept(FinishKind.Save);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.P)
        {
            Accept(FinishKind.Print);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.T)
        {
            Accept(FinishKind.Pin);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.R)
        {
            Accept(FinishKind.Recognize);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.L)
        {
            Accept(FinishKind.Scroll);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.A)
        {
            _drag = DragMode.None;
            _sel = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
            UpdateChrome();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Z)
        {
            _doc.Undo();
            RefreshMarks();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Y)
        {
            _doc.Redo();
            RefreshMarks();
            e.Handled = true;
            return;
        }

        // 字母选工具（Ctrl 组合已经全部处理完，这里只看裸字母）
        if (!ctrl && ToolCatalog.ForKey(e.Key) is CaptureTool tool)
        {
            SetTool(tool);
            e.Handled = true;
            return;
        }

        if (_sel is not Rect sel || _drag != DragMode.None) return;

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

        _sel = MoveTo(sel, delta);
        UpdateChrome();
        e.Handled = true;
    }

    // ============ 完成动作 ============

    internal enum FinishKind { Copy, Save, QuickSave, Print, Pin, Recognize, Scroll }

    /// <summary>「✓ 复制」= Enter / 双击的默认出口（P0 不加配置域字段，默认就是复制）。</summary>
    private void Copy_Click(object sender, RoutedEventArgs e) => Accept(FinishKind.Copy);

    /// <summary>「贴图」= 把成品钉在屏幕上（Ctrl+T）。</summary>
    private void Pin_Click(object sender, RoutedEventArgs e) => Accept(FinishKind.Pin);

    /// <summary>「识别」= 文字 / 表格 / 二维码，结果窗在覆盖层关掉之后开（Ctrl+R）。</summary>
    private void Recognize_Click(object sender, RoutedEventArgs e) => Accept(FinishKind.Recognize);

    /// <summary>「长截图」= 滚屏拼接，循环在覆盖层关掉之后跑（Ctrl+L）。</summary>
    private void Scroll_Click(object sender, RoutedEventArgs e) => Accept(FinishKind.Scroll);

    private void Save_Click(object sender, RoutedEventArgs e) => Accept(FinishKind.Save);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Window_Cancel(object sender, MouseButtonEventArgs e)
    {
        // 右键落在浮动工具栏上：那是它的布局菜单（格子 / 收起来的工具），不是「取消」。
        // 这里必须**不**置 Handled：置了隧道路由就断在窗口这一层，工具栏收不到这次右键。
        if (IsOnToolbar(e.OriginalSource as DependencyObject)) return;

        e.Handled = true;

        // 打字中右键 = 放弃这段文字；其余逐级往回退（右键在有选区时直接取消这次框选）
        if (_textEditing) CancelText();
        else StepBack(preferSelection: true);
    }

    /// <summary>先按当前选区重绘成品，再按出口复制 / 贴图 / 识别 / 另存为；任一步失败都留在覆盖层里继续改。</summary>
    private void Accept(FinishKind kind)
    {
        // 「贴图内标注」只有「完成」一个出口：别的（另存为 / 打印 / 贴图 / 识别 / 长截图）在这里没意义
        if (EditOnly && kind != FinishKind.Copy) return;

        if (_sel is not Rect sel || !IsValid(sel)) return;
        SyncArea();
        if (!_doc.HasArea) return;

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

        if (EditOnly)
        {
            // 贴图内标注：只把成品交回调用方（不复制 / 不贴图 / 不识别 / 不记历史）
            ResultImage = image;
            Finish = kind;
            _confirmed = true;
            try { DialogResult = true; }
            catch (InvalidOperationException) { Close(); }
            return;
        }

        switch (kind)
        {
            case FinishKind.Save:
                if (!CaptureSave.TrySave(this, image)) return;   // 取消另存为 = 留在覆盖层，继续改
                break;

            case FinishKind.QuickSave:
                // 快速保存不弹框：失败就留在覆盖层（提示已经由 CaptureSave 发出）
                if (!CaptureSave.TryQuickSave(image)) return;
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
                break;

            case FinishKind.Scroll:
                // 长截图：滚屏要一段一段来，同样等覆盖层关掉之后再跑（见 CaptureSession）
                ScrollRequested = true;
                break;

            default:
                try
                {
                    NativeClipboard.SetImage(image);
                }
                catch (Exception ex)
                {
                    CrashLog.Write("CapturePro.Copy", ex);
                    TrayService.Notify("复制截图失败：" + ex.Message);
                    return;
                }
                break;
        }

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

    /// <summary>确认过（不是 Esc / 右键退出）。</summary>
    public bool Confirmed => _confirmed;
}
