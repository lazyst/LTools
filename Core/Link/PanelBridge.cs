using System;
using System.Threading;
using System.Threading.Tasks;
using LTools.Core;

namespace LTools.Core.Link;

/// <summary>面板搬家的过渡开关。</summary>
/// <remarks>
/// 默认**关**：面板仍在提权进程里（现有行为）。设环境变量 <c>LTOOLS_PANEL_HOST=1</c> 时，
/// 提权进程会把面板唤起转给普通权限宿主，并在启动时把宿主拉起来。等面板的暂存区 / 编辑器 /
/// 菜单都接通了再把默认值翻过来。见 <c>docs/PROCESS_SPLIT.md</c> §14。
/// </remarks>
internal static class PanelRouting
{
    public static bool Enabled { get; } =
        string.Equals(Environment.GetEnvironmentVariable("LTOOLS_PANEL_HOST"), "1", StringComparison.Ordinal);
}

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
}
