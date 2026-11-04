using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LTools.Native;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;

namespace LTools.Features;

/// <summary>
/// 读取前台应用里当前选中的文本：备份剪贴板 → 清空 → 注入 Ctrl+C → 轮询文本 → 还原剪贴板。
/// </summary>
/// <remarks>
/// <para>
/// 剪贴板的 OLE 读写必须在带消息泵的 STA 线程（即 WPF UI 线程）进行：本应用其余剪贴板操作
/// 也遵循该约定（见 <see cref="NativeClipboard"/> 注释）。因此读取 / 还原都投递到
/// <see cref="Application.Dispatcher"/>，而清空 / 轮询走不阻塞的原始 Win32 剪贴板接口。
/// </para>
/// <para>
/// 备份是「把全部格式复制到本地 DataObject」的快照：清空剪贴板后原数据对象可能失效，
/// 只有本地快照才能可靠还原。数据量大时会短暂占用 UI 线程，但这是一次显式操作。
/// </para>
/// <para>
/// 局限（无法用通用方法绕开）：终端里 Ctrl+C 是中断；部分控件没有标准复制；
/// 锁屏 / 安全桌面不可用；读取期间会短暂占用系统剪贴板。
/// </para>
/// </remarks>
internal static class SelectionCapture
{
    /// <summary>读取选中文本；失败 / 无选中返回空串。</summary>
    public static string Capture(int timeoutMs)
    {
        timeoutMs = Math.Clamp(timeoutMs, 100, 5000);
        var dispatcher = Application.Current?.Dispatcher;

        // 没有 UI 时退化为纯文本备份 / 还原（仍能工作，只是丢失富格式）。
        bool uiBackup = dispatcher != null;
        DataObject? backup = uiBackup ? OnUi(dispatcher!, SnapshotClipboard) : null;
        string oldText = "";
        if (!uiBackup) NativeClipboard.TryGetText(out oldText);

        try
        {
            NativeClipboard.Clear();
            Thread.Sleep(30);   // 让剪贴板清空生效，避免读到旧内容
            InputHelper.Combo((ushort)Win32.VkControl, (ushort)'C');

            int waited = 0;
            string text = "";
            while (waited < timeoutMs)
            {
                if (NativeClipboard.TryGetText(out text) && text.Length > 0) break;
                Thread.Sleep(20);
                waited += 20;
            }

            // 目标应用可能稍晚写剪贴板；给一点固定余量再还原，避免把还没读完的内容抢走。
            Thread.Sleep(80);
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
