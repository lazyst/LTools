using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 钩子线程用的 UI 状态镜像（2026-11 阶段 2）。
///
/// 低级键鼠钩子跑在专用 STA 线程，回调里**不能碰任何 WPF 对象**（窗口 / 菜单 / Popup 跨线程
/// 访问会抛「另一个线程拥有该对象」）。主 UI 线程在状态变化时发布一份不可变快照，钩子线程
/// 只读快照做同步吞 / 放判定；所有 UI 副作用走 <see cref="Ui.Post"/> 排回去，不等待。
///
/// 面板与悬浮各一份独立快照：两边由各自的 UI 代码推送，互不覆盖，也不需要跨模块聚合。
/// 面板窗口矩形不进快照：只存 HWND，判「点外部」时钩子线程自己 <c>GetWindowRect</c>（纯 Win32），
/// 窗口移动 / 缩放会自动反映，不必为每次布局变化推送。
/// </summary>
internal static class HookMirror
{
    /// <summary>物理屏幕像素矩形（与 MSLLHOOKSTRUCT.pt 同一坐标系）。</summary>
    public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
    }

    /// <summary>超级面板镜像。发布后不再修改，钩子线程可放心遍历数组。</summary>
    public sealed class PanelSnapshot
    {
        public bool Open;
        public bool Pinned;
        public bool Interacting;
        public bool Active;
        public IntPtr Hwnd;
        /// <summary>当前打开的右键菜单（含已展开子菜单）矩形；菜单未开为空数组。</summary>
        public Rect[] MenuRects = Array.Empty<Rect>();

        public static readonly PanelSnapshot Empty = new();
    }

    /// <summary>桌面悬浮镜像。</summary>
    public sealed class FloatSnapshot
    {
        public bool MenuOpen;
        /// <summary>所有开着的悬浮菜单（含已展开子菜单）矩形。</summary>
        public Rect[] MenuRects = Array.Empty<Rect>();

        public static readonly FloatSnapshot Empty = new();
    }

    private static PanelSnapshot _panel = PanelSnapshot.Empty;
    private static FloatSnapshot _float = FloatSnapshot.Empty;

    /// <summary>钩子线程读：一次 volatile 引用读，之后全是纯字段 / 数组访问。</summary>
    public static PanelSnapshot Panel => System.Threading.Volatile.Read(ref _panel);
    public static FloatSnapshot Float => System.Threading.Volatile.Read(ref _float);

    /// <summary>主 UI 线程写：整份替换，绝不在原对象上改字段。</summary>
    public static void PublishPanel(PanelSnapshot s) => System.Threading.Volatile.Write(ref _panel, s);
    public static void PublishFloat(FloatSnapshot s) => System.Threading.Volatile.Write(ref _float, s);

    /// <summary>点是否落在面板窗口矩形内。句柄失效时 GetWindowRect 返回 false，按「不在」处理。</summary>
    public static bool PanelContains(int x, int y)
    {
        IntPtr hwnd = Panel.Hwnd;
        if (hwnd == IntPtr.Zero) return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
    }

    /// <summary>点是否落在面板当前打开的右键菜单（含已展开子菜单）里。</summary>
    public static bool PanelMenuContains(int x, int y)
    {
        foreach (var r in Panel.MenuRects)
            if (r.Contains(x, y)) return true;
        return false;
    }

    /// <summary>点是否落在任一悬浮窗当前打开的右键菜单里。</summary>
    public static bool FloatMenuContains(int x, int y)
    {
        foreach (var r in Float.MenuRects)
            if (r.Contains(x, y)) return true;
        return false;
    }
}
