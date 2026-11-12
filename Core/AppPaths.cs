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

    /// <summary>仓库根 = exe 目录上溯 3 级（bin/&lt;Config&gt;/&lt;TFM&gt;/）。</summary>
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

    /// <summary>运行日志目录，位于数据根下（已 gitignore，按天分文件）。</summary>
    public static string LogsDir => Path.Combine(DataRoot, "logs");

    /// <summary>全局子动作库，位于数据根下（一个子动作一个 JSON 文件）。</summary>
    public static string SubActionsDir => Path.Combine(DataRoot, "subactions");

    /// <summary>跨动作状态目录（「存状态 / 读状态」动作单元），位于数据根下，进备份。</summary>
    public static string StateDir => Path.Combine(DataRoot, "state");

    /// <summary>
    /// 动作未指定工作目录时的默认值：数据根（目录不可用时回退桌面）。
    /// launchApp / runCommand 共用，避免两个功能各自默认到不同目录。
    /// </summary>
    public static string DefaultWorkdir() => Directory.Exists(DataRoot)
        ? DataRoot
        : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    /// <summary>
    /// 解析动作的工作目录：空 → <see cref="DefaultWorkdir"/>；绝对路径原样；
    /// 相对路径（如 <c>resources</c>、<c>resources/sub</c>）先按数据根解析，存在则用绝对路径。
    /// 斜杠 / 反斜杠均可（<see cref="Path.GetFullPath"/> 统一为系统分隔符）。
    /// </summary>
    public static string ResolveWorkdir(string? workdir)
    {
        if (string.IsNullOrWhiteSpace(workdir)) return DefaultWorkdir();
        string w = workdir.Trim();
        if (Path.IsPathRooted(w)) return w;
        try
        {
            string candidate = Path.GetFullPath(Path.Combine(DataRoot, w));
            return Directory.Exists(candidate) ? candidate : w;
        }
        catch { return w; }
    }

    /// <summary>
    /// 解析相对目标：绝对路径原样；相对路径先按**数据根**解析（动作里自动转换出来的相对路径即相对数据根），
    /// 再按工作目录解析，任一存在则返回绝对路径，否则原样返回（裸文件名继续走 PATH，URL / 文档交给 ShellExecute 回退）。
    /// 不依赖目标里有没有 <c>\</c> / <c>/</c>——<c>foo.bat</c> 与 <c>resources/foo.bat</c> 一视同仁。
    /// </summary>
    public static string ResolveTarget(string target, string workdir)
        => ResolvePath(target, workdir, allowDirectory: false);

    /// <summary>同 <see cref="ResolveTarget"/>，但命中项也可以是**目录**（openFolder 用）。
    /// <paramref name="target"/> 为 null / 空时返回空串（调用方按空值报「缺少路径」）。</summary>
    public static string ResolvePath(string? target, string workdir, bool allowDirectory)
    {
        if (string.IsNullOrEmpty(target)) return "";
        if (Path.IsPathRooted(target)) return target;
        try
        {
            foreach (string baseDir in new[] { DataRoot, workdir })
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                string candidate = Path.GetFullPath(Path.Combine(baseDir, target));
                if (File.Exists(candidate) || (allowDirectory && Directory.Exists(candidate)))
                    return candidate;
            }
        }
        catch { /* 非法路径字符等，原样返回 */ }
        return target;
    }

    /// <summary>
    /// 把数据根下的**绝对**路径转成相对路径（相对数据根）：浏览 / 保存动作时自动调用，
    /// 让「启动程序」的路径与「工作目录」在备份换机后仍可用。相对路径与数据根之外的路径原样保留；
    /// 恰好是数据根本身 → 空串（= 默认工作目录）。
    /// </summary>
    public static string ToRelativeIfUnderDataRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        string p = path.Trim();
        if (!Path.IsPathRooted(p)) return p;   // 已是相对路径，原样保留
        string full;
        try { full = Path.GetFullPath(p); }
        catch { return p; }
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataRoot));
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
        string prefix = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return p;
        return Path.GetRelativePath(root, full);
    }
}
