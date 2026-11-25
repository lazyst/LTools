using System;
using System.Windows;
using System.Windows.Interop;
using LTools.Core;
using LTools.Features.Floating;
using LTools.Features.Panel;
using LTools.Native;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 动作面板控制器（设计见 <c>docs/PANEL_REDESIGN.md</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 唤起时先记录光标坐标与**目标窗口**（键盘唤起 = 前台窗口；长按右键 = 光标下的顶层窗口），
/// 再创建面板窗口——窗口一显示，前台就变成 LTools 自己，上下文场景必须在创建之前解析。
/// 目标命中「禁止弹出」列表时直接放弃。
/// </para>
/// <para>
/// 键盘路由只处理 Esc：面板打开时搜索框需要正常 IME 与数字输入，全局钩子不能吞可打印字符。
/// 面板内容、场景与动作引用都由 <see cref="PanelLibrary"/> 负责。
/// </para>
/// </remarks>
internal static class SuperPanel
{
    private const string LogSource = "SuperPanel";
    private static SuperPanelWindow? _window;
    private static int _invokeX, _invokeY;
    private static bool _hiddenForRecording;

    /// <summary>下一次建窗时直接停到暂存区（<see cref="ShowStash"/> 用；建好即复位）。</summary>
    private static bool _openOnStash;

    static SuperPanel()
    {
        // 录制期间钩子透明，面板上的点击会被一起录进去：开始录制就关门 / 临时隐藏。
        // 事件是广播（热键 / 托盘发起的也算），可能来自链路线程或钩子线程，一律投到 UI 线程。
        // **别把这件事留在 <c>MacroRecorder</c> 里**：录制在执行器，面板在宿主，那边够不着这扇窗。
        MacroRecording.Started += () => Ui.Post(HideForRecording);
        MacroRecording.Stopped += () => Ui.Post(RestoreAfterRecording);
    }

    /// <summary>面板是否已打开。</summary>
    public static bool IsOpen => _window != null;

    /// <summary>唤起瞬间的光标 X（屏幕物理像素）。面板未打开时为 0。</summary>
    public static int InvokeX => _invokeX;

    /// <summary>唤起瞬间的光标 Y（屏幕物理像素）。面板未打开时为 0。</summary>
    public static int InvokeY => _invokeY;

    /// <summary>面板是否已钉住——钉住时点击外部不关闭（钩子那边读镜像里的 <c>Pinned</c>）。</summary>
    public static bool IsPinned => _window?.IsPinned == true;

    /// <summary>面板状态变了（宿主据此把整份状态推给提权进程的钩子）。</summary>
    internal static event Action? StateChanged;

    /// <summary>
    /// 把当前面板状态发布给钩子线程的纯数据镜像（阶段 2）。只能在主 UI 线程调用。
    /// 钩子线程不能碰 WPF 窗口 / 菜单，它读 <see cref="HookMirror.Panel"/> 做同步吞 / 放判定；
    /// 窗口矩形不进镜像（钩子自己按 HWND 调 GetWindowRect），所以拖动 / 缩放不必推送。
    /// 面板建窗、关闭、钉住、菜单开合、子菜单展开、交互计数变化、激活态变化都要调一次。
    /// </summary>
    internal static void PublishMirror()
    {
        var w = _window;
        if (w == null)
        {
            HookMirror.PublishPanel(HookMirror.PanelSnapshot.Empty);
            StateChanged?.Invoke();
            return;
        }
        IntPtr hwnd = IntPtr.Zero;
        try { hwnd = new WindowInteropHelper(w).Handle; }
        catch { /* 窗口刚建好还没句柄：按 0 处理，下次布局事件会补推 */ }
        HookMirror.PublishPanel(new HookMirror.PanelSnapshot
        {
            Open = true,
            Pinned = w.IsPinned,
            Interacting = w.InteractCount > 0,
            Active = w.IsActive,
            Hwnd = hwnd,
            MenuRects = w.OpenMenuRects(),
        });
        StateChanged?.Invoke();
    }

