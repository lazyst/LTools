using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LTools.Core;

/// <summary>
/// 进程级 DPI 感知的最早设置点。
/// 模块初始化器在 Main 之前、WPF（PresentationCore）加载之前运行，
/// 抢先把进程设为 PerMonitorV2——否则 WPF 会默认设成 System DPI Aware
/// （不处理 per-monitor DPI 变化，全屏游戏切分辨率后托盘菜单等坐标会陈旧错位）。
/// Release 构建由 app.manifest 声明 DPI 感知（进程创建时即生效），本调用为幂等 no-op；
/// Debug 构建用 -p:NoWin32Manifest=true 剥掉了清单（免 UAC），靠这里补设。
/// </summary>
internal static class DpiInitializer
{
    [ModuleInitializer]
    internal static void SetPerMonitorV2()
    {
        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (DPI_AWARENESS_CONTEXT)-4
        try
        {
            // 进程已设置（Release 经 manifest）则返回 false（ERROR_ACCESS_DENIED），无副作用。
            SetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch { /* pre-1703 系统无此导出，静默：回退到 WPF 默认 System 感知（即现状） */ }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
