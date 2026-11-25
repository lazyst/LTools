using System;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace LTools.Features;

/// <summary>
/// 系统文件 / 文件夹选择框的**唯一实现**（动作单元 <c>file.select</c> 与宿主替它弹窗都用这一份）。
/// </summary>
/// <remarks>
/// <para>
/// **必须在 UI 线程调用**（<c>ShowDialog</c> 会在这里跑消息循环）。两个进程都可能用到它：
/// 动作在执行器里跑、界面归宿主，所以宿主侧 <c>UiDialogHost</c> 也调这里。
/// </para>
/// <para>
/// 系统对话框是**模态且关不掉**的：这期间宿主 UI 线程被占（面板 / 其它窗口不响应），
/// 停止热键也收不掉它——用户按 Esc 就行。这是与截图遮罩一样的已知限制，别试图用定时器硬关。
/// </para>
/// </remarks>
internal static class FilePicker
{
    /// <summary>弹框；返回 (是否选中, 路径)。取消 / 关窗时路径为空。</summary>
    public static (bool Ok, string Path) TryPick(string kind, string title, string filter, string initialDir)
    {
        Window? owner = Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

        if (string.Equals(kind, "folder", StringComparison.OrdinalIgnoreCase))
        {
            var dialog = new OpenFolderDialog();
            if (title.Length > 0) dialog.Title = title;
            if (initialDir.Length > 0) dialog.InitialDirectory = initialDir;
            bool picked = owner == null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;
            return (picked, picked ? dialog.FolderName : "");
        }

        if (string.Equals(kind, "saveFile", StringComparison.OrdinalIgnoreCase))
        {
            var dialog = new SaveFileDialog
            {
                Title = title.Length > 0 ? title : "保存文件",
                Filter = filter.Length > 0 ? filter : "所有文件|*.*",
            };
            if (initialDir.Length > 0) dialog.InitialDirectory = initialDir;
            bool picked = owner == null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;
            return (picked, picked ? dialog.FileName : "");
        }

        var open = new OpenFileDialog
        {
            Title = title.Length > 0 ? title : "选择文件",
            Filter = filter.Length > 0 ? filter : "所有文件|*.*",
        };
        if (initialDir.Length > 0) open.InitialDirectory = initialDir;
        bool ok = owner == null ? open.ShowDialog() == true : open.ShowDialog(owner) == true;
        return (ok, ok ? open.FileName : "");
    }
}
