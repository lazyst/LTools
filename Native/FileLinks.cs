using System.IO;
using System.Runtime.InteropServices;

namespace LTools.Native;

/// <summary>
/// 文件级 Win32 调用：硬链接。
/// </summary>
/// <remarks>
/// 跨动作共享同一份素材（同一张模板图被两个动作引用）时，用硬链接代替复制：
/// 磁盘上只有一份内容、两个目录各有一个名字。删除其中一个名字不影响另一个；
/// 但**就地覆盖**会同时改掉两边，所以写动作目录时一律先删名字再写新文件
/// （见 <c>ActionStore.CopyFilesFrom</c>）。
/// </remarks>
internal static class FileLinks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(
        string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>
    /// 把 <paramref name="existingFile"/> 硬链接成 <paramref name="linkPath"/>；
    /// 目标已存在 / 跨卷 / 文件系统不支持时返回 false（调用方回退成复制）。
    /// </summary>
    public static bool TryCreateHardLink(string linkPath, string existingFile)
    {
        try
        {
            if (!File.Exists(existingFile) || File.Exists(linkPath)) return false;
            return CreateHardLinkW(linkPath, existingFile, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }
}
