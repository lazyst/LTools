using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;

namespace LTools.Core.Link;

/// <summary>
/// 宿主侧（普通权限）：面板里的动作**绝不能在宿主里执行**，一律转给提权进程。
/// </summary>
/// <remarks>
/// 只在宿主进程里 <see cref="Attach"/>。提权进程里是 null，<see cref="TryForwardRun"/> 直接返回
/// false，走原来的本地执行。宿主连不上提权进程时**拦下**（返回 true）而不是退回本地执行——
/// 动作里可能有需要提权的键鼠注入 / 脚本，在中等完整性下跑会失败或行为不对。
/// </remarks>
internal static class PanelHostBridge
{
    private static LinkServer? _server;

    public static bool IsAttached => Volatile.Read(ref _server) != null;

    public static void Attach(LinkServer server) => Volatile.Write(ref _server, server);

    public static void Detach() => Volatile.Write(ref _server, null);

    /// <summary>
    /// 宿主把「运行这个引用」转给提权进程。返回 true = 已接管（调用方不要再本地执行）。
    /// </summary>
    public static bool TryForwardRun(string reference, int cursorX, int cursorY, RunSource source)
    {
        LinkServer? server = Volatile.Read(ref _server);
        if (server == null) return false;

        if (!server.IsConnected)
        {
            AppLog.Warn("Link", "宿主连不上主程序，动作没有执行（不会在宿主里本地跑）");
            return true;
        }

        _ = ForwardAsync(server, reference, cursorX, cursorY, source);
        return true;
    }

    private static async Task ForwardAsync(LinkServer server, string reference, int cursorX, int cursorY, RunSource source)
    {
        try
        {
            LinkMessage reply = await server
                .RequestAsync(LinkMethods.RunAction,
                    new RunActionRequest(reference, cursorX, cursorY, source.ToString()), timeoutMs: 5000)
                .ConfigureAwait(false);

            if (!reply.Ok) AppLog.Warn("Link", $"转交动作失败：{reply.Error}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Link", $"转交动作失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 关掉桌面上全部悬浮项（设置 → 面板管理那个按钮）：悬浮窗在提权进程里。
    /// 返回 true = 已接管（调用方别再本地关）。连不上时拦下并写日志。
    /// </summary>
    public static bool TryCloseAllFloating()
    {
        LinkServer? server = Volatile.Read(ref _server);
        if (server == null) return false;

        if (!server.IsConnected)
        {
            AppLog.Warn("Link", "宿主连不上主程序，悬浮项没有关闭");
            return true;
        }

        _ = ActionOpAsync(server, new ActionOpRequest(PanelOps.FloatCloseAll));
        return true;
    }

    /// <summary>
    /// 面板结构改动（拖磁贴 / 分组改名 / 增删分组场景…）落盘：宿主把整份配置交给提权进程。
    /// 返回 true = 已接管（<see cref="Features.Panel.PanelLibrary.Save"/> 据此认为保存成功）。
    /// </summary>
    public static bool TryPushPanel(LTools.Config.PanelConfig panel)
    {
        LinkServer? server = Volatile.Read(ref _server);
        if (server == null) return false;

        if (!server.IsConnected)
        {
            AppLog.Warn("Link", "宿主连不上主程序，面板改动没有落盘");
            return true;
        }

        _ = PushPanelAsync(server, panel);
        return true;
    }

    private static async Task PushPanelAsync(LinkServer server, LTools.Config.PanelConfig panel)
    {
        try
        {
            LinkMessage reply = await server
                .RequestAsync(LinkMethods.PanelReplace, panel, timeoutMs: 5000)
                .ConfigureAwait(false);

            if (!reply.Ok) AppLog.Warn("Link", $"面板落盘失败：{reply.Error}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Link", $"面板落盘失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 面板的动作级操作（新建 / 编辑 / 删除 / 粘贴…）：宿主转给提权进程。
    /// 返回 true = 已接管（调用方别再本地执行）。
    /// </summary>
    public static bool TryActionOp(ActionOpRequest op)
    {
        LinkServer? server = Volatile.Read(ref _server);
        if (server == null) return false;

        if (!server.IsConnected)
        {
            AppLog.Warn("Link", "宿主连不上主程序，操作没有执行");
            return true;
        }

        _ = ActionOpAsync(server, op);
        return true;
    }

    private static async Task ActionOpAsync(LinkServer server, ActionOpRequest op)
    {
        try
        {
            LinkMessage reply = await server
                .RequestAsync(LinkMethods.ActionOp, op, timeoutMs: 5000)
                .ConfigureAwait(false);

            if (!reply.Ok) AppLog.Warn("Link", $"面板操作失败（{op.Op}）：{reply.Error}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Link", $"面板操作失败（{op.Op}）：{ex.Message}");
        }
    }

    // —— 悬浮状态（宿主里没有悬浮库，菜单标签靠快照里那一份） ——
    private static HashSet<string> _floating = new(StringComparer.Ordinal);

    /// <summary>记下「哪些动作正在悬浮」（宿主拿到 <c>panel.snapshot</c> 时灌进来）。</summary>
    public static void SetFloating(IEnumerable<string>? ids) =>
        Volatile.Write(ref _floating, ids == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(ids, StringComparer.Ordinal));

    /// <summary>这个动作在提权进程里有没有悬浮项（菜单据此显示「悬浮 / 取消悬浮」）。</summary>
    public static bool IsFloating(string? actionId) =>
        !string.IsNullOrEmpty(actionId) && Volatile.Read(ref _floating).Contains(actionId);

    // —— 面板状态（提权进程这一侧：面板开在宿主里，`MouseHook` 要据此让开「长按右键」手势） ——

    private static int _remotePanelOpen;

    /// <summary>宿主里的面板开着（由宿主推 <c>panel.state</c>）。</summary>
    public static bool RemotePanelOpen => Volatile.Read(ref _remotePanelOpen) != 0;

    /// <summary>记下宿主那扇面板的开 / 关（断开时按关处理）。</summary>
    public static void SetRemotePanelOpen(bool open) => Volatile.Write(ref _remotePanelOpen, open ? 1 : 0);
}
