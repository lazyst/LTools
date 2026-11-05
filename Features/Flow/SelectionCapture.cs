using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;
using LTools.Native;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;

namespace LTools.Features;

/// <summary>
/// 读取前台应用里当前选中的文本。
/// </summary>
/// <remarks>
/// <para>
/// 优先走 UI Automation 的 TextPattern：只通过系统辅助功能接口询问控件「你选了什么」，
/// <b>不注入按键、不动剪贴板</b>，浏览器 / Office / Electron / WinUI / 标准 Win32 编辑框都支持。
/// </para>
/// <para>
/// 个别老控件没有 UIA 文本模式时，退化为给焦点窗口发 <c>WM_COPY</c>（标准「复制」消息，
/// 仍不模拟按键），读剪贴板后把原内容原样还原。剪贴板的 OLE 读写必须在带消息泵的 STA
/// 线程（WPF UI 线程）进行，所以备份 / 还原都投递到 <see cref="Application.Dispatcher"/>。
/// </para>
/// </remarks>
internal static class SelectionCapture
{
    /// <summary>读取选中文本；失败 / 无选中返回空串。</summary>
    public static string Capture(int timeoutMs)
    {
        timeoutMs = Math.Clamp(timeoutMs, 100, 5000);

        string text = ReadWithUia();
        return text.Length > 0 ? text : ReadWithWmCopy(timeoutMs);
    }

    // —— 首选：UI Automation（无按键、无剪贴板） ——

    /// <summary>复用 UIA 客户端（每次新建要重新初始化 COM，循环里反复读选区会明显变慢）。</summary>
    private static readonly Lazy<UIA3Automation> Automation = new(() => new UIA3Automation
    {
        // 目标应用无响应时不要让读取无限期挂住
        ConnectionTimeout = TimeSpan.FromMilliseconds(500),
        TransactionTimeout = TimeSpan.FromMilliseconds(800),
    });

    private static string ReadWithUia()
    {
        try
        {
            AutomationElement? element = Automation.Value.FocusedElement();

            // 焦点元素本身不一定暴露 TextPattern（例如内层文本节点），向上找若干层
            for (int depth = 0; element != null && depth < 8; depth++)
            {
                if (element.Properties.ProcessId.ValueOrDefault == Environment.ProcessId)
                    return "";   // 焦点在 LTools 自己窗口里：不读自己的控件

                if (element.Patterns.Text.PatternOrDefault is { } pattern)
                {
                    string text = SelectionText(pattern);
                    if (text.Length > 0) return text;
                }
                element = SafeParent(element);
            }
        }
        catch
        {
            // UIA 不可用 / 目标无响应：交给 WM_COPY
        }
        return "";
    }

    private static string SelectionText(ITextPattern pattern)
    {
        var sb = new StringBuilder();
        foreach (var range in pattern.GetSelection())
        {
            string part = range.GetText(-1);
            if (part.Length > 0) sb.Append(part);
        }
        return sb.ToString();
    }

    private static AutomationElement? SafeParent(AutomationElement element)
    {
        try { return element.Parent; }
        catch { return null; }
    }

    // —— 兜底：WM_COPY（标准复制消息，不模拟按键） ——

    private static string ReadWithWmCopy(int timeoutMs)
    {
        var info = new Win32.Guithreadinfo { cbSize = Marshal.SizeOf<Win32.Guithreadinfo>() };
        if (!Win32.GetGUIThreadInfo(0, ref info) || info.hwndFocus == IntPtr.Zero) return "";
        if (IsOurWindow(info.hwndFocus)) return "";

        var dispatcher = Application.Current?.Dispatcher;
        bool uiBackup = dispatcher != null;
        DataObject? backup = uiBackup ? OnUi(dispatcher!, SnapshotClipboard) : null;
        string oldText = "";
        if (!uiBackup) NativeClipboard.TryGetText(out oldText);

        try
        {
            NativeClipboard.Clear();
            Win32.SendMessageTimeout(info.hwndFocus, Win32.WmCopy, IntPtr.Zero, IntPtr.Zero,
                Win32.SmtoAbortIfHung, 300, out _);

            int waited = 0;
            string text = "";
            while (waited < timeoutMs)
            {
                if (NativeClipboard.TryGetText(out text) && text.Length > 0) break;
                Thread.Sleep(20);
                waited += 20;
            }

            // 目标应用可能稍晚写剪贴板；给一点固定余量再还原，避免把还没读完的内容抢走。
            Thread.Sleep(60);
            return text;
        }
        finally
        {
            if (uiBackup && backup != null)
            {
                OnUi(dispatcher!, () =>
                {
                    try { Clipboard.SetDataObject(backup, copy: true); }
                    catch { /* 还原失败静默，不影响选区读取 */ }
                });
            }
            else if (!uiBackup && oldText.Length > 0)
            {
                NativeClipboard.SetText(oldText);
            }
        }
    }

    private static bool IsOurWindow(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    // —— 剪贴板快照 / 还原（只在兜底路径用到） ——

    /// <summary>把剪贴板所有可用格式复制到本地 DataObject（清空后仍可还原）。</summary>
    private static DataObject? SnapshotClipboard()
    {
        try
        {
            var source = Clipboard.GetDataObject();
            if (source == null) return null;

            var snapshot = new DataObject();
            foreach (string format in source.GetFormats(autoConvert: false))
            {
                try
                {
                    object? data = source.GetData(format, autoConvert: false);
                    if (data != null) snapshot.SetData(format, data);
                }
                catch { /* 某些延迟渲染格式可能取不到，跳过 */ }
            }
            return snapshot.GetFormats().Length > 0 ? snapshot : null;
        }
        catch
        {
            return null;
        }
    }

    private static T? OnUi<T>(Dispatcher dispatcher, Func<T?> func)
    {
        try { return dispatcher.Invoke(func); }
        catch { return default; }
    }

    private static void OnUi(Dispatcher dispatcher, Action action)
    {
        try { dispatcher.Invoke(action); }
        catch { /* 忽略 */ }
    }
}
