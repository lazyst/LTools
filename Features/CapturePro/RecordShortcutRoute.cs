using System;
using System.Collections.Generic;
using System.Threading;
using LTools.Core;
using LTools.Core.Link;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 把录屏面板的快捷键接到全局键盘钩子上（照上游 <c>screenrecordingshortcutcontroller.cpp</c>，
/// 区别只在作用范围：上游是窗口作用域，这里是指针停在录屏条上时生效）。
/// </summary>
/// <remarks>
/// <para>
/// 钩子线程只能碰纯数据：面板通过 <see cref="Publish"/> 交一份不可变快照（状态 + 指针是否在条上 +
/// 要执行的回调），<see cref="Handle"/> 只做「命中吗 / 能触发吗」，命中就 <see cref="Ui.Post"/>
/// 把动作排回 UI 线程并吞掉这个键。
/// </para>
/// <para>
/// **为什么要作用范围**：录屏条刻意不抢焦点（<c>WS_EX_NOACTIVATE</c>），没有「窗口焦点」可用；
/// 如果无差别接管 Ctrl+C / Esc / R，用户在被录的那个程序里按 Esc 就会把录制取消掉。
/// 所以只认「指针停在录屏条上」（用户正在操作我们的面板）——功能一样，误伤为零。
/// </para>
/// </remarks>
internal static class RecordShortcutRoute
{
    private sealed class Snapshot
    {
        public RecordShortcutState State { get; init; } = new();
        public bool Hot { get; init; }
        public Action<RecordShortcutAction>? Fire { get; init; }

        /// <summary>钩子与录屏条不在一个进程（录屏条在宿主）：命中了就把动作转过去。</summary>
        public bool Remote { get; init; }
    }

    private static Snapshot? _current;

    /// <summary>最后一次推给提权进程的快照（只在这份变了时才发，见 <see cref="Publish"/>）。</summary>
    private static RecordKeysEvent? _pushed;

    /// <summary>等待抬起的那个键（<c>end_recording</c> 是 Release 触发器）。</summary>
    private static ushort _pendingRelease;

    /// <summary>我们吞过 keydown 的键：它们的 keyup 也要吞，保持事件平衡。</summary>
    private static readonly HashSet<int> Swallowed = new();

    /// <summary>面板画完一帧就发布一次（UI 线程）。</summary>
    /// <remarks>
    /// 录屏条在宿主、钩子在提权进程：这里交的是本进程那份快照，另外把「钩子要用的那几个状态」
    /// 推过去（<c>record.keys</c>）。**只在快照变了时推**——这个方法是每帧调的。
    /// </remarks>
    public static void Publish(RecordShortcutState state, bool hot, Action<RecordShortcutAction> fire)
    {
        Volatile.Write(ref _current, new Snapshot { State = state.Clone(), Hot = hot, Fire = fire });

        if (AppRole.IsUiHost) PushKeys(state, hot);
    }

    /// <summary>宿主：把快照推给提权进程的钩子（只在变了时发）。</summary>
    private static void PushKeys(RecordShortcutState state, bool hot)
    {
        var keys = new RecordKeysEvent(hot, state.Recording, state.Paused, state.Busy,
            state.HasLastRecord, state.ExportRowVisible, state.AreaHistory,
            state.CanUndo, state.CanRedo, state.DrawingAllowed);
        if (_pushed == keys) return;

        _pushed = keys;
        InputBridge.RecordKeys(keys);
    }

    /// <summary>面板关了：解绑，钩子不再接管任何键。</summary>
    public static void Clear()
    {
        Volatile.Write(ref _current, null);
        _pendingRelease = 0;
        Swallowed.Clear();

        if (!AppRole.IsUiHost) return;
        _pushed = null;
        InputBridge.RecordKeys(new RecordKeysEvent(false, false, false, false,
            false, false, false, false, false, false));
    }

