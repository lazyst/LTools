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
        if (!_active) return;
        try { _link?.NotifyAsync(LinkMethods.EventPickerActive, new PickerActiveEvent(true)); }
        catch (Exception ex) { AppLog.Warn("Link", $"补发点选器状态失败：{ex.Message}"); }
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
}
