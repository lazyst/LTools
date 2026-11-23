using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using LTools.Core;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 给窗口接上「从资源管理器拖文件进来」，走老式 <c>WM_DROPFILES</c>。
/// </summary>
/// <remarks>
/// <para>
/// WPF 的 <c>AllowDrop</c> 走 OLE 拖放，而 UIPI 会拦住「低完整性 → 高完整性」的跨进程调用，
/// 所以 <c>requireAdministrator</c> 的程序（本项目的 Release 包）从资源管理器拖文件进来
/// **永远收不到**，界面上表现为光标显示禁止、什么都不发生。
/// </para>
/// <para>
/// 这里把 <c>WM_DROPFILES</c> / <c>WM_COPYDATA</c> / <c>WM_COPYGLOBALDATA</c> /
/// <c>WM_NCHITTEST</c> 放行、再 <c>DragAcceptFiles</c> + <c>WS_EX_ACCEPTFILES</c>，
/// 只是为了「非提权时多一条老路 / 手工构造 HDROP 也能收」（冒烟这么验）。
/// </para>
/// <para>
/// **实测提权时 Explorer 连 <c>WM_DROPFILES</c> 都不发**（钩子数到 0），所以 Release 包里
/// 拖放就是用不了——替代是 <c>Ctrl+C</c> / <c>Ctrl+V</c> 或窗口里的剪贴板按钮。
/// 真要拖放，得让窗口跑在普通权限的进程里（见 <c>docs/LAN_SHARE.md</c> §14）。
/// </para>
/// </remarks>
internal static class FileDropBridge
{
    /// <summary>挂上桥；窗口句柄还没建好就等 <c>SourceInitialized</c>。</summary>
    /// <remarks>
    /// 同时决定 <c>AllowDrop</c>：**提权时必须是 false**。Explorer 看到窗口有 OLE 拖放目标就只走 OLE，
    /// 被 UIPI 拦下也不会退到 <c>WM_DROPFILES</c>；非提权时 OLE 能收文字 / 图片，两套一起开。
    /// </remarks>
    public static void Attach(Window window, Action<IReadOnlyList<string>> onFiles)
    {
        window.AllowDrop = !Elevation.IsElevated;

        if (PresentationSource.FromVisual(window) is HwndSource ready)
        {
            Enable(ready, onFiles);
            return;
        }

        window.SourceInitialized += OnInitialized;

        void OnInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnInitialized;
            if (PresentationSource.FromVisual(window) is HwndSource source) Enable(source, onFiles);
        }
    }

    private static void Enable(HwndSource source, Action<IReadOnlyList<string>> onFiles)
    {
        IntPtr hwnd = source.Handle;

        // 放行这几条消息，让低完整性的 Explorer 能把 WM_DROPFILES 投到提权进程里。
        // WM_NCHITTEST 也一起放行：Explorer 用它判断光标下面的窗口肯不肯收（实测少了它会一直显示禁止光标）。
        Win32.ChangeWindowMessageFilterEx(hwnd, Win32.WmDropFiles, Win32.MsgfltAllow, IntPtr.Zero);
        Win32.ChangeWindowMessageFilterEx(hwnd, Win32.WmCopyData, Win32.MsgfltAllow, IntPtr.Zero);
        Win32.ChangeWindowMessageFilterEx(hwnd, Win32.WmCopyGlobalData, Win32.MsgfltAllow, IntPtr.Zero);
        Win32.ChangeWindowMessageFilterEx(hwnd, Win32.WmNcHitTest, Win32.MsgfltAllow, IntPtr.Zero);

        // 声明「这个窗口收拖进来的文件」：DragAcceptFiles 登记内部标记，
        // WS_EX_ACCEPTFILES 让 Explorer 的光标变成「可以放」，两个都要。
        Win32.DragAcceptFiles(hwnd, true);
        long exStyle = Win32.GetWindowLongPtr(hwnd, Win32.GwlExstyle).ToInt64();
        Win32.SetWindowLongPtr(hwnd, Win32.GwlExstyle, (IntPtr)(exStyle | Win32.WsExAcceptFiles));

        source.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message != Win32.WmDropFiles) return IntPtr.Zero;

            handled = true;
            try
            {
                IReadOnlyList<string> files = ReadPaths(wParam);
                AppLog.Info("Share", $"拖放：WM_DROPFILES 收到 {files.Count} 个文件");
                if (files.Count > 0) onFiles(files);
            }
            finally
            {
                Win32.DragFinish(wParam);
            }

            return IntPtr.Zero;
        });
    }

    /// <summary>读出 HDROP 里的全部路径（调用方负责 DragFinish）。</summary>
    private static IReadOnlyList<string> ReadPaths(IntPtr hDrop)
    {
        var files = new List<string>();

        try
        {
            uint count = Win32.DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint length = Win32.DragQueryFile(hDrop, i, null, 0);
                if (length == 0) continue;

                var buffer = new StringBuilder((int)length + 1);
                if (Win32.DragQueryFile(hDrop, i, buffer, (uint)buffer.Capacity) > 0)
                    files.Add(buffer.ToString());
            }
        }
        catch
        {
            // HDROP 来自别的进程，坏了就当没拖。
        }

        return files;
    }
}
