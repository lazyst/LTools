using System.Runtime.InteropServices;

namespace LTools.Native;

/// <summary>
/// 实时贴图的内容层：用 <c>DwmRegisterThumbnail</c> 把源窗口的画面「投影」到贴图窗口正下方的一层
/// 宿主窗口上。
/// </summary>
/// <remarks>
/// <para>
/// 这是 Snow Shot 的做法（<c>snow_shot/src/platform/windows/livewindowpresentation.cpp</c>）：
/// 缩略图由 DWM 在合成阶段直接画，**一帧都不用我们抓**，所以完全没有抓帧延迟，
/// 源窗口滚动 / 播放 / 拖动时画面就是实时的。以前每 200ms 抓一次 <c>PrintWindow</c>，
/// 又慢又不跟手（用户报过）。
/// </para>
/// <para>
/// 为什么是「下面一层」而不是画在自己窗口里：DWM 缩略图画在窗口自己的表面上，
/// 任何不透明的 WPF 画刷都会把它盖住；而 WPF 的分层窗口（<c>AllowsTransparency</c>）
/// 是逐像素 alpha，没法当缩略图宿主。所以照 Snow Shot 分两层：
/// 宿主窗口（本类，<c>WS_EX_LAYERED</c> + 整窗 alpha）画缩略图，贴图窗口压在它上面、
/// 内容区留透明（alpha = 0 的地方鼠标直接穿过去，落到宿主上，宿主再把消息转发给贴图窗口）。
/// </para>
/// <para>
/// **z 序靠「拥有」关系固定**：把贴图窗口的 owner 改成宿主（<c>SetWindowLongPtr(GWLP_HWNDPARENT)</c>），
/// 拥有的窗口永远压在 owner 上面，所以宿主稳稳贴在贴图窗口正下方，
/// 不用每次同步都抢一次 z 序（那样会被别的 topmost 窗口插进来）。
/// </para>
/// <para>
/// 宿主**永远不是前台窗口**（<c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>），也不进 Alt+Tab。
/// </para>
/// </remarks>
internal sealed class LiveThumbnail : IDisposable
{
    private const string ClassName = "LToolsLiveThumbnailHost";

    private const int WsPopup = unchecked((int)0x80000000);

    private const int WsExTopmost = 0x00000008;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoactivate = 0x08000000;

    private const int LwaAlpha = 0x2;

    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;
    private const uint SwpHidewindow = 0x0080;
    private const uint SwpNoownerzorder = 0x0200;

    private const uint WmPaint = 0x000F;
    private const uint WmErasebkgnd = 0x0014;
    private const uint WmSetcursor = 0x0020;
    private const uint WmMouseactivate = 0x0021;
    private const uint WmNcdestroy = 0x0082;
    private const uint WmMousemove = 0x0200;
    private const uint WmLbuttondown = 0x0201;
    private const uint WmLbuttonup = 0x0202;
    private const uint WmLbuttondblclk = 0x0203;
    private const uint WmRbuttondown = 0x0204;
    private const uint WmRbuttonup = 0x0205;
    private const uint WmRbuttondblclk = 0x0206;
    private const uint WmMbuttondown = 0x0207;
    private const uint WmMbuttonup = 0x0208;
    private const uint WmMbuttondblclk = 0x0209;
    private const uint WmMousewheel = 0x020A;
    private const uint WmXbuttondown = 0x020B;
    private const uint WmXbuttonup = 0x020C;
    private const uint WmMousehwheel = 0x020E;
    private const uint WmMouseleave = 0x02A3;

    private const int MaNoactivate = 3;

    private const int GwlpHwndparent = -8;
    private const uint GwOwner = 4;

    /// <summary>IDC_ARROW（光标类资源是序号，不是字符串）。</summary>
    private const int IdcArrow = 32512;

    private const uint TmeLeave = 0x00000002;

    private const uint DwmTnpRectdestination = 0x1;
    private const uint DwmTnpRectsource = 0x2;
    private const uint DwmTnpOpacity = 0x4;
    private const uint DwmTnpSourceclientareaonly = 0x10;

