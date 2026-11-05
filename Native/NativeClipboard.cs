using System.Runtime.InteropServices;
using System.Threading;

namespace LTools.Native;

/// <summary>
/// 原始 Win32 剪贴板读写（CF_UNICODETEXT）。
/// </summary>
/// <remarks>
/// <b>为何不用 WinForms Clipboard（OLE）？</b>——独立剪贴板/符号跳转的工作线程是裸 STA（无消息泵），
/// WinForms Clipboard 的 OLE 读取（<c>OleGetClipboard</c>→<c>GetData</c>）在跨进程复制后调用会
/// <b>长时间阻塞</b>（实测单次 5 秒，因 OLE 跨单元回 marshaling 需要消息泵而工作线程不泵送），
/// 导致 ClipWait 的 500ms 超时形同虚设、整条搜索链超时。
/// 原始 <c>OpenClipboard</c>/<c>GetClipboardData</c> 直接读 Win32 剪贴板层，不经 OLE，<b>不阻塞</b>，
/// 且与目标应用 <c>SetDataObject(text, copy:true)</c> 触发的 <c>OleFlushClipboard</c> 落到 Win32 层的数据一致。
/// </remarks>
/// <remarks>
/// 仅覆盖文本场景（CF_UNICODETEXT）。剪贴板的完整格式备份/恢复（IDataObject）仍走 WinForms OLE——
/// 那是每次操作前后各一次，不在热循环里，且 OLE 的 GetDataObject/SetDataObject 在剪贴板空闲时不会阻塞。
/// </remarks>
internal static class NativeClipboard
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern int GlobalSize(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>剪贴板被别的程序占用时的重试次数 / 间隔。</summary>
    /// <remarks>
    /// 剪贴板是全局独占资源：剪贴板管理器、云同步、截图工具都会在内容变化后立刻打开它。
    /// 写完后马上读（流程里「写入剪贴板」紧跟「取值 · 剪贴板文本」）或读之前正好被抢走，
    /// <c>OpenClipboard</c> 就会返回失败——不重试的话看起来就像内容凭空消失。
    /// 总等待上限约 60ms，远小于钩子 300ms 的约束。
    /// </remarks>
    private const int OpenRetries = 5;
    private const int OpenRetryDelayMs = 15;

    private static bool OpenWithRetry()
    {
        for (int i = 0; i < OpenRetries; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            if (i < OpenRetries - 1) Thread.Sleep(OpenRetryDelayMs);
        }
        return false;
    }

    /// <summary>清空剪贴板（EmptyClipboard）。</summary>
    public static void Clear()
    {
        if (OpenWithRetry())
        {
            try { EmptyClipboard(); } finally { CloseClipboard(); }
        }
    }

    /// <summary>读取剪贴板文本。无文本/失败返回 false。</summary>
    public static bool TryGetText(out string text)
    {
        text = "";
        if (!OpenWithRetry()) return false;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return false;
            int len = GlobalSize(h);
            if (len <= 0) return false;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return false;
            try
            {
                int charCount = len / 2;
                var buf = new char[charCount];
                Marshal.Copy(p, buf, 0, charCount);
                int end = Array.IndexOf(buf, '\0');
                text = end < 0 ? new string(buf) : new string(buf, 0, end);
                return true;
            }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>写入文本到剪贴板（CF_UNICODETEXT，立即落 Win32 层）。失败返回 false。</summary>
    public static bool SetText(string text)
    {
        if (!OpenWithRetry()) return false;
        try
        {
            EmptyClipboard();
            // 空串只清空
            if (string.IsNullOrEmpty(text)) return true;
            int bytes = (text.Length + 1) * 2; // 含终止符
            var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero) return false;
            var p = GlobalLock(hMem);
            if (p == IntPtr.Zero) return false;
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                // 终止符已由 GlobalAlloc 零初始化（GMEM_MOVEABLE 不保证零初始化，显式写）
                Marshal.WriteInt16(p + text.Length * 2, 0);
            }
            finally { GlobalUnlock(hMem); }
            // SetClipboardData 接管 hMem 所有权（勿再 Free）
            return SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero;
        }
        finally { CloseClipboard(); }
    }
}
