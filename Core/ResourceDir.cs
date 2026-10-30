using System.Diagnostics;
using System.Text;

namespace LTools.Core;

/// <summary>
/// 用户资源目录（<c>数据根/resources</c>）：存放脚本、程序、素材等，动作可用相对路径引用。
/// 首次启动自动创建并写入 <c>README.txt</c> 说明；升级解压不会删除用户已有文件。
/// </summary>
internal static class ResourceDir
{
    private const string ReadmeName = "README.txt";

    private const string ReadmeText =
        "resources\\ — LTools 用户资源目录\r\n" +
        "\r\n" +
        "这里存放你的脚本、程序或其他素材。动作编辑器里把「工作目录」留空，\r\n" +
        "runCommand / launchApp 用相对路径引用即可，例如：resources\\foo.ps1\r\n" +
        "\r\n" +
        "升级 LTools 不会删除本目录。删除本文件不影响功能。\r\n";

    /// <summary>资源目录路径（不保证存在）。</summary>
    public static string Get() => AppPaths.ResourceDir;

    /// <summary>确保资源目录存在（首次创建并写入说明文件）；返回目录路径。失败只记日志，不抛出。</summary>
    public static string Ensure()
    {
        string dir = AppPaths.ResourceDir;
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string readme = Path.Combine(dir, ReadmeName);
            if (!File.Exists(readme)) File.WriteAllText(readme, ReadmeText, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            CrashLog.Write("ResourceDirEnsure", ex);
        }
        return dir;
    }

    /// <summary>在资源管理器中打开资源目录（不存在则先创建）。</summary>
    public static void OpenInExplorer()
    {
        string dir = Ensure();
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("ResourceDirOpen", ex);
            TrayService.Notify("无法打开资源目录");
        }
    }
}
