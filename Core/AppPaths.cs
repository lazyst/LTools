namespace LTools.Core;

/// <summary>
/// 应用自身的目录约定。
/// <see cref="AppDirectory"/> 是 exe 所在目录；<see cref="DataRoot"/> 是用户数据（配置 / 资源目录 / 速记）的共同根：
/// dev 为仓库根（与 <see cref="ConfigLocator"/> 一致），prod 为 exe 同级。
/// </summary>
internal static class AppPaths
{
    /// <summary>应用所在目录（exe 同级）。</summary>
    public static string AppDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>仓库根 = exe 目录上溯 3 级（bin/&lt;Config&gt;/net10.0-windows/）。</summary>
    public static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

    /// <summary>开发布局：仓库根含 LTools.csproj。</summary>
    public static bool IsDevLayout => File.Exists(Path.Combine(RepoRoot, "LTools.csproj"));

    /// <summary>用户数据根：dev=仓库根，prod=exe 同级。</summary>
    public static string DataRoot => IsDevLayout ? RepoRoot : AppDirectory;

    /// <summary>用户资源目录（脚本 / 程序 / 素材），位于数据根下。</summary>
    public static string ResourceDir => Path.Combine(DataRoot, "resources");

    /// <summary>速记目录，位于数据根下（速记与备份共用同一路径）。</summary>
    public static string NotesDir => Path.Combine(DataRoot, "速记");

    /// <summary>
    /// 动作未指定工作目录时的默认值：数据根（目录不可用时回退桌面）。
    /// launchApp / runCommand 共用，避免两个功能各自默认到不同目录。
    /// </summary>
    public static string DefaultWorkdir() => Directory.Exists(DataRoot)
        ? DataRoot
        : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
}
