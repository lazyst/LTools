using System;
using System.Threading;
using LTools.Core;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 长截图期间给全局钩子看的「让路 / 观察」状态：鼠标手势全部放行（页面自己滚），
/// 只把**落在目标窗口里的滚轮**交回拼接流程（用来触发抓帧）；Esc / Enter / Ctrl+C / 空格归拼接流程。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要这层：长截图时覆盖层**不能盖住目标窗口**。Chromium 一发现自己的窗口被别的
/// 窗口整个盖住就丢掉送进来的滚轮（合成消息和真滚轮一样丢），这是「滚不动 / 拼不出」的根。
/// 所以长截图期间覆盖层只剩透明壳 + 小面板、鼠标事件原样放行，页面收到的是真正的滚轮。
/// </para>
/// <para>
/// 但「页面自己滚」意味着没人通知我们「该抓帧了」——所以滚轮由 <see cref="Hooks.MouseHook"/>
/// 顺手**观察**一份（不吞、不改），滚轮落在目标窗口里就 <see cref="Ui.Post"/> 回 UI 线程抓一帧。
/// 观察范围用**目标窗口矩形**而不是选区：页面本身是整窗滚动的，光标滑出选区也不该漏帧。
/// 注入的滚轮（自动滚动自己发的）带 <see cref="MouseOps.Tag"/>，钩子那边已经过滤掉了。
/// </para>
/// </remarks>
internal static class ScrollCaptureHookState
{
    /// <summary>长截图期间被吞掉、转给拼接流程的三个键（外加 Ctrl+C = 完成）。</summary>
    public enum Key
    {
        /// <summary>Enter / Ctrl+C = 完成。</summary>
        Finish,

        /// <summary>Esc = 放弃。</summary>
        Cancel,

        /// <summary>空格 = 开关自动滚动。</summary>
        AutoScroll,
    }

    private static int _active;
    private static int _left;
    private static int _top;
    private static int _right;
    private static int _bottom;      // 目标窗口矩形（虚拟桌面物理像素，与截图 / 注入同一套坐标）
    private static Action? _wheel;   // 滚轮落在窗口里 → UI 线程安排抓帧
    private static Action<Key>? _key;

    /// <summary>钩子线程读一次就够（只读快照，见 docs/THREADING.md）。</summary>
    public static bool Active => Volatile.Read(ref _active) != 0;

    /// <summary>进入长截图（UI 线程）。<paramref name="onWheel"/> 在 UI 线程被调。</summary>
    public static void Start(int left, int top, int right, int bottom, Action onWheel, Action<Key> onKey)
    {
        Volatile.Write(ref _left, left);
        Volatile.Write(ref _top, top);
        Volatile.Write(ref _right, right);
        Volatile.Write(ref _bottom, bottom);
        Volatile.Write(ref _wheel, onWheel);
        Volatile.Write(ref _key, onKey);
        Volatile.Write(ref _active, 1);
    }

    /// <summary>退出长截图（UI 线程；幂等）。</summary>
    public static void Stop()
    {
        Volatile.Write(ref _active, 0);
        Volatile.Write(ref _wheel, null);
        Volatile.Write(ref _key, null);
    }

    /// <summary>
    /// 钩子线程：滚轮落在目标窗口里就把「该抓帧了」排给 UI 线程。事件本身照常下传（页面自己滚）。
    /// </summary>
    public static void ObserveWheel(int x, int y)
    {
        var sink = Volatile.Read(ref _wheel);
        if (sink == null) return;
        if (x < Volatile.Read(ref _left) || x >= Volatile.Read(ref _right)) return;
        if (y < Volatile.Read(ref _top) || y >= Volatile.Read(ref _bottom)) return;
        Ui.Post(sink);
    }

    /// <summary>
    /// 钩子线程：Esc / Enter / Ctrl+C / 空格吞掉（连 keyup 一起吞，保持成对），其余按键返回 false 照常走。
    /// </summary>
    public static bool HandleKey(int vk, bool isDown, bool isUp)
    {
        var sink = Volatile.Read(ref _key);
        if (sink == null) return false;

        Key key;
        if (vk == Win32.VkEscape) key = Key.Cancel;
        else if (vk == Win32.VkReturn) key = Key.Finish;
        else if (vk == Win32.VkSpace) key = Key.AutoScroll;
        else if (IsCopyChord(vk)) key = Key.Finish;
        else return false;

        // down 才转给流程；up 只负责一起吞掉（否则页面 / 输入法会收到半截按键）
        if (isDown && !isUp) Ui.Post(() => sink(key));
        return true;
    }

    /// <summary>Ctrl + C：和「完成」同一个出口（面板按钮的 ToolTip 就是这么写的）。</summary>
    private static bool IsCopyChord(int vk)
    {
        const int VkC = 0x43;
        return vk == VkC && (Win32.GetAsyncKeyState(Win32.VkControl) & 0x8000) != 0;
    }
}
