using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CapsLockPro.Config;
using CapsLockPro.Core;
using CapsLockPro.Native;
using CapsLockPro.Views;

namespace CapsLockPro.Features;

/// <summary>
/// 超级面板控制器（计划 §5）。管理面板窗口的生命周期、唤起瞬间的光标坐标、
/// 以及槽位数据的磁盘读写（经 <see cref="ConfigIO"/> 串行落盘）。
/// </summary>
/// <remarks>
/// 键盘热键唤起（阶段 4：CapsLock+T）与未来的鼠标长按手势（阶段 5）都调 <see cref="Toggle"/> / <see cref="Show"/>。
/// 唤起瞬间用 <see cref="Win32.GetCursorPos"/> 记录光标物理坐标，供 <c>windowPin.toggle</c> 等需坐标的
/// 内部命令使用（见计划 §7）。槽位数据来自 <see cref="AppConfig.SuperPanelConfig.Pages"/>。
/// </remarks>
internal static class SuperPanel
{
    private static SuperPanelWindow? _window;
    private static int _invokeX, _invokeY;

    /// <summary>面板是否已打开。</summary>
    public static bool IsOpen => _window != null;

    /// <summary>唤起瞬间的光标 X（屏幕物理像素）。面板未打开时为 0。</summary>
    public static int InvokeX => _invokeX;

    /// <summary>唤起瞬间的光标 Y（屏幕物理像素）。</summary>
    public static int InvokeY => _invokeY;

    /// <summary>是否有右键菜单 / 对话框正打开——钩子据此决定是否拦截外部点击 / Esc。</summary>
    public static bool IsInteracting => _window?.InteractCount > 0;

    /// <summary>屏幕物理坐标点是否落在面板窗口矩形内（供鼠标钩子判定“点击外部”）。</summary>
    public static bool PointInWindowRect(int x, int y)
    {
        var w = _window;
        if (w == null) return false;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
    }

    /// <summary>切换显示/关闭。</summary>
    public static void Toggle()
    {
        if (_window != null) { Close(); return; }
        Show();
    }

    /// <summary>在光标处显示面板。勾子回调内调用时延迟到下一 Dispatcher 周期再创建窗口，
    /// 并显式 Activate 确保成为前台（与 MenuPopup / HelpPanel 同一类激活竞态处理）。</summary>
    public static void Show()
    {
        if (_window != null) return;

        // 唤起瞬间记录光标位置（供 windowPin.toggle 等需坐标的内部命令使用）
        if (Win32.GetCursorPos(out var pt)) { _invokeX = pt.X; _invokeY = pt.Y; }
        else { _invokeX = 0; _invokeY = 0; }

        Application.Current.Dispatcher.BeginInvoke(new System.Action(() =>
        {
            if (_window != null) return;   // 期间可能已打开/关闭
            var pages = LoadPages();
            _window = new SuperPanelWindow(pages, _invokeX, _invokeY);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
            _window.Activate();
        }));
    }

    /// <summary>关闭面板（若已打开）。</summary>
    public static void Close()
    {
        var w = _window;
        if (w == null) return;
        _window = null;
        try { w.Close(); } catch { /* 静默 */ }
    }

    /// <summary>钩子键路由（不依赖窗口焦点）：1~9 选格 / Esc 关面板。打开期间被钩子调用。</summary>
    /// <returns>true=已处理（吞掉该键）；false=不处理（交下层）。</returns>
    public static bool HandleKey(ushort vk, bool isDown)
    {
        if (!IsOpen || !isDown || IsInteracting) return false;
        var w = _window;
        if (w == null) return false;

        if (vk == Win32.VkEscape) { w.RequestEscape(); return true; }
        if (vk >= '1' && vk <= '9')
        {
            if (w.ExecuteSlot(vk - '1')) Close();
            return true;
        }
        return false;
    }

    // —— 配置读写（经 ConfigIO 串行化，避免与设置窗口防抖保存互相覆盖）——

    /// <summary>从磁盘加载超级面板页（每页补齐到 9 槽）；无页时返回单页空占位。</summary>
    internal static List<List<string?>> LoadPages()
    {
        var path = ResolveConfigPath();
        var cfg = AppConfig.Load(path);
        var pages = new List<List<string?>>();
        foreach (var p in cfg.SuperPanel.Pages)
        {
            var page = new List<string?>(9);
            for (int i = 0; i < 9; i++) page.Add(i < p.Count ? p[i] : null);
            pages.Add(page);
        }
        if (pages.Count == 0) pages.Add(new List<string?>(new string?[9]));
        return pages;
    }

    /// <summary>把页写回磁盘（读现有配置→只替换 Pages→保存，保留动作清单等其它节）。</summary>
    internal static void SavePages(List<List<string?>> pages)
    {
        ConfigIO.Modify(ResolveConfigPath(), cfg =>
        {
            cfg.SuperPanel.Pages = pages.Select(p => new List<string?>(p)).ToList();
        });
    }

    /// <summary>删除动作引用：把所有页槽位中指向该 Id 的清成 null（落盘）。</summary>
    public static void RemoveActionReferences(string id)
    {
        ConfigIO.Modify(ResolveConfigPath(), cfg =>
        {
            if (cfg.SuperPanel?.Pages == null) return;
            foreach (var p in cfg.SuperPanel.Pages)
            {
                for (int i = 0; i < p.Count; i++)
                    if (p[i] == id) p[i] = null;
            }
        });
    }

    private static string ResolveConfigPath() =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;
}