    /// <summary>面板上是否开着可见的子对话框（<c>Owner = 面板</c>）。</summary>
    public static bool HasOpenDialog
    {
        get
        {
            var w = _window;
            if (w == null) return false;
            var windows = Application.Current?.Windows;
            if (windows == null) return false;
            foreach (Window x in windows)
                if (!ReferenceEquals(x, w) && ReferenceEquals(x.Owner, w) && x.IsVisible) return true;
            return false;
        }
    }

    /// <summary>
    /// 开始键鼠录制前让开屏幕。面板上有对话框时改为临时隐藏（关闭 Owner 会连带销毁对话框）。
    /// </summary>
    public static void HideForRecording()
    {
        var w = _window;
        if (w == null) return;
        if (!HasOpenDialog)
        {
            Close();
            return;
        }
        if (_hiddenForRecording) return;
        _hiddenForRecording = true;
        try { w.Hide(); } catch { /* 隐藏失败不影响录制 */ }
    }

    /// <summary>录制结束：恢复被 <see cref="HideForRecording"/> 临时隐藏的面板。</summary>
    public static void RestoreAfterRecording()
    {
        if (!_hiddenForRecording) return;
        _hiddenForRecording = false;
        var w = _window;
        if (w == null) return;
        try { w.Show(); } catch { /* 恢复失败不影响编辑 */ }
        FloatingManager.RaiseAllAbove();
    }

    /// <summary>切换显示/关闭。</summary>
    public static void Toggle()
    {
        if (_window != null) { Close(); return; }
        Show();
    }

    /// <summary>
    /// 在光标处显示面板（键盘唤起：上下文按前台窗口解析）。
    /// </summary>
    public static void Show() => ShowCore(IntPtr.Zero, null, null);

    /// <summary>
    /// 显示面板并直接停在「暂存区」（设置页 / 动作管理里的入口用）。
    /// </summary>
    public static void ShowStash()
    {
        var w = _window;
        if (w != null)
        {
            w.ShowStashPage();
            w.Activate();
            FloatingManager.RaiseAllAbove();
            return;
        }

        _openOnStash = true;
        Show();
    }

    /// <summary>
    /// 在光标处显示面板。
    /// </summary>
    /// <param name="contextHwnd">
    /// 上下文窗口句柄。长按右键唤起时传**光标下的顶层窗口**：右键不会激活后台窗口，
    /// 此刻前台可能还停在别的程序上。照 Quicker：先把这个窗口激活（面板关掉后焦点回到它），
    /// 上下文也按它解析。传 <see cref="IntPtr.Zero"/> 时用当前前台窗口（键盘唤起 / CapsLock+T）。
    /// </param>
    public static void Show(IntPtr contextHwnd) => ShowCore(contextHwnd, null, null);

    /// <summary>
    /// 在指定屏幕物理坐标处显示面板（宿主收到 <c>panel.show</c> 时用它，位置按唤起瞬间的光标，
    /// 而不是宿主当前光标——用户可能已经挪开了）。
    /// </summary>
    public static void Show(IntPtr contextHwnd, int invokeX, int invokeY) => ShowCore(contextHwnd, invokeX, invokeY);

    private static void ShowCore(IntPtr contextHwnd, int? invokeX, int? invokeY)
    {
        if (_window != null) return;

        // 建窗是异步的：把「是否停到暂存区」摘进局部变量，别让后续 Show 影响这一次
        bool openOnStash = _openOnStash;
        _openOnStash = false;

        // 唤起瞬间记录光标位置（windowPin.toggle 等内部命令依赖该坐标）
        if (invokeX.HasValue && invokeY.HasValue) { _invokeX = invokeX.Value; _invokeY = invokeY.Value; }
        else if (Win32.GetCursorPos(out var pt)) { _invokeX = pt.X; _invokeY = pt.Y; }
        else { _invokeX = 0; _invokeY = 0; }

        // 钩子回调里只取句柄（微秒级）；进程 / 标题 / 类名放到 UI 线程补全，
        // 避免 MainModule 在个别进程上阻塞低级钩子回调。
        IntPtr fgHwnd = Win32.GetForegroundWindow();
        IntPtr targetHwnd = contextHwnd;

        // 光标下正好是 LTools 自己的浮层（提示卡片 / 录制指示器）时，仍按前台程序算
        if (targetHwnd == IntPtr.Zero || ForegroundApp.IsOwnOverlay(targetHwnd))
            targetHwnd = fgHwnd;

        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_window != null) return;

