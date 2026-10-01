using System.Runtime.InteropServices;

namespace LTools.Native;

/// <summary>
/// 文件夹选择对话框，封装 .NET 8 WPF 原生 <c>Microsoft.Win32.OpenFolderDialog</c>。
/// 该 API 由 PresentationFramework 提供（&lt;UseWPF&gt; 即可），内部走现代 COM
/// <c>IFileOpenDialog</c> + <c>FOS_PICKFOLDERS</c>（与 Windows 资源管理器同款 UI，
/// 左侧含「快速访问」/ 导航栏、可输入路径、最近访问等），自动适配各 Windows 版本
/// 的 broker 化差异。不依赖 WinForms、不依赖 WinRT 投影。
/// 返回所选文件夹完整路径，用户取消返回 null。
/// </summary>
internal static class FolderPicker
{
    /// <param name="ownerHwnd">父窗口句柄（保留兼容签名；WPF OpenFolderDialog 用当前活动窗口作父）。</param>
    /// <param name="title">对话框标题；null 用默认。</param>
    public static string? PickFolder(IntPtr ownerHwnd, string? title = null)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title ?? "选择文件夹",
        };
        bool? result = dlg.ShowDialog();
        return result == true && !string.IsNullOrWhiteSpace(dlg.FolderName)
            ? dlg.FolderName
            : null;
    }
}
