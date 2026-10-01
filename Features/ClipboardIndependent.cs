using Clipboard = System.Windows.Clipboard;
using IDataObject = System.Windows.IDataObject;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 独立剪贴板（对应原版 lib/TextEdit.ahk 的 x/c/v 实现）。
/// 维护一个独立于系统剪贴板的私有缓冲 <see cref="AppState._clipboardIndependent"/>。
/// - 复制(C)/剪切(X)：备份系统剪贴板 → 清空 → 注入 Ctrl+C/X → 等待内容 → 存入私有 → 100ms 后恢复系统原剪贴板。
/// - 粘贴(V)：私有空则直接 Ctrl+V；否则备份系统剪贴板 → 写入私有内容 → 等 0.3s 让应用就绪 → 注入 Ctrl+V → 150ms 后恢复。
/// 所有 IO 在 STA 工作线程执行，钩子回调只 spawn 线程，绝不阻塞（防 >300ms 摘钩子）。
/// </summary>
internal static class ClipboardIndependent
{
    /// <summary>CapsLock+C 复制到独立剪贴板。</summary>
    public static void Copy() => RunOnSta(() => DoCopy(cut: false));

    /// <summary>CapsLock+X 剪切到独立剪贴板。</summary>
    public static void Cut() => RunOnSta(() => DoCopy(cut: true));

    /// <summary>CapsLock+V 从独立剪贴板粘贴。</summary>
    public static void Paste() => RunOnSta(DoPaste);

    // —— 实现 ——

    private static void DoCopy(bool cut)
    {
        var orig = BackupClipboard();
        NativeClipboard.Clear();
        // 发送 Ctrl+C / Ctrl+X（注入事件，钩子忽略放行，系统转发给前台）
        InputHelper.Combo((ushort)Win32.VkControl, cut ? (ushort)'X' : (ushort)'C');
        // 注入复制键后留 50ms 让目标应用产出选区/内容再读取（同 SymbolJump 的时序补丁）
        Thread.Sleep(50);
        if (ClipWait(500))
        {
            NativeClipboard.TryGetText(out var text);
            if (!string.IsNullOrEmpty(text)) AppState.SetIndependentClipboard(text);
        }
        // 100ms 后恢复原剪贴板
        Thread.Sleep(100);
        RestoreClipboard(orig);
    }

    private static void DoPaste()
    {
        var saved = AppState.GetIndependentClipboard();
        if (string.IsNullOrEmpty(saved))
        {
            // 私有空 → 直接系统粘贴
            InputHelper.Combo((ushort)Win32.VkControl, (ushort)'V');
            return;
        }

        var orig = BackupClipboard();
        NativeClipboard.Clear();
        NativeClipboard.SetText(saved);
        // 等 0.3s 让应用确认剪贴板就绪
        if (!ClipWaitReady(300))
        {
            RestoreClipboard(orig);
            return;
        }
        InputHelper.Combo((ushort)Win32.VkControl, (ushort)'V');
        Thread.Sleep(150);
        RestoreClipboard(orig);
    }

    /// <summary>轮询直到剪贴板有文本或超时。对应 AHK ClipWait(s, 0)。原始 Win32 读取（不阻塞，见 NativeClipboard）。</summary>
    private static bool ClipWait(int timeoutMs)
    {
        int slept = 0;
        while (slept < timeoutMs)
        {
            try { if (NativeClipboard.TryGetText(out var t) && !string.IsNullOrEmpty(t)) return true; }
            catch { /* 剪贴板被占用，继续等 */ }
            Thread.Sleep(20);
            slept += 20;
        }
        return false;
    }

    /// <summary>轮询直到剪贴板写入成功（含目标文本）或超时。</summary>
    private static bool ClipWaitReady(int timeoutMs)
    {
        int slept = 0;
        while (slept < timeoutMs)
        {
            try
            {
                if (NativeClipboard.TryGetText(out var t) && t == AppState.GetIndependentClipboard())
                    return true;
            }
            catch { }
            Thread.Sleep(20);
            slept += 20;
        }
        return false;
    }

    private static IDataObject? BackupClipboard()
    {
        try { return Clipboard.GetDataObject(); }
        catch { return null; }
    }

    private static void RestoreClipboard(IDataObject? orig)
    {
        try { if (orig != null) Clipboard.SetDataObject(orig, copy: false); }
        catch { /* 恢复失败静默 */ }
    }

    private static void RunOnSta(ThreadStart action)
    {
        var t = new Thread(() =>
        {
            try { action(); }
            catch { /* 剪贴板操作失败静默降级 */ }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}