            var foreground = ForegroundApp.FromHandle(targetHwnd);
            if (foreground == null && targetHwnd != fgHwnd)
                foreground = ForegroundApp.FromHandle(fgHwnd);   // 句柄已销毁：退回前台
            if (foreground != null && PanelLibrary.IsBlocked(foreground.ExeName, foreground.ExePath))
            {
                AppLog.Info(LogSource, $"目标程序在「禁止弹出」列表里：{foreground.ExeName}");
                return;
            }

            // 照 Quicker：长按在哪个窗口上，就先把那个窗口激活——面板关掉后焦点回到它，
            // 之后的动作也作用在它身上。短按不走这里（由注入的原生右键完成激活）；
            // 键盘唤起时它本来就在前台，不用切。桌面 / 任务栏不激活。
            if (contextHwnd != IntPtr.Zero && !ForegroundApp.IsShellWindow(targetHwnd))
                WindowAction.ActivateNoWait(targetHwnd);

            var win = new SuperPanelWindow(foreground, _invokeX, _invokeY);
            win.Closed += (_, _) =>
            {
                if (_window == win) _window = null;
                PublishMirror();
            };
            _window = win;
            _hiddenForRecording = false;
            // 悬浮项要永远盖在面板前面（照 Quicker）：两边都是 Topmost，同一层里谁最后
            // SetWindowPos 谁在前，面板一显示 / 一激活就会把悬浮按钮盖住。面板每次激活都把
            // 悬浮项重新顶回去（悬浮窗是 WS_EX_NOACTIVATE，不会把焦点从面板抢走）。
            win.Activated += (_, _) => { FloatingManager.RaiseAllAbove(); PublishMirror(); };
            win.Deactivated += (_, _) => PublishMirror();
            // 在面板里按下鼠标时，Windows 会把面板提到 topmost 带的最前面——即使它已经是活动窗口
            // （这时不会再触发 Activated）。悬浮项要一直盖在面板上面，所以每次按下都顶一次；
            // 只改 z 序（SWP_NOACTIVATE），不会把焦点从面板抢走。
            win.PreviewMouseDown += (_, _) => FloatingManager.RaiseAllAbove();
            win.Show();
            PublishMirror();   // 建窗后才有 HWND，这时推第一份镜像
            if (openOnStash) win.ShowStashPage();
            win.Activate();
            FloatingManager.RaiseAllAbove();

