using System;
using System.Collections.Generic;
using System.Threading;
using LTools.Core;
using LTools.Core.Link;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 把贴图窗口的快捷键接到全局键盘钩子上（照上游 <c>pinnedwindowshortcutcontroller</c>，
/// 区别只在作用范围：上游是窗口焦点，这里是「点过这张贴图、指针还停在它上面」）。
/// </summary>
/// <remarks>
/// <para>
/// **为什么要作用范围**：贴图窗是 <c>WS_EX_NOACTIVATE</c>（点一下不抢前台，后续注入的键鼠
/// 不会落到贴图上），没有「窗口焦点」可用。只按「指针停在这张贴图上」也不行——用户把鼠标
/// 停在贴图上、又在别的程序里打字时，<c>W</c> / <c>A</c> / <c>S</c> / <c>L</c> 这些裸键会被吞掉。
/// 所以照「点一下 = 给它焦点」的直觉收窄成两步：**先点这张贴图（engage），指针离开就作废**，
/// 期间按表里的键才生效。用户看到的是「点一下贴图，然后按 L 锁定 / 按 Esc 关闭」。
/// </para>
/// <para>
/// 钩子线程只碰纯数据：贴图窗通过 <see cref="Register"/> / <see cref="Engage"/> /
/// <see cref="Unregister"/> 交一份 <c>(窗口句柄 → 回调 + 是否点过)</c>，<see cref="Handle"/>
/// 只做「光标下是不是这张贴图 / 点过吗 / 命中吗」，命中就 <see cref="Ui.Post"/> 排回 UI 线程
/// 并吞掉这个键。
/// </para>
/// </remarks>
internal static class PinShortcutRoute
{
    private sealed class Target
    {
        public IntPtr Hwnd { get; init; }
        public Action<PinShortcutAction>? Fire { get; init; }

        /// <summary>这张贴图在**另一个进程**（宿主）里：命中了就把动作转过去，别找本地回调。</summary>
        public bool Remote { get; init; }

        public volatile bool Engaged;
    }

    private static Target[] _targets = Array.Empty<Target>();

    /// <summary>我们吞过 keydown 的键：它们的 keyup 也要吞，保持事件平衡。</summary>
    private static readonly HashSet<int> Swallowed = new();

    /// <summary>一张贴图开好了：登记它的窗口句柄与执行回调（UI 线程）。</summary>
    public static void Register(IntPtr hwnd, Action<PinShortcutAction> fire)
    {
        if (hwnd == IntPtr.Zero) return;

        var list = new List<Target>(_targets.Length + 1);
        foreach (var target in _targets)
        {
            if (target.Hwnd != hwnd) list.Add(target);
        }

        list.Add(new Target { Hwnd = hwnd, Fire = fire });
        Volatile.Write(ref _targets, list.ToArray());
        Push(hwnd, PinStates.Release);
    }

    /// <summary>一张贴图关了：摘掉它，别再把键投给它。</summary>
    public static void Unregister(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var list = new List<Target>(_targets.Length);
        foreach (var target in _targets)
        {
            if (target.Hwnd != hwnd) list.Add(target);
        }

        if (list.Count == _targets.Length) return;

        Volatile.Write(ref _targets, list.ToArray());
        if (list.Count == 0) Swallowed.Clear();
        Push(hwnd, PinStates.Closed);
    }

    /// <summary>
    /// 点过 / 离开这张贴图（UI 线程）：点一下 = 把键盘交给它，指针离开就作废。
    /// </summary>
    public static void Engage(IntPtr hwnd, bool engaged)
    {
        if (hwnd == IntPtr.Zero) return;

        foreach (var target in Volatile.Read(ref _targets))
        {
            if (target.Hwnd == hwnd) target.Engaged = engaged;
        }

        Push(hwnd, engaged ? PinStates.Engage : PinStates.Release);
    }

    /// <summary>宿主：把「这张贴图接 / 不接管键盘」推给提权进程的钩子（贴图窗口在宿主里）。</summary>
    private static void Push(IntPtr hwnd, string state)
    {
        if (!ConfigStore.ReadOnly) return;
        InputBridge.PinState(hwnd.ToInt64(), state);
    }

    /// <summary>执行器：收到宿主推来的贴图状态（链路线程；留下的是「不在本进程」的影子）</summary>
    public static void ApplyState(long handle, string? state)
    {
        if (handle == 0) return;
        var hwnd = new IntPtr(handle);
        var targets = Volatile.Read(ref _targets);

        if (state == PinStates.Closed)
        {
            var kept = new List<Target>(targets.Length);
            foreach (var target in targets)
            {
                if (target.Hwnd != hwnd) kept.Add(target);
            }

            if (kept.Count != targets.Length) Volatile.Write(ref _targets, kept.ToArray());
            if (kept.Count == 0) Swallowed.Clear();
            return;
        }

        bool engaged = state == PinStates.Engage;
        foreach (var target in targets)
        {
            if (target.Hwnd == hwnd)
            {
                target.Engaged = engaged;
                return;
            }
        }

        if (!engaged) return;   // 没登记过又要取消：不用建影子

        var list = new List<Target>(targets.Length + 1);
        list.AddRange(targets);
        list.Add(new Target { Hwnd = hwnd, Remote = true, Engaged = true });
        Volatile.Write(ref _targets, list.ToArray());
    }

