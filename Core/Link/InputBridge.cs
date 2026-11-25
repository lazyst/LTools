using System;
using LTools.Core;

namespace LTools.Core.Link;

/// <summary>
/// 全屏点选器状态的**宿主侧**：宿主开着遮罩时告诉提权进程「鼠标钩子让路」。
/// </summary>
/// <remarks>
/// 鼠标钩子（裸右键长按 = 面板）跑在提权进程里，而窗口 / 控件 / 框选遮罩都在宿主里——
/// 不让路的话遮罩收到的是被钩子吞掉又注入的右键，用户要连点两次才退得出去（踩过）。
/// 只发不排队：点选器是瞬时的，宿主没连上时本来也不会有遮罩。
/// </remarks>
internal static class InputBridge
{
    private static LinkServer? _link;
    private static bool _active;

    /// <summary>宿主启动链路服务端后挂上（与 <see cref="PanelHostBridge.Attach"/> 同一处）。</summary>
    public static void Attach(LinkServer server) => _link = server;

    /// <summary>开 / 关「点选器占用输入」的状态。</summary>
    public static void SetPickerActive(bool active)
    {
        if (_active == active) return;
        _active = active;

        try { _link?.NotifyAsync(LinkMethods.EventPickerActive, new PickerActiveEvent(active)); }
        catch (Exception ex) { AppLog.Warn("Link", $"通知点选器状态失败：{ex.Message}"); }
    }

    /// <summary>客户端（重新）连上时补一次当前状态：断线期间漏掉的那一次不能留错。</summary>
    public static void PushCurrent()
    {
        if (_active)
        {
            try { _link?.NotifyAsync(LinkMethods.EventPickerActive, new PickerActiveEvent(true)); }
            catch (Exception ex) { AppLog.Warn("Link", $"补发点选器状态失败：{ex.Message}"); }
        }

        // 录入框可能正开着：钩子那边断线时按「没在录入」处理过，这里补回来。
        if (Features.HotkeyCapture.IsHostCapturing)
        {
            try { _link?.NotifyAsync(LinkMethods.EventHotkeyCapture, new InputCaptureEvent(true)); }
            catch (Exception ex) { AppLog.Warn("Link", $"补发快捷键录入状态失败：{ex.Message}"); }
        }
    }

    /// <summary>
    /// 快捷键录入框开始 / 结束等按键：钩子在提权进程里，录入期间必须让路，
    /// 否则「录 CapsLock+S」会先把那个热键触发一遍。
    /// </summary>
    public static void SetHotkeyCapture(bool capturing)
    {
        try { _link?.NotifyAsync(LinkMethods.EventHotkeyCapture, new InputCaptureEvent(capturing)); }
        catch (Exception ex) { AppLog.Warn("Link", $"通知快捷键录入状态失败：{ex.Message}"); }
    }

    /// <summary>
    /// 「退出那一下的抬起要吞掉」：采样在宿主这边（窗口刚关、键还按着），钩子在提权进程那边上膛。
    /// </summary>
    /// <remarks>
    /// 遮罩 / 覆盖层都是「按下就走」：窗口一关，配对的那一下抬起会落到光标下面的程序上
    /// （资源管理器弹右键菜单、对话框像被按了一下回车）。宿主里没有钩子，只能把**采样结果**送过去——
    /// 让对方重新采样会在跨进程的几毫秒里读成「已经松开」而漏吞。
    /// </remarks>
    public static void Swallow(SwallowInputEvent sampled)
    {
        try { _link?.NotifyAsync(LinkMethods.EventInputSwallow, sampled); }
        catch (Exception ex) { AppLog.Warn("Link", $"通知吞掉退出按键失败：{ex.Message}"); }
    }

    /// <summary>
    /// 某张贴图接 / 不接管键盘了（贴图窗口在宿主，钩子在提权进程）：钩子按它决定要不要把键投过去。
    /// </summary>
    /// <remarks>
    /// 状态变化只有 <c>PinShortcutRoute</c> 一处（登记 / 点过 / 指针离开 / 关窗），
    /// 别在窗口里各发一次。断线重连不补发：贴图窗口靠「再点一下」重新 engage。
    /// </remarks>
    public static void PinState(long hwnd, string state)
    {
        try { _link?.NotifyAsync(LinkMethods.EventPinState, new PinStateEvent(hwnd, state)); }
        catch (Exception ex) { AppLog.Warn("Link", $"通知贴图接管状态失败：{ex.Message}"); }
    }

    /// <summary>
    /// 录屏条的快捷键快照变了（录屏条在宿主，钩子在提权进程）：钩子按它决定要不要接管按键。
    /// </summary>
    /// <remarks>
    /// 只在快照真的变了时调用（<c>RecordShortcutRoute</c> 自己比对），别每帧推一次——
    /// 录屏条画一帧就发布一次，全推过去会白占链路。
    /// </remarks>
    public static void RecordKeys(RecordKeysEvent snapshot)
    {
        try { _link?.NotifyAsync(LinkMethods.EventRecordKeys, snapshot); }
        catch (Exception ex) { AppLog.Warn("Link", $"通知录屏快捷键状态失败：{ex.Message}"); }
    }
}