            // 刚把前台让给目标窗口，前台锁可能让面板的 Activate() 抢不回来：兜底再抢一次
            var winHwnd = new WindowInteropHelper(win).Handle;
            if (winHwnd != IntPtr.Zero && Win32.GetForegroundWindow() != winHwnd)
                WindowAction.ActivateNoWait(winHwnd);
            FloatingManager.RaiseAllAbove();   // 兜底激活也可能把面板提到最前
        }));
    }

    /// <summary>
    /// 配置被外部改写后重建打开着的面板（宿主收到 <c>actions.changed</c> 时用；没开就什么都不做）。
    /// </summary>
    public static void Refresh() => _window?.RefreshFromConfig();

    /// <summary>关闭面板（若已打开）。这是**真的关**：✕、右键菜单「关闭面板」、CapsLock+T 走它。</summary>
    public static void Close()
    {
        var w = _window;
        if (w == null) return;
        _window = null;
        _hiddenForRecording = false;
        PublishMirror();   // 先撤镜像：钩子线程立刻知道面板已关，不必等窗口真的 Close()
        try { w.Close(); } catch { /* 静默 */ }
    }

    /// <summary>
    /// 「用完即收」的判定：钉住时不收（纯函数，自测直接断言）。见 <see cref="CloseAfterUse"/>。
    /// </summary>
    internal static bool ShouldDismissAfterUse(bool pinned) => !pinned;

    /// <summary>
    /// 用完即收：跑完一个动作 / 草稿之后收起面板。
    /// **钉住时不收**——钉住就是把它当工具盘（用户要求：点动作、右键编辑都不该让它消失）。
    /// </summary>
    internal static void CloseAfterUse()
    {
        if (!ShouldDismissAfterUse(IsPinned)) return;
        Close();
    }

    /// <summary>
    /// 打开外部窗口（编辑器 / 参数框 / 动作池 / 确认框）期间让开：钉住时只**隐藏**、不关，
    /// 用完 <see cref="IDisposable.Dispose"/> 再显示回来；没钉住就照旧先关掉。
    /// </summary>
    /// <remarks>
    /// 让开而不是留在屏幕上：面板是 Topmost，模态框会被它盖住、隐藏的 Owner 还会把对话框拍成黑块。
    /// 隐藏期间 <see cref="IsOpen"/> 仍为 true——钉住的面板从没被关掉，只是让开了。
    /// </remarks>
    internal static IDisposable StepAside()
    {
        var w = _window;
        if (w == null) return NoopScope.Instance;
        if (!IsPinned)
        {
            Close();
            return NoopScope.Instance;
        }
        try { w.Hide(); } catch { return NoopScope.Instance; }
        return new ReturnScope(w);
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    private sealed class ReturnScope : IDisposable
    {
        private readonly SuperPanelWindow _win;
        public ReturnScope(SuperPanelWindow win) => _win = win;

        public void Dispose()
        {
            if (!ReferenceEquals(_window, _win)) return;   // 让开期间被关掉了（✕ / 菜单）
            try { _win.Show(); }
            catch { return; }
            _win.Activate();
            FloatingManager.RaiseAllAbove();
        }
    }

    /// <summary>
    /// 钩子键路由（不依赖窗口焦点）：Esc 关面板；其余按键放行（搜索框要正常输入）。
    /// 判定只读 <see cref="HookMirror.Panel"/> 纯数据镜像，同步返回是否吞键；
    /// 「关面板」单独排回 UI 线程 / 转给宿主，绝不等待。
    /// </summary>
    /// <remarks>
    /// 面板在宿主里时本进程没有那扇窗（<see cref="_window"/> 为 null），所以关它只能发
    /// <c>panel.close</c>——**别在这里写 <c>_window?.RequestEscape()</c> 就完事**，
    /// 那样宿主里的面板永远收不到 Esc（面板没抢到焦点时就是这样）。
    /// </remarks>
    /// <returns>true=已处理（吞掉该键）；false=不处理（交下层）。</returns>
    public static bool HandleKey(ushort vk, bool isDown)
    {
        var s = HookMirror.Panel;
        if (!s.Open || !isDown || s.Interacting) return false;
        // 钉住 = 工具盘：Esc 既不关面板，也不该继续被全局吞掉（前台窗口自己要用）。
        // 面板自己那一份由 <see cref="SuperPanelWindow.RequestEscape"/> 挡住。
        if (s.Pinned) return false;
        if (vk != Win32.VkEscape) return false;
        Ui.Post(() =>
        {
            if (_window is { } w) w.RequestEscape();
            else PanelLauncher.Dismiss();
        });
        return true;
    }

    /// <summary>删除动作引用：从所有面板分组里移除（由 <see cref="ConfigStore.RemoveAction"/> 调用）。</summary>
    public static void RemoveActionReferences(string id) => PanelLibrary.RemoveActionReferences(id);
}