    /// <summary>宿主：收到执行器转来的「这张贴图收到某个动作」（UI 线程）。</summary>
    public static void FireLocal(long handle, string? action)
    {
        if (handle == 0 || !Enum.TryParse<PinShortcutAction>(action, ignoreCase: true, out var parsed)) return;

        var hwnd = new IntPtr(handle);
        foreach (var target in Volatile.Read(ref _targets))
        {
            if (target.Hwnd != hwnd) continue;
            try { target.Fire?.Invoke(parsed); }
            catch (Exception ex) { AppLog.Warn("CapturePro.Pin", "贴图快捷键执行失败：" + ex.Message); }
            return;
        }
    }

    /// <summary>当前登记了几张（冒烟 / 自检用）。</summary>
    internal static int Count => Volatile.Read(ref _targets).Length;

    /// <summary>这张贴图现在能不能接管按键（冒烟 / 自检用）。</summary>
    internal static bool IsEngaged(IntPtr hwnd)
    {
        foreach (var target in Volatile.Read(ref _targets))
        {
            if (target.Hwnd == hwnd) return target.Engaged;
        }

        return false;
    }

    /// <summary>钩子回调里调：命中就吞掉并投递。**别在这里碰 WPF**。</summary>
    public static bool Handle(ushort vk, bool isDown, bool isUp)
    {
        var targets = Volatile.Read(ref _targets);
        if (targets.Length == 0) return false;

        if (isUp) return Swallowed.Remove(vk);
        if (!isDown || vk == 0) return false;

        // 指针下的窗口必须是「点过的那张贴图」（点击穿透的贴图本来就不在 WindowFromPoint 里）
        IntPtr hwnd = Win32.TopLevelWindowAtCursor();
        if (hwnd == IntPtr.Zero) return false;

        Target? hit = null;
        foreach (var target in targets)
        {
            if (target.Hwnd != hwnd || !target.Engaged) continue;
            hit = target;
            break;
        }

        if (hit == null) return false;

        bool ctrl = Down(0xA2) || Down(0xA3) || Down(0x11);
        bool shift = Down(0xA0) || Down(0xA1) || Down(0x10);
        bool alt = Down(0xA4) || Down(0xA5) || Down(0x12);
        bool win = Down(0x5B) || Down(0x5C);

        return hit.Remote
            ? DispatchRemote(hit.Hwnd, vk, ctrl, shift, alt, win)
            : Dispatch(hit.Fire, vk, ctrl, shift, alt, win);
    }

    /// <summary>
    /// 命中就投递并返回 true（冒烟直接调这个，不用把鼠标真的挪到贴图上）。
    /// 修饰键状态由调用方给，钩子那边从 <c>GetAsyncKeyState</c> 取。
    /// </summary>
    internal static bool Dispatch(Action<PinShortcutAction>? fire, ushort vk,
        bool ctrl, bool shift, bool alt, bool win)
    {
        var action = PinShortcuts.Match(vk, ctrl, shift, alt, win);
        if (action == PinShortcutAction.None || fire == null) return false;

        Swallowed.Add(vk);

        Ui.Post(() =>
        {
            try { fire(action); }
            catch (Exception ex) { AppLog.Warn("CapturePro.Pin", "贴图快捷键执行失败：" + ex.Message); }
        });

        return true;
    }

    /// <summary>贴图在宿主里：命中就告诉宿主「这张贴图收到某个动作」（事件，别排队）。</summary>
    private static bool DispatchRemote(IntPtr hwnd, ushort vk, bool ctrl, bool shift, bool alt, bool win)
    {
        var action = PinShortcuts.Match(vk, ctrl, shift, alt, win);
        if (action == PinShortcutAction.None) return false;

        Swallowed.Add(vk);
        CaptureWindowBridge.Show(CaptureWindows.PinShortcut, action.ToString(), hwnd.ToInt64());
        return true;
    }

    /// <summary>测试 / 关闭时清空（别把上一条会话的句柄留着）。</summary>
    internal static void Clear()
    {
        Volatile.Write(ref _targets, Array.Empty<Target>());
        Swallowed.Clear();
    }

    /// <summary>
    /// 宿主断开了：它那边登记的贴图全都没了——只摘掉「影子」，本地贴图（降级时开在本进程里的）留着。
    /// </summary>
    /// <remarks>
    /// 影子留着的话，宿主重启后同一个句柄可能已经落到别的窗口上，钩子就会对着无关窗口吞键。
    /// 重连也不补发：贴图窗口靠「再点一下」重新 engage。
    /// </remarks>
    internal static void DropRemote()
    {
        var targets = Volatile.Read(ref _targets);
        var kept = new List<Target>(targets.Length);
        foreach (var target in targets)
        {
            if (!target.Remote) kept.Add(target);
        }

        if (kept.Count == targets.Length) return;
        Volatile.Write(ref _targets, kept.ToArray());
        if (kept.Count == 0) Swallowed.Clear();
    }

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
}