    // —— P/Invoke ——

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint dwFlags;
        public Win32.Rect rcDestination;
        public Win32.Rect rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    /// <summary>只为了把 <c>BeginPaint</c> / <c>EndPaint</c> 要的缓冲区填满（内容是系统写的，我们不读）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr hdc;
        public int fErase;
        public Win32.Rect rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] reserved;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData data);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    // —— 类注册与实例表（窗口过程要靠 hwnd 找到实例）——

    private static readonly object Gate = new();
    private static WndProcDelegate? _proc;      // 必须保活：委托被回收后窗口过程就是野指针
    private static ushort _classAtom;
    private static readonly Dictionary<IntPtr, LiveThumbnail> Instances = new();

    private IntPtr _host;
    private IntPtr _thumb;
    private IntPtr _source;
    private IntPtr _shell;
    private IntPtr _originalOwner;               // 贴图窗口原来的 owner（拆的时候还回去）
    private Win32.Rect _rcSource;
    private Win32.Rect _rcDestination;
    private Win32.Rect _hostRect;               // 宿主上一次摆到的矩形（没变就不必再挪）
    private ThumbnailProperties _appliedProperties;
    private bool _applied;
    private byte _alpha = 255;
    private bool _visible = true;
    private bool _shown = true;
    private bool _disposed;

    /// <summary>缩略图是不是建起来了（建不起来时调用方退回逐帧抓图）。</summary>
    public bool Attached => _thumb != IntPtr.Zero && !_disposed;

    /// <summary>
    /// 给 <paramref name="source"/> 建一层宿主窗口贴到 <paramref name="shell"/> 正下方，
    /// 只显示 <paramref name="source"/> 里的 <paramref name="rcSource"/> 那一块，
    /// 画到 <paramref name="shell"/> 的 <paramref name="rcDestination"/>（都相对各自窗口左上角，物理像素）。
    /// 返回 null = 系统不吃缩略图（远程桌面 / 老驱动），调用方退回逐帧抓图。
    /// </summary>
    public static LiveThumbnail? Attach(IntPtr source, IntPtr shell,
        Win32.Rect rcSource, Win32.Rect rcDestination)
    {
        if (source == IntPtr.Zero || shell == IntPtr.Zero) return null;

        var live = new LiveThumbnail { _source = source, _shell = shell };
        if (!live.CreateHost(rcSource, rcDestination))
        {
            live.Dispose();
            return null;
        }
        return live;
    }

    private bool CreateHost(Win32.Rect rcSource, Win32.Rect rcDestination)
    {
        _rcSource = rcSource;
        _rcDestination = rcDestination;

        lock (Gate)
        {
            if (_classAtom == 0)
            {
                _proc = WindowProc;
                var wc = new WndClassEx
                {
                    cbSize = Marshal.SizeOf<WndClassEx>(),
                    style = 0x0008,                   // CS_DBLCLKS
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                    hInstance = Win32.GetModuleHandle(null),
                    hCursor = LoadCursor(IntPtr.Zero, new IntPtr(IdcArrow)),
                    lpszClassName = ClassName,
                };
                _classAtom = RegisterClassEx(ref wc);
                if (_classAtom == 0) return false;
            }
        }

        // 先建成「贴图窗口原来的 owner 的 owned 窗口」，等壳窗口反过来认它当 owner 之后，
        // 两者就在同一个 z 序带里、宿主永远紧贴在壳下面。
        _originalOwner = Win32.GetWindow(_shell, GwOwner);

        _host = CreateWindowEx(WsExToolwindow | WsExNoactivate | WsExLayered, ClassName, "",
            WsPopup, 0, 0, 1, 1, _originalOwner, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);
        if (_host == IntPtr.Zero) return false;

        lock (Gate) Instances[_host] = this;

        // 玻璃扩展：客户区变成合成面，缩略图就画在这一层上
        var glass = new Margins { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        if (DwmExtendFrameIntoClientArea(_host, ref glass) != 0) return false;
        SetLayeredWindowAttributes(_host, 0, 255, LwaAlpha);

        if (DwmRegisterThumbnail(_host, _source, out _thumb) != 0 || _thumb == IntPtr.Zero)
        {
            _thumb = IntPtr.Zero;
            return false;
        }

        // 壳窗口变成宿主的 owned 窗口：它永远压在宿主上面，宿主就永远贴在它正下方。
        Win32.SetWindowLongPtr(_shell, GwlpHwndparent, _host);

        ApplyCrop();
        // 位置 / 尺寸 / z 序都在这里对齐：宿主插到壳的**正下方**（与 Snow Shot 同一个做法）。
        // 只靠「把宿主提进 topmost 带」有时会落在壳的上面——那样缩略图会盖住贴图窗口自己的内容。
        return SyncGeometry();
    }

    /// <summary>
    /// 换一块显示内容：<paramref name="rcSource"/> 是源窗口里的矩形，
    /// <paramref name="rcDestination"/> 是贴图窗口里的矩形，都相对各自左上角。
    /// <paramref name="visible"/> = false 时整层透明（源窗口完全挪开了 / 被最小化）。
    /// </summary>
    public void UpdateCrop(Win32.Rect rcSource, Win32.Rect rcDestination, bool visible)
    {
        if (!Attached) return;
        _rcSource = rcSource;
        _rcDestination = rcDestination;
        _visible = visible;
        ApplyCrop();
    }

    /// <summary>
    /// 下一拍无条件把属性推一遍：源窗口最小化 / 还原之后 DWM 可能把缩略图丢了，
    /// 靠它把画面接回来（正常的 200ms 那一拍走缓存，不惊动 DWM）。
    /// </summary>
    public void Refresh()
    {
        _applied = false;
        ApplyCrop();
    }

    private void ApplyCrop()
    {
        if (_thumb == IntPtr.Zero || _disposed) return;

        bool empty = _rcDestination.Right <= _rcDestination.Left ||
                     _rcDestination.Bottom <= _rcDestination.Top ||
                     _rcSource.Right <= _rcSource.Left || _rcSource.Bottom <= _rcSource.Top;

        var properties = new ThumbnailProperties
        {
            dwFlags = DwmTnpRectdestination | DwmTnpRectsource | DwmTnpOpacity |
                      DwmTnpSourceclientareaonly,
            rcDestination = _rcDestination,
            rcSource = _rcSource,
            opacity = 255,
            fVisible = _visible && !empty,
            fSourceClientAreaOnly = false,
        };

        // 每拍（贴图的实时定时器）都会走到这里，属性没变就别再惊动 DWM。
        if (_applied && SameProperties(_appliedProperties, properties)) return;

        DwmUpdateThumbnailProperties(_thumb, ref properties);
        _appliedProperties = properties;
        _applied = true;
    }

    private static bool SameProperties(ThumbnailProperties left, ThumbnailProperties right) =>
        left.dwFlags == right.dwFlags &&
        left.opacity == right.opacity &&
        left.fVisible == right.fVisible &&
        left.fSourceClientAreaOnly == right.fSourceClientAreaOnly &&
        SameRect(left.rcSource, right.rcSource) &&
        SameRect(left.rcDestination, right.rcDestination);

    private static bool SameRect(Win32.Rect left, Win32.Rect right) =>
        left.Left == right.Left && left.Top == right.Top &&
        left.Right == right.Right && left.Bottom == right.Bottom;

    /// <summary>
    /// 让宿主窗口贴住 <paramref name="shell"/>：位置 / 尺寸 / 显隐 / z 序。
    /// </summary>
    /// <remarks>
    /// 照 Snow Shot 的 <c>synchronize()</c>：宿主用 <c>SetWindowPos(host, shell, …)</c> 插到壳的**正下方**
    /// （已经贴着时用 <c>SWP_NOZORDER</c> 不动它，免得每拍都抢一次 z 序把别的 topmost 窗口搅进来）；
    /// 壳最小化 / 贴图被藏起来时把宿主一起藏掉，否则屏幕上会剩一块点不掉的画面。
    /// </remarks>
    public bool SyncGeometry()
    {
        if (_host == IntPtr.Zero || _disposed) return false;
        if (!Win32.GetWindowRect(_shell, out var rect)) return false;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        bool show = _shown && width > 0 && height > 0 && !Win32.IsIconic(_shell);

        if (!show)
        {
            // 藏起来：位置尺寸都不动，只收起窗口（再显示时照旧贴回去）
            Win32.SetWindowPos(_host, IntPtr.Zero, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpNoownerzorder | SwpHidewindow);
            return true;
        }

        uint flags = SwpNoactivate | SwpNoownerzorder | SwpShowwindow;
        if (Win32.GetWindow(_shell, Win32.GwHwndnext) == _host) flags |= SwpNozorder;
        if (Win32.IsWindowVisible(_host) && SameRect(_hostRect, rect)) flags |= SwpNomove | SwpNosize;

        if (!Win32.SetWindowPos(_host, _shell, rect.Left, rect.Top, width, height, flags)) return false;
        _hostRect = rect;
        return true;
    }

    /// <summary>整窗不透明度（0–1）：贴图窗口自己是逐像素 alpha，实时的内容靠这一层调。</summary>
    public void SetOpacity(double opacity)
    {
        if (_host == IntPtr.Zero || _disposed) return;
        byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
        if (alpha == _alpha) return;
        if (SetLayeredWindowAttributes(_host, 0, alpha, LwaAlpha)) _alpha = alpha;
    }

    /// <summary>贴图窗口被隐藏（分组 / 关闭全部）时整层跟着藏，免得剩一块点不掉的画面。</summary>
    public void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        SyncGeometry();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 顺序要紧：先解开 owner 关系再销毁宿主，否则系统会连着把「被拥有的」贴图窗口一起收走。
        if (_shell != IntPtr.Zero && IsAlive(_shell) && Win32.GetWindow(_shell, GwOwner) == _host)
            Win32.SetWindowLongPtr(_shell, GwlpHwndparent,
                IsAlive(_originalOwner) ? _originalOwner : IntPtr.Zero);

        if (_thumb != IntPtr.Zero)
        {
            try { DwmUnregisterThumbnail(_thumb); } catch { /* 已经没了就算了 */ }
            _thumb = IntPtr.Zero;
        }
        if (_host != IntPtr.Zero)
        {
            lock (Gate) Instances.Remove(_host);
            try { DestroyWindow(_host); } catch { /* 已经没了就算了 */ }
            _host = IntPtr.Zero;
        }
    }

    private static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && Win32.IsWindow(hwnd);

    // —— 窗口过程：画归 DWM，鼠标消息转发给贴图窗口 ——

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        LiveThumbnail? live;
        lock (Gate) Instances.TryGetValue(hwnd, out live);

        if (msg == WmNcdestroy)
        {
            if (live != null)
            {
                lock (Gate) Instances.Remove(hwnd);
                live._host = IntPtr.Zero;
                if (live._thumb != IntPtr.Zero)
                {
                    try { DwmUnregisterThumbnail(live._thumb); } catch { /* ignore */ }
                    live._thumb = IntPtr.Zero;
                }
            }
            return IntPtr.Zero;
        }

        if (live == null) return DefWindowProc(hwnd, msg, wParam, lParam);

        switch (msg)
        {
            case WmMouseactivate:
                return MaNoactivate;
            case WmPaint:
                // 画归 DWM，这里什么都不画——但**必须** BeginPaint / EndPaint 把更新区验证掉。
                // 只 `return 0` 的话这块窗口的更新区永远是脏的，系统会不停地再发 WM_PAINT：
                // 消息队列被刷满、UI 线程 100% 空转，`DispatcherPriority.Background` 永远排不到
                // （冒烟里表现为「开始实时之后卡死」，见 docs/SCREEN_CAPTURE_PRO.md §17 坑 75）。
                if (BeginPaint(hwnd, out var paint) != IntPtr.Zero) EndPaint(hwnd, ref paint);
                return IntPtr.Zero;
            case WmErasebkgnd:
                return 1;
        }

        // 贴图窗口的内容区是透明的（逐像素 alpha = 0），点击会落到本窗口上：
        // 原样转发给贴图窗口，拖动 / 右键菜单 / 缩放滚轮照旧（与 Snow Shot 同款做法）。
        switch (msg)
        {
            case WmMousemove:
            case WmLbuttondown:
            case WmLbuttonup:
            case WmLbuttondblclk:
            case WmRbuttondown:
            case WmRbuttonup:
            case WmRbuttondblclk:
            case WmMbuttondown:
            case WmMbuttonup:
            case WmMbuttondblclk:
            case WmXbuttondown:
            case WmXbuttonup:
            case WmMousewheel:
            case WmMousehwheel:
            case WmMouseleave:
                if (msg == WmMousemove)
                {
                    var track = new TrackMouseEventData
                    {
                        cbSize = Marshal.SizeOf<TrackMouseEventData>(),
                        dwFlags = TmeLeave,
                        hwndTrack = hwnd,
                    };
                    TrackMouseEvent(ref track);
                }
                Win32.SendMessage(live._shell, msg, wParam, lParam);
                return IntPtr.Zero;

            case WmSetcursor:
                // DefWindowProc 可能回头问壳的 owner（也就是宿主）要光标，原样转发会自己转给自己。
                if (wParam != hwnd) return DefWindowProc(hwnd, msg, wParam, lParam);
                return Win32.SendMessage(live._shell, msg, live._shell, lParam);
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
