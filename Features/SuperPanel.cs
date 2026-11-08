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

    /// <summary>面板是否已打开。</summary>
    public static bool IsOpen => _window != null;

    /// <summary>唤起瞬间的光标 X（屏幕物理像素）。面板未打开时为 0。</summary>
    public static int InvokeX => _invokeX;

    /// <summary>唤起瞬间的光标 Y（屏幕物理像素）。面板未打开时为 0。</summary>
    public static int InvokeY => _invokeY;

    /// <summary>是否有右键菜单 / 对话框正打开——钩子据此决定是否拦截外部点击 / Esc。</summary>
    public static bool IsInteracting => _window?.InteractCount > 0;

    /// <summary>面板是否已钉住——钉住时点击外部不关闭（钩子不介入外部点击）。</summary>
    public static bool IsPinned => _window?.IsPinned == true;

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

    /// <summary>屏幕物理坐标点是否落在面板窗口矩形内（供鼠标钩子判定“点击外部”）。</summary>
    public static bool PointInWindowRect(int x, int y)
    {
        var w = _window;
        if (w == null) return false;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
    }

    /// <summary>
    /// 屏幕物理坐标点是否落在面板当前打开的右键菜单里（含已展开的子菜单）。
    /// 菜单浮在面板外面，钩子判「点外部」时要靠它把「点菜单项」排除掉。
    /// </summary>
    public static bool PointInOpenMenu(int x, int y) => _window?.MenuContains(x, y) == true;

    /// <summary>切换显示/关闭。</summary>
    public static void Toggle()
    {
        if (_window != null) { Close(); return; }
        Show();
    }

    /// <summary>
    /// 在光标处显示面板（键盘唤起：上下文按前台窗口解析）。
    /// </summary>
    public static void Show() => Show(IntPtr.Zero);

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
    public static void Show(IntPtr contextHwnd)
    {
        if (_window != null) return;

        // 建窗是异步的：把「是否停到暂存区」摘进局部变量，别让后续 Show 影响这一次
        bool openOnStash = _openOnStash;
        _openOnStash = false;

        // 唤起瞬间记录光标位置（windowPin.toggle 等内部命令依赖该坐标）
        if (Win32.GetCursorPos(out var pt)) { _invokeX = pt.X; _invokeY = pt.Y; }
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
            };
            _window = win;
            _hiddenForRecording = false;
            // 悬浮项要永远盖在面板前面（照 Quicker）：两边都是 Topmost，同一层里谁最后
            // SetWindowPos 谁在前，面板一显示 / 一激活就会把悬浮按钮盖住。面板每次激活都把
            // 悬浮项重新顶回去（悬浮窗是 WS_EX_NOACTIVATE，不会把焦点从面板抢走）。
            win.Activated += (_, _) => FloatingManager.RaiseAllAbove();
            // 在面板里按下鼠标时，Windows 会把面板提到 topmost 带的最前面——即使它已经是活动窗口
            // （这时不会再触发 Activated）。悬浮项要一直盖在面板上面，所以每次按下都顶一次；
            // 只改 z 序（SWP_NOACTIVATE），不会把焦点从面板抢走。
            win.PreviewMouseDown += (_, _) => FloatingManager.RaiseAllAbove();
            win.Show();
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

    /// <summary>关闭面板（若已打开）。</summary>
    public static void Close()
    {
        var w = _window;
        if (w == null) return;
        _window = null;
        _hiddenForRecording = false;
        try { w.Close(); } catch { /* 静默 */ }
    }

    /// <summary>钩子键路由（不依赖窗口焦点）：Esc 关面板；其余按键放行（搜索框要正常输入）。</summary>
    /// <returns>true=已处理（吞掉该键）；false=不处理（交下层）。</returns>
    public static bool HandleKey(ushort vk, bool isDown)
    {
        if (!IsOpen || !isDown || IsInteracting) return false;
        var w = _window;
        if (w == null) return false;

        // 钉住后用户去别的窗口工作：键盘归前台窗口，不能继续全局吞 Esc
        if (w.IsPinned && !w.IsActive) return false;

        if (vk == Win32.VkEscape) { w.RequestEscape(); return true; }
        return false;
    }

    /// <summary>删除动作引用：从所有面板分组里移除（由 <see cref="ConfigStore.RemoveAction"/> 调用）。</summary>
    public static void RemoveActionReferences(string id) => PanelLibrary.RemoveActionReferences(id);
}
