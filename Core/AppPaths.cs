namespace LTools.Core;

/// <summary>应用自身的目录约定。</summary>
internal static class AppPaths
{
    /// <summary>应用所在目录（发布后 = exe 同级；开发时 = bin/Debug/net10.0-windows）。</summary>
    public static string AppDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>
    /// 动作未指定工作目录时的默认值：应用目录（目录不可用时回退桌面）。
    /// launchApp / runCommand 共用，避免两个功能各自默认到不同目录。
    /// </summary>
    public static string DefaultWorkdir()
    {
        string dir = AppDirectory;
        return Directory.Exists(dir)
            ? dir
            : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }
}
