using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图窗口：把截下来的图钉在屏幕上，可拖动 / 缩放 / 调不透明度 / 置顶 / 锁定 / 局部显示 / 缩略图 / 标注。
/// </summary>
/// <remarks>
/// <para>
/// **不抢焦点**：<c>ShowActivated=False</c> + 扩展样式 <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>。
/// 点它、滚它都不该让前台变成自己——否则紧接着注入的键鼠全打在贴图上。
/// 代价是收不到键盘事件（<c>Esc</c> 关不掉），所以出口做成鼠标可达：双击、右上角 ✕、右键菜单。
/// </para>
/// <para>
/// 坐标仍是「窗口 DIP」：截图区域是物理像素，出场时按屏幕缩放换成 DIP（<see cref="PinMath.PlaceAt"/>），
/// 100% 缩放时正好盖住刚才截的那块。滚轮缩放读 <see cref="PinMath"/>，光标下的内容不动。
/// </para>
/// <para>
/// **局部显示**（<see cref="SetViewport"/>）：窗口变成固定大小的取景框，图片在框里平移、超出部分裁掉。
/// 长截图钉在屏幕上时它比屏幕还大，整张缩到能看又太小；切到局部显示就能在 100% 下拖着一块块看。
/// 此时拖动 = 平移内容，按住 <c>Ctrl</c> 拖才是移动窗口（取景框也得能挪地方）。
/// </para>
/// <para>
/// **缩略图**（<see cref="SetThumbnail"/>）：缩成 <see cref="PinMath.ThumbnailSide"/> 的小方块让开路，
/// 与局部显示互斥；方块里双击 = 还原（不是关闭）。**标注**（<see cref="AnnotateContent"/>）复用截图覆盖层
/// （<c>editOnly</c>），标完把结果换回这张贴图。
/// </para>
/// <para>
/// 代码按职责分 partial：本文件是字段 / 构造 / 属性 / 出场；显示形态在
/// <c>CapturePinWindow.View.cs</c>，实时贴图在 <c>CapturePinWindow.Live.cs</c>，
/// 鼠标手势在 <c>CapturePinWindow.Drag.cs</c>，右键菜单在 <c>CapturePinWindow.Menu.cs</c>。
/// </para>
/// </remarks>
public partial class CapturePinWindow : Window
{
    private BitmapSource _content;
    private readonly double _scale;
    private double _baseWidth;               // 100% 时的 DIP 宽 / 高
    private double _baseHeight;

    private IntPtr _hwnd;
    private double _zoom = 1;
    private bool _locked;
    private bool _viewport;
    private bool _thumb;                     // 缩略图模式（固定边长的小方块）
    private bool _hovering;
    private Vector _pan;                     // 局部显示里内容相对取景框的偏移（DIP）

    private bool _dragging;
    private bool _panning;
    private Point _dragStart;                // 平移：按下的**窗口内**位置（窗口不动，够用）
    private Point _moveStart;                // 移动窗口：按下的**屏幕**位置（DIP）
    private Vector _panStart;

    private bool _maybeSelect;                // Ctrl 按下但还没挪动：松手算「多选」，挪了算「拖」
    private Point _selectAt;                  // 上面那一下按下的屏幕位置（DIP）

    private string _group = PinGroups.Default;
    private bool _selected;

    private PinAppearance _appearance = PinAppearance.None;   // 图自带的选区外观（圆角 / 阴影 / 边框）
    private bool _checkerboard;                              // 透明棋盘格（有形状时为真，剪贴板元数据可显式带）

