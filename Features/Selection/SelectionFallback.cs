using System;
using System.Collections.Generic;
using System.Threading;
using LTools.Core;
using LTools.Native;

namespace LTools.Features.Selection;

/// <summary>
/// 「选中的文件」兜底方案：给前台窗口发 Ctrl+C，再从剪贴板读 <c>CF_HDROP</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认关闭</b>（步骤参数 <c>useClipboardFallback</c>）。它会模拟按键并临时占用剪贴板，
/// 和「读取选中文本」坚持不模拟按键、不动剪贴板的取向相反；只有资源管理器之外的
/// 第三方文件管理器（Total Commander / Directory Opus / 各类网盘客户端）才需要它。
/// </para>
/// <para>
/// <b>还原是 best-effort</b>：只备份 / 还原文本与文件列表两种载荷（图片 / HTML 等不备份）。
/// 如果原本剪贴板就是空的，则保留这次复制出来的文件（等于用户真的按了一次 Ctrl+C）。
/// </para>
/// </remarks>
internal static class SelectionFallback
{
    private const ushort VkC = 0x43;

    /// <summary>发 Ctrl+C 并等剪贴板出现文件列表；拿不到返回空列表。</summary>
    public static IReadOnlyList<string> Capture(int waitMs)
    {
        waitMs = Math.Clamp(waitMs, 50, 3000);

        NativeClipboard.TryGetText(out string oldText);
        bool hadFiles = NativeClipboard.TryGetFileList(out List<string> oldFiles, out bool oldCut);
        bool hadContent = hadFiles || oldText.Length > 0;

        try
        {
            NativeClipboard.Clear();
            InputHelper.ComboReleasingHeldModifiers(Win32.VkControl, VkC);

            int waited = 0;
            while (waited < waitMs)
            {
                if (NativeClipboard.TryGetFileList(out List<string> files) && files.Count > 0)
                {
                    AppLog.Info("Selection", $"Ctrl+C 兜底：拿到 {files.Count} 个路径");
                    return files;
                }
                Thread.Sleep(20);
                waited += 20;
            }

            AppLog.Info("Selection", $"Ctrl+C 兜底：{waitMs} ms 内剪贴板没有出现文件");
            return Array.Empty<string>();
        }
        finally
        {
            if (hadFiles)
            {
                if (!NativeClipboard.SetFileList(oldFiles, oldCut))
                    AppLog.Warn("Selection", "Ctrl+C 兜底：还原剪贴板文件列表失败");
            }
            else if (hadContent)
            {
                if (!NativeClipboard.SetText(oldText))
                    AppLog.Warn("Selection", "Ctrl+C 兜底：还原剪贴板文本失败");
            }
            // 原本就是空的：保留这次复制出来的文件
        }
    }
}
