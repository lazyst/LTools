using System.Diagnostics;
using Clipboard = System.Windows.Clipboard;
using IDataObject = System.Windows.IDataObject;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 智能搜索（对应原版 lib/QuickNote.ahk 的 QuickSearch）。
/// CapsLock+Q：复制当前选中文本 → 若为 URL 用浏览器打开；若为绝对路径用资源管理器打开；
/// 其余用 Bing 搜索。所有 IO 在 STA 工作线程执行（注入 Ctrl+C + 剪贴板读取 + Sleep），
/// 钩子回调只 spawn 线程，绝不阻塞（防 &gt;300ms 摘钩子）。
/// </summary>
/// <remarks>
/// 剪贴板读取一律走 <see cref="NativeClipboard"/>（原始 Win32，不阻塞裸 STA 线程，
/// 见 NativeClipboard 文档与旧 handoff 根因 B）。注入 Ctrl+C 后补 50ms 让目标建立选区再读
/// （旧 handoff 根因 A：选区键与复制键零间隔会复制空内容）。
/// </remarks>
internal static class QuickSearch
{
    public static void Start() => RunOnSta(DoSearch);

    private static void DoSearch()
    {
        // 备份系统剪贴板（富格式），清空，注入 Ctrl+C 复制选中内容
        var orig = BackupClipboard();
        NativeClipboard.Clear();
        InputHelper.Combo((ushort)Win32.VkControl, (ushort)'C');
        Thread.Sleep(50); // 选区建立时序（同 ClipboardIndependent / SymbolJump）
        if (!ClipWait(300))
        {
            RestoreClipboard(orig);
            return;
        }
        if (!NativeClipboard.TryGetText(out var raw) || raw == null)
        {
            RestoreClipboard(orig);
            return;
        }
        RestoreClipboard(orig);

        var text = raw.Trim();
        if (text.Length == 0) return;

        // URL → 浏览器打开
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            RunShell(text);
            ShowTooltip("打开链接: " + text);
            return;
        }

        // 绝对路径 → 资源管理器打开
        if (text.Length >= 3 && text[1] == ':' && text[2] == '\\' &&
            (File.Exists(text) || Directory.Exists(text)))
        {
            RunExplorer(text);
            ShowTooltip("打开路径: " + text);
            return;
        }

        // 其余 → Bing 搜索（Uri.EscapeDataString 与 AHK _UriEncode 等价：保留 A-Za-z0-9-_.~，其余百分号编码）
        string url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(text);
        RunShell(url);
        ShowTooltip("Bing 搜索: " + text);
    }

    /// <summary>轮询直到剪贴板有文本或超时（对应 AHK ClipWait(s, 0)，原始 Win32 不阻塞）。</summary>
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

    private static void RunShell(string cmd)
    {
        try { Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true }); }
        catch (Exception ex) { CrashLog.Write("QuickSearchRun", ex); }
    }

    private static void RunExplorer(string path)
    {
        try { Process.Start("explorer.exe", "\"" + path + "\""); }
        catch (Exception ex) { CrashLog.Write("QuickSearchOpenPath", ex); }
    }

    private static void ShowTooltip(string msg) =>
        TrayService.Notify(msg);

    private static void RunOnSta(ThreadStart action)
    {
        var t = new Thread(() =>
        {
            try { action(); }
            catch { /* 搜索失败静默降级 */ }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}