    private IntPtr _liveHwnd;                // 实时贴图跟的源窗口
    private uint _livePid;                   // 源窗口属于哪个程序（句柄换了要在同一个程序里找回）
    private string _liveClass = "";          // 源窗口的类名（句柄换了的兜底只认同一个类名）
    private int _liveGoneTicks;              // 源窗口「没了」连续几拍（防瞬时误判把贴图收掉）
    private bool _live;
    private LiveState _liveState = LiveState.Live;
    private System.Threading.Timer? _liveTimer;
    private int _liveBusy;                   // 抓帧防重入（上一拍还没回来就跳过这一拍）
    private LiveThumbnail? _thumbLayer;      // 实时内容层（DWM 缩略图，零抓帧零延迟）
    private bool _liveFallback;              // 系统不吃缩略图时退回逐帧抓图（远程桌面 / 老驱动）
    private IntPtr _liveHook;                // 源窗口位置 / 尺寸变化（缩略图裁剪要跟着重算）
    private Win32.WinEventProc? _liveHookProc;
    private LiveAnchor _liveAnchor;          // 贴图相对源窗口那块区域的偏移（源窗口怎么挪，贴图跟着挪）
    private Int32Rect _liveRegion;           // 跟住的那一块（源窗口坐标；进实时时定下，之后不再变）
    private Int32Rect _liveBlock;            // 那一块最后一次在屏幕上的位置（源窗口最小化期间沿用）
    private Int32Rect _livePlaced;           // 上一次对齐后贴图窗口的屏幕矩形（判「用户自己挪过」）
    private bool _liveLayingOut;             // 这次挪贴图是我们自己为了跟住源窗口发的
    private Int32Rect _livePinRect;          // 贴图当前的屏幕矩形（物理像素，抓帧兜底用）
    private HwndSource? _pinSource;          // 贴图窗口的消息钩子（挪 / 改尺寸当拍就把缩略图对齐）

    private ContextMenu? _menu;

    private IReadOnlyList<PinCardCopy> _copies = Array.Empty<PinCardCopy>();   // 色卡上的复制热区
    private PinContentKind _cardKind = PinContentKind.Image;
    private string _cardText = "";           // 文字 / 色卡原文（「复制原文」与历史重画用）

    /// <summary>抓一帧实时画面（默认真抓源窗口；冒烟注入合成帧，不碰真桌面）。</summary>
    internal Func<IntPtr, Int32Rect, BitmapSource?> LiveFrame = ScreenShot.CaptureWindow;

    public CapturePinWindow(BitmapSource image, Int32Rect area, double scale)
        : this(image, area, scale, 1)
    {
    }

    internal CapturePinWindow(BitmapSource image, Int32Rect area, double scale, double zoom)
    {
        InitializeComponent();
        InitDrop();
        InitPointerActions();

        _content = image;        _scale = scale > 0 ? scale : 1;
        _zoom = PinMath.ClampZoom(zoom);
        Shot.Source = image;

        // 100% 尺寸由「出场矩形 ÷ 屏幕缩放 ÷ 初始缩放」推出来：
        // 截图贴图 area 就是图占的那块；剪贴板贴图 area 是等比缩小后的落点，初始缩放补回 100%。
        double zoomForBase = _zoom > 0 ? _zoom : 1;
        _baseWidth = Math.Max(1, area.Width / _scale / zoomForBase);
        _baseHeight = Math.Max(1, area.Height / _scale / zoomForBase);

        var rect = PinMath.PlaceAt(new Rect(area.X, area.Y, area.Width, area.Height), _scale);

        Width = Math.Max(1, _baseWidth * _zoom);
        Height = Math.Max(1, _baseHeight * _zoom);
        Left = rect.X;
        Top = rect.Y;
        ApplyContent();

        Area = area;
        SourceInitialized += (_, _) => MakeNonActivating();

        // 实时贴图：本窗口一动 / 一改尺寸 / 一藏，下面那层缩略图宿主就得跟着（否则屏幕上会
        // 留下一块对不上的画面）。平时它们是空操作。点击穿透的三个控件也跟着贴图走。
        LocationChanged += (_, _) =>
        {
            SyncLiveLayer();
            SyncClickThroughGeometry();
        };
        SizeChanged += (_, _) =>
        {
            SyncLiveLayer();
            SyncClickThroughGeometry();
        };
        IsVisibleChanged += (_, _) => _thumbLayer?.SetShown(IsVisible);
    }

