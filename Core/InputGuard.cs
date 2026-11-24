using System;
using LTools.Core.Link;
using LTools.Features;

namespace LTools.Core;

/// <summary>
/// 全局快捷键的「整体挂起 + 只放行一条」状态：**钩子线程读的那一份**，以及宿主的发起端。
/// </summary>
/// <remarks>
/// <para>
/// 全屏画布这种「铺满屏幕、自己收键」的窗口现在在宿主里，而钩子在提权进程，所以这条状态要过链路：
/// 宿主打开画布时推「全挂起，只留 <see cref="OnlyRef"/> 这一条」（<c>input.suspend</c>）；
/// 那条还放行的快捷键被按下时，钩子把键吞掉再转回宿主（<see cref="GlobalCanvasSession"/> 的
/// 「再按一次 = 切点击穿透」）——**别指望提权进程里有画布窗口**。
/// </para>
/// <para>
/// 进程内（宿主没连上、画布就在提权进程里开）时两个字段就是全部，行为与搬走之前一样。
/// </para>
/// </remarks>
internal static class InputGuard
{
    /// <summary>全局快捷键是否整体挂起（钩子线程读）。</summary>
    public static volatile bool Suspended;

    /// <summary>挂起期间唯一还放行的那条绑定引用（null = 一条都不放行）。</summary>
    public static volatile string? OnlyRef;

    private static LinkServer? _link;

    /// <summary>现在这份挂起状态是**宿主推来的**（宿主断开时要作废，本地画布那份不能跟着清）。</summary>
    private static volatile bool _remote;

    /// <summary>宿主启动链路服务端后挂上（与 <see cref="PanelHostBridge.Attach"/> 同一处）。</summary>
    public static void Attach(LinkServer server) => _link = server;

    /// <summary>
    /// 宿主：挂起全部全局快捷键，只放行 <paramref name="onlyRef"/> 这一条。
    /// </summary>
    /// <remarks>
    /// 提权进程里本地跑（宿主没连上）时，直接落 <see cref="HotkeyRegistry.Suspended"/>——钩子看的就是它。
    /// </remarks>
    public static void SetSuspended(bool suspended, string? onlyRef)
    {
        _remote = false;
        Suspended = suspended;
        OnlyRef = onlyRef;

        if (!AppRole.IsUiHost)
        {
            HotkeyRegistry.Suspended = suspended;
            return;
        }

        try { _link?.NotifyAsync(LinkMethods.EventInputSuspend, new InputSuspendEvent(suspended, onlyRef)); }
        catch (Exception ex) { AppLog.Warn("Link", $"通知快捷键挂起失败：{ex.Message}"); }
    }

    /// <summary>执行器：收到宿主的挂起状态（链路线程写，钩子线程读）。</summary>
    public static void Apply(InputSuspendEvent state)
    {
        _remote = true;
        Suspended = state.Suspended;
        OnlyRef = state.OnlyRef;
        HotkeyRegistry.Suspended = state.Suspended;
    }

    /// <summary>
    /// 宿主断开了：它推来的挂起状态作废（画布窗口已经跟着宿主一起没了）。
    /// </summary>
    /// <remarks>
    /// 不清的话宿主一崩，`Suspended` 就永远停住——全局快捷键全被吞掉，只能重启程序。
    /// 只清「宿主推来的」那一份：本地降级开的画布还在时不能顺手解开。
    /// </remarks>
    public static void Reset()
    {
        if (!_remote) return;
        _remote = false;
        Suspended = false;
        OnlyRef = null;
        HotkeyRegistry.Suspended = false;
    }

    /// <summary>客户端（重新）连上时补一次：断线期间画布可能已经开了 / 关了。</summary>
    public static void PushCurrent()
    {
        if (!Suspended && OnlyRef == null) return;
        try { _link?.NotifyAsync(LinkMethods.EventInputSuspend, new InputSuspendEvent(Suspended, OnlyRef)); }
        catch (Exception ex) { AppLog.Warn("Link", $"补发快捷键挂起状态失败：{ex.Message}"); }
    }
}