    /// <summary>执行器：收到宿主推来的录屏条快照（链路线程；只留「不在本进程」的影子）。</summary>
    public static void ApplyState(RecordKeysEvent keys)
    {
        var state = new RecordShortcutState
        {
            Recording = keys.Recording,
            Paused = keys.Paused,
            Busy = keys.Busy,
            HasLastRecord = keys.HasLastRecord,
            ExportRowVisible = keys.ExportRowVisible,
            AreaHistory = keys.AreaHistory,
            CanUndo = keys.CanUndo,
            CanRedo = keys.CanRedo,
            DrawingAllowed = keys.DrawingAllowed,
        };

        Volatile.Write(ref _current, new Snapshot { State = state, Hot = keys.Hot, Remote = true });
    }

    /// <summary>宿主断开了：它那边推来的快照作废（录屏条已经跟着宿主一起没了）。</summary>
    internal static void DropRemote()
    {
        if (Volatile.Read(ref _current) is not { Remote: true }) return;
        Volatile.Write(ref _current, null);
        _pendingRelease = 0;
        Swallowed.Clear();
    }

    /// <summary>当前快照（冒烟 / 自检读它，钩子线程之外也能调）。</summary>
    internal static bool IsPublished => Volatile.Read(ref _current) != null;

    /// <summary>钩子回调里调：命中就吞掉并投递。**别在这里碰 WPF**。</summary>
    public static bool Handle(ushort vk, bool isDown, bool isUp)
    {
        var snapshot = Volatile.Read(ref _current);
        if (snapshot == null) return false;

        // 抬起：先处理「等抬起的那个键」（上游 end_recording 的 Release），
        // 其余我们吞过的键也一并吞掉。
        if (isUp)
        {
            if (vk != 0 && vk == _pendingRelease)
            {
                _pendingRelease = 0;
                Fire(snapshot, RecordShortcutAction.EndRecording);
                return true;
            }

            if (Swallowed.Remove(vk)) return true;
            return false;
        }

        if (!isDown || vk == 0) return false;

        // 指针不在录屏条上：一个键都不碰（见类注释「为什么要作用范围」）
        if (!snapshot.Hot) return false;

        var action = RecordShortcuts.Match(vk,
            Down(0xA2) || Down(0xA3) || Down(0x11),
            Down(0xA0) || Down(0xA1) || Down(0x10),
            Down(0xA4) || Down(0xA5) || Down(0x12),
            Down(0x5B) || Down(0x5C),
            snapshot.State);

        if (action == RecordShortcutAction.None) return false;

        Swallowed.Add(vk);

        if (RecordShortcuts.FiresOnRelease(action))
        {
            _pendingRelease = vk;
            return true;   // 抬起时才执行（先在 keydown 吞掉，别让下面的程序收到）
        }

        Fire(snapshot, action);
        return true;
    }

    private static void Fire(Snapshot snapshot, RecordShortcutAction action)
    {
        // 录屏条在宿主里：把动作名转过去（宿主在自己的 UI 线程上点按钮）
        if (snapshot.Remote)
        {
            CaptureWindowBridge.Show(CaptureWindows.RecordShortcut, action.ToString());
            return;
        }

        var callback = snapshot.Fire;
        if (callback == null) return;

        // 回调碰 WPF（按钮 / 面板控件）：排回 UI 线程就返回，钩子回调不等待
        Ui.Post(() =>
        {
            try { callback(action); }
            catch (Exception ex) { AppLog.Warn("CapturePro.Record", "录屏快捷键执行失败：" + ex.Message); }
        });
    }

    /// <summary>宿主：收到执行器转来的「录屏条收到某个快捷键动作」（UI 线程）。</summary>
    public static void FireLocal(string? action)
    {
        if (!Enum.TryParse<RecordShortcutAction>(action, ignoreCase: true, out var parsed)) return;

        var callback = Volatile.Read(ref _current)?.Fire;
        try { callback?.Invoke(parsed); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "录屏快捷键执行失败：" + ex.Message); }
    }

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
}