    /// <summary>
    /// 文字 / 颜色卡片的构造函数：<paramref name="card"/> 已经按屏幕缩放渲染成位图
    /// （<see cref="PinCardRenderer"/>），热区坐标是卡片的 100% DIP 坐标。
    /// </summary>
    internal CapturePinWindow(PinCardRender card, Int32Rect area, double scale, double zoom, string text)
        : this(card.Image, area, scale, zoom)
    {
        _baseWidth = Math.Max(1, card.Logical.Width);
        _baseHeight = Math.Max(1, card.Logical.Height);
        Width = Math.Max(1, _baseWidth * _zoom);
        Height = Math.Max(1, _baseHeight * _zoom);
        _copies = card.Copies;
        _cardKind = card.Kind;
        _cardText = text ?? "";
        ApplyContent();
        RefreshHoverChrome();
    }

    /// <summary>这张贴图是哪块屏幕区域截下来的（物理像素）。</summary>
    public Int32Rect Area { get; }

    /// <summary>贴的图（导出 / 复制用；标注之后就是标完的那张）。</summary>
    public BitmapSource Image => _content;

    /// <summary>贴的内容种类（图片 / 文字卡 / 颜色取样板）。</summary>
    internal PinContentKind CardKind => _cardKind;

    /// <summary>文字 / 颜色卡的原文（图片贴图是空串）。</summary>
    internal string CardText => _cardText;

    /// <summary>色卡上的复制热区（冒烟直接问命中）。</summary>
    internal IReadOnlyList<PinCardCopy> Copies => _copies;

    /// <summary>相对 100% 的缩放倍率。</summary>
    public double Zoom => _zoom;

    /// <summary>锁定后不能拖动、不能滚轮缩放 / 改不透明度。</summary>
    public bool Locked => _locked;

    /// <summary>局部显示开着没有（窗口是固定大小的取景框）。</summary>
    public bool Viewport => _viewport;

    /// <summary>缩略图模式开着没有（窗口是 83 DIP 的小方块）。</summary>
    public bool Thumbnail => _thumb;

    /// <summary>局部显示里内容的偏移（DIP，负 = 往左 / 上拖过）。</summary>
    public Vector Pan => _pan;

    /// <summary>这张贴图在哪个分组（只在内存，见 <see cref="CapturePinManager"/>）。</summary>
    public string Group => _group;

    /// <summary>图自带的选区外观（圆角 / 阴影 / 边框，见 <see cref="PinAppearance"/>）。</summary>
    internal PinAppearance Appearance => _appearance;

    /// <summary>
    /// 套上选区外观：图里已经烘进圆角 / 阴影时不再铺不透明底、不再叠窗口描边
    /// （照上游 <c>PinnedBorderAppearance</c>：效果以图为准，窗口只负责外形）。
    /// </summary>
    internal void SetAppearance(PinAppearance? appearance)
    {
        _appearance = (appearance ?? PinAppearance.None)
            .Clamp(_content.PixelWidth, _content.PixelHeight);
        ApplyChrome();
    }

    /// <summary>这张贴图现在会不会铺透明棋盘格（显式开关，或图自带圆角 / 阴影 / 边框）。</summary>
    internal bool Checkerboard => _checkerboard || _appearance.HasShape;

    /// <summary>
    /// 显式开 / 关棋盘格（剪贴板元数据里的 <c>checkerboardEnabled</c>）。
    /// 图自带形状时无论如何都会铺（见 <see cref="ApplyChrome"/>）——否则圆角外那几块会直接透出桌面。
    /// </summary>
    internal void SetCheckerboard(bool on)
    {
        if (_checkerboard == on) return;
        _checkerboard = on;
        ApplyChrome();
    }

