using System.Runtime.InteropServices;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 光标位置获取（对应原版 lib/CaretPos.ahk 的 <c>GetCaretPosition</c>）。
/// 阶段3 仅实现基础策略 <see cref="GetCaretPosFromGui"/>：GetGUIThreadInfo →
/// hwndCaret + rcCaret（客户端坐标）→ ClientToScreen 屏幕坐标。
/// 返回插入符左下角（{x:left, y:bottom}），与 AHK 一致，供符号跳转边界检测使用。
/// </summary>
/// <remarks>
/// 完整 5 策略（MSAA/UIA/WPF/Hook 注入）留阶段6：覆盖 Office/UWP/Electron 等无标准
/// 插入符的场景。此处基础策略对绝大多数编辑器（记事本/VSCode/浏览器输入框/Word 等）已足够。
/// </remarks>
internal static class CaretHelper
{
    /// <summary>
    /// 取当前插入符屏幕坐标。失败返回 null（无插入符或取信息失败）。
    /// </summary>
    public static Win32.Point? GetCaretPosition()
    {
        return GetCaretPosFromGui();
    }

    /// <summary>基础策略：GetGUIThreadInfo(0) 取 hwndCaret + rcCaret，转屏幕坐标。</summary>
    private static Win32.Point? GetCaretPosFromGui()
    {
        var info = new Win32.Guithreadinfo { cbSize = Marshal.SizeOf<Win32.Guithreadinfo>() };
        if (!Win32.GetGUIThreadInfo(0, ref info))
            return null;

        // hwndCaret 为 0 → 该焦点窗口未暴露标准插入符（如 Office/UWP），阶段3 放弃
        if (info.hwndCaret == IntPtr.Zero)
            return null;

        // rcCaret 为相对于 hwndCaret 的客户端坐标；取左下角转屏幕坐标
        var pt = new Win32.Point { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
        if (!Win32.ClientToScreen(info.hwndCaret, ref pt))
            return null;

        return pt;
    }
}
