using System;
using System.IO;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 图像模板的存放约定：默认放在数据根的 <c>resources/images/</c>（备份服务已整体打包 resources/，
/// 导出 / 导入无需额外处理）；也允许引用任意绝对路径。
/// </summary>
internal static class ImageLibrary
{
    /// <summary>模板目录。</summary>
    public static string Dir => Path.Combine(AppPaths.ResourceDir, "images");

    /// <summary>确保模板目录存在。</summary>
    public static void EnsureDir()
    {
        try { Directory.CreateDirectory(Dir); } catch { /* 权限问题留给保存时提示 */ }
    }

    /// <summary>把界面上填的路径解析为实际文件路径（找不到时返回最可能的候选，便于报错）。</summary>
    public static string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Trim();

        if (Path.IsPathRooted(path)) return path;

        string underImages = Path.Combine(Dir, path);
        if (File.Exists(underImages)) return underImages;

        string underRoot = Path.Combine(AppPaths.DataRoot, path);
        if (File.Exists(underRoot)) return underRoot;

        return underImages;
    }

    /// <summary>保存到配置时尽量使用相对 resources/images 的短路径。</summary>
    public static string ToStoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetFullPath(Dir) + Path.DirectorySeparatorChar;
            if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(Dir, full);
        }
        catch { /* 非合法路径按原样保存 */ }
        return path;
    }
}