    /// <summary>
    /// 贴图历史里的记录 Id（见 <see cref="PinStore"/>）；空串 = 没记历史（冒烟夹具 / 旧路径）。
    /// 关闭时按它补上关闭时间与最后落点。
    /// </summary>
    internal string PinId { get; set; } = "";

    /// <summary>在多选里没有（<c>Ctrl</c>+单击切换）。</summary>
    public bool Selected => _selected;

    /// <summary>内容当前的实际尺寸（100% 尺寸 × 缩放）。</summary>
    private Size ContentSize => new(Math.Max(1, _baseWidth * _zoom), Math.Max(1, _baseHeight * _zoom));

    /// <summary>这是一块实时贴图吗（跟着源窗口刷新）。</summary>
    public bool Live => _live;

    /// <summary>实时贴图这一拍看到的状态（源窗口最小化 / 关闭都会反映出来）。</summary>
    public LiveState LiveState => _liveState;

    // ============ 出场 ============

    private void MakeNonActivating()
    {
        _hwnd = OverlayShell.MakeNonActivating(this, excludeFromCapture: false);
        if (_hwnd == IntPtr.Zero) return;

        // 物理像素落位：混合 DPI 下 WPF 的 DIP 换算可能差几个像素，
        // 贴图要正好盖住刚才截的那块，所以再用 Win32 钉一次（与覆盖层同一套）。
        // 尺寸取**当前**窗口尺寸而不是 100% 尺寸：剪贴板贴图出场时可能已经等比缩小。
        int w = Math.Max(1, (int)Math.Round(Width * _scale));
        int h = Math.Max(1, (int)Math.Round(Height * _scale));
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, Area.X, Area.Y, w, h, Win32.SwpNoactivate);
        RaiseToTop();

        // 自己的位置 / 尺寸一变就把内容层对齐当拍（拖窗口时是系统模态移动循环，等不到 200ms 的定时器）。
        // 照 Snow Shot 的 `WM_WINDOWPOSCHANGED → synchronize()`：移动循环里也不会落后一帧。
        if (_pinSource == null && PresentationSource.FromVisual(this) is HwndSource source)
        {
            _pinSource = source;
            source.AddHook(PinMessageHook);
        }
    }

    /// <summary>
    /// 贴图窗口自己的消息：位置 / 尺寸变了就把 DWM 缩略图对齐（拖窗口跟手，不等定时器）。
    /// </summary>
    /// <remarks>
    /// 实时的画面在**另一个 HWND**（<see cref="LiveThumbnail"/> 的宿主）上，它不会跟着 WPF 窗口走；
    /// 只在 200ms 那拍对齐的话，拖动时画面会比窗口慢一大截，看着像「贴图没跟着拖」。
    /// </remarks>
    private IntPtr PinMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0047:        // WM_WINDOWPOSCHANGED
                if (_live) SyncLiveLayer();
                PinStacking.NativeOrderChanged(this);    // z 序变了：延迟一拍读回真实前后序
                break;
            case 0x0003:        // WM_MOVE
            case 0x0005:        // WM_SIZE
            case 0x0018:        // WM_SHOWWINDOW
                if (_live) SyncLiveLayer();
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>顶回最上面（点它 / 面板显示之后调；不抢焦点）。</summary>
    public void RaiseToTop()
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd, Topmost ? Win32.HwndTopmost : IntPtr.Zero,   // IntPtr.Zero = HWND_TOP
            0, 0, 0, 0, Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);

        // 穿透控件是同级窗口，贴图一抬头就会把它们盖住——跟着一起提。
        _ctBar?.RaiseToTop();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopLive();                            // 关掉窗口就别再抓帧（定时器不停会一直 PrintWindow）
        ShutdownHideToTop();                   // 顶边手柄是独立窗口，关窗时一起带走
        ShutdownClickThrough();                // 穿透控件是独立窗口，不关会留在屏幕上
        _menu?.IsOpen = false;
        _menu = null;
        base.OnClosed(e);
        CapturePinManager.Forget(this);
    }
}
