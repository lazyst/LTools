using System;
using System.Collections.Generic;
using System.IO;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features;

/// <summary>
/// 图像模板的存放约定。动作自己的图在 <c>actions/&lt;id&gt;/files/</c>（随动作目录一起删除 / 备份），
/// 共享与历史图仍在数据根的 <c>resources/</c> 下：
/// <list type="bullet">
/// <item><c>actions/&lt;id&gt;/files/</c>——该动作独占的模板图（采纳草稿时整体搬进来），按动作目录回收；</item>
/// <item><c>resources/images/</c>——程序生成的模板截图（<c>shot-时间戳.png</c>），由 <see cref="CollectGarbage"/> 按引用回收；</item>
/// <item><c>resources/user/</c>——用户自己的素材，文件选择器默认打开这里，从不自动删除；</item>
/// <item><c>cache/screenshots/</c>——「截图」步骤的运行期输出，下一次运行 / 启动时整体清空，不进备份。</item>
/// </list>
/// 分成多个目录是为了让回收边界是「目录」而不是「文件名前缀」：程序生成的东西可以放心清，
/// 用户资产与动作独占的资源不会被误伤。也允许引用任意绝对路径（这类文件不参与回收）。
/// </remarks>
internal static class ImageLibrary
{
    /// <summary>程序生成的模板目录（可回收）。</summary>
    public static string Dir => Path.Combine(AppPaths.ResourceDir, "images");

    /// <summary>用户素材目录（永不回收）。</summary>
    public static string UserDir => Path.Combine(AppPaths.ResourceDir, "user");

    /// <summary>
    /// 运行期截图目录：只服务当前这次运行（「截图」步骤的 <c>.file</c>），
    /// 下一次运行开始 / 启动时整体清空，不进备份。
    /// </summary>
    public static string CaptureDir => Path.Combine(AppPaths.DataRoot, "cache", "screenshots");

    /// <summary>本应用生成的模板文件名前缀。</summary>
    private const string ShotPrefix = "shot-";

    /// <summary>确保两个目录都存在（用户素材目录要能在文件对话框里当初始位置）。</summary>
    public static void EnsureDir()
    {
        try { Directory.CreateDirectory(Dir); } catch { /* 权限问题留给保存时提示 */ }
        try { Directory.CreateDirectory(UserDir); } catch { /* 同上 */ }
    }

    /// <summary>
    /// 把界面上填的路径解析为实际文件路径（找不到时返回最可能的候选，便于报错）。
    /// 依次尝试：绝对路径 → resources/images → resources/user → 数据根（覆盖 <c>resources\user\a.png</c> 这种存法）。
    /// </summary>
    public static string Resolve(string? path) => Resolve(path, null);

    /// <summary>
    /// 同上，但先查一个「资源根」再走常规顺序。
    /// </summary>
    /// <param name="resourceRoot">
    /// 本次运行的额外第一查找根（草稿试运行时传草稿 <c>files/</c>）：
    /// 动作里的 <c>files/x.png</c> 与裸文件名 <c>x.png</c> 都认。
    /// 为空（正式动作）时行为与单参版本完全一致。
    /// </param>
    public static string Resolve(string? path, string? resourceRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Trim();

        if (Path.IsPathRooted(path)) return path;

        if (!string.IsNullOrWhiteSpace(resourceRoot))
        {
            foreach (string candidate in new[] { path, StripFilesPrefix(path) })
            {
                string under = Path.Combine(resourceRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(under)) return under;
            }
        }

        string underImages = Path.Combine(Dir, path);
        if (File.Exists(underImages)) return underImages;

        string underUser = Path.Combine(UserDir, path);
        if (File.Exists(underUser)) return underUser;

        string underRoot = Path.Combine(AppPaths.DataRoot, path);
        if (File.Exists(underRoot)) return underRoot;

        // 草稿根下的兜底候选也要指对，否则报错会指到 resources/images 里一个不存在的位置
        if (!string.IsNullOrWhiteSpace(resourceRoot))
            return Path.Combine(resourceRoot, StripFilesPrefix(path).Replace('/', Path.DirectorySeparatorChar));

        return underImages;
    }

    /// <summary>草稿里图片按约定写在 <c>files/x.png</c>：查资源根时去掉这层前缀。</summary>
    private static string StripFilesPrefix(string path)
    {
        foreach (string prefix in new[] { "files/", "files\\" })
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return path[prefix.Length..];
        }
        return path;
    }

    /// <summary>
    /// 保存到配置时用的路径：images 下的只存文件名，数据根下的存数据根相对路径
    /// （<c>resources\user\a.png</c>，备份换机仍有效），其余保持绝对路径。
    /// </summary>
    public static string ToStoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            string full = Path.GetFullPath(path);

            string imagesPrefix = Path.GetFullPath(Dir) + Path.DirectorySeparatorChar;
            if (full.StartsWith(imagesPrefix, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(Dir, full);

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataRoot)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(AppPaths.DataRoot, full);
        }
        catch { /* 非合法路径按原样保存 */ }
        return path;
    }

    /// <summary>
    /// 把用户选的外部图片收进 <c>resources/user/</c>（重名自动加序号），返回可存进配置的路径。
    /// 已经在本应用两个目录里的不复制；同名同内容已存在时复用（这个目录永不回收，重复文件会一直留着）。
    /// 这样备份换机后模板仍然找得到，不会因为绝对路径失效。
    /// </summary>
    public static string ImportUserImage(string sourcePath)
    {
        try
        {
            string full = Path.GetFullPath(sourcePath);
            string fileDir = Path.GetDirectoryName(full) ?? "";
            foreach (string owned in new[] { Dir, UserDir })
            {
                string ownedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(owned));
                if (string.Equals(fileDir, ownedDir, StringComparison.OrdinalIgnoreCase))
                    return ToStoredPath(full);
            }

            Directory.CreateDirectory(UserDir);

            string stem = Path.GetFileNameWithoutExtension(full);
            string ext = Path.GetExtension(full);
            string first = Path.Combine(UserDir, Path.GetFileName(full));

            // 反复选同一张外部图：同名同内容就复用。这个目录永不回收，多存一份就永久多占一份。
            if (File.Exists(first) && SameContent(full, first)) return ToStoredPath(first);

            string dest = first;
            for (int n = 2; File.Exists(dest); n++)
                dest = Path.Combine(UserDir, $"{stem}-{n}{ext}");

            File.Copy(full, dest);
            return ToStoredPath(dest);
        }
        catch
        {
            // 复制失败（源被删 / 无权限）就退回原路径，至少这次能存下来
            return ToStoredPath(sourcePath);
        }
    }

    /// <summary>
    /// 把程序生成的模板图片存进 <see cref="Dir"/>（<c>shot-时间戳.png</c>，重名加序号），返回绝对路径。
    /// 模板截图与「剪贴板图片」取值器共用；这类文件按引用回收（<see cref="CollectGarbage"/>）：
    /// 模板截图随配置长期保留，未被动作引用的在下一次运行开始时清掉。
    /// </summary>
    public static string SaveGenerated(System.Windows.Media.Imaging.BitmapSource image)
    {
        EnsureDir();
        return SavePng(image, Dir);
    }

    /// <summary>同上，直接保存 GDI+ 位图（模板截图 / 剪贴板图片用）。</summary>
    public static string SaveGenerated(System.Drawing.Bitmap image)
    {
        EnsureDir();
        return SavePng(image, Dir);
    }

    /// <summary>
    /// 运行期截图（「截图」步骤的输出）存进 <see cref="CaptureDir"/>：本次运行内一直可用
    /// （文字识别 / 打开文件 / 剪贴板都还能读），下一次运行开始或程序启动时整体清空。
    /// </summary>
    public static string SaveCapture(System.Windows.Media.Imaging.BitmapSource image)
    {
        EnsureCaptureDir();
        return SavePng(image, CaptureDir);
    }

    /// <summary>同上，直接保存 GDI+ 位图（「截图」步骤的自动取图用）。</summary>
    public static string SaveCapture(System.Drawing.Bitmap image)
    {
        EnsureCaptureDir();
        return SavePng(image, CaptureDir);
    }

    private static void EnsureCaptureDir()
    {
        try { Directory.CreateDirectory(CaptureDir); } catch { /* 保存时会再报错 */ }
    }

    private static string SavePng(System.Windows.Media.Imaging.BitmapSource image, string dir)
    {
        string path = NextShotPath(dir);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    private static string SavePng(System.Drawing.Bitmap image, string dir)
    {
        string path = NextShotPath(dir);
        image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    private static string NextShotPath(string dir)
    {
        string stem = $"shot-{DateTime.Now:yyyyMMdd-HHmmss}";
        string path = Path.Combine(dir, stem + ".png");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, $"{stem}-{n}.png");
        return path;
    }

    /// <summary>清空运行期截图目录（下一次运行开始 / 启动时调用）；返回删除的文件数。</summary>
    public static int ClearCaptures()
    {
        try
        {
            if (!Directory.Exists(CaptureDir)) return 0;
            int removed = 0;
            foreach (string file in Directory.GetFiles(CaptureDir))
            {
                try { File.Delete(file); removed++; }
                catch (Exception ex) { CrashLog.Write("ImageLibrary.ClearCaptures", ex); }
            }
            if (removed > 0)
                AppLog.Info("Resource", $"清空运行期截图 {removed} 个");
            return removed;
        }
        catch (Exception ex)
        {
            CrashLog.Write("ImageLibrary.ClearCaptures", ex);
            return 0;
        }
    }

    /// <summary>
    /// 删掉一张程序生成的图片（模板试截 / 运行期截图）。只动 <see cref="Dir"/> 与
    /// <see cref="CaptureDir"/> 下的文件，误传别的路径（用户素材 / 动作目录）一律不碰；失败忽略。
    /// </summary>
    public static void DeleteGenerated(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = Path.GetFullPath(Resolve(path));
            foreach (string owned in new[] { Dir, CaptureDir })
            {
                string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(owned));
                if (!string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(full)) File.Delete(full);
                return;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("ImageLibrary.DeleteGenerated", ex);
        }
    }

    /// <summary>两个文件内容是否完全一致（先比长度，再比 SHA-256）。读取失败按「不一致」处理。</summary>
    private static bool SameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;

            using var sa = System.Security.Cryptography.SHA256.Create();
            using var streamA = File.OpenRead(a);
            byte[] ha = sa.ComputeHash(streamA);
            using var streamB = File.OpenRead(b);
            byte[] hb = sa.ComputeHash(streamB);
            return ha.AsSpan().SequenceEqual(hb);
        }
        catch
        {
            return false;
        }
    }

    // ============ 引用回收 ============

    /// <summary>
    /// 按引用回收程序生成的截图：删除不被任何找图 / 变量赋值 / 条件 / 循环条件引用的 <c>shot-*.png</c>。
    ///
    /// 与 <see cref="MacroStore.CollectGarbage"/> 同一套思路（引用来自所有动作，含组合里的嵌套节点与
    /// 旧 Steps），在「启动」「删除动作」与「每次流程运行开始时」各跑一次。只扫 <see cref="Dir"/>，
    /// 用户素材目录 <see cref="UserDir"/> 整个不在回收范围内；<see cref="Dir"/> 里也仍按
    /// <see cref="ShotPrefix"/> 过滤，避免误删用户以前直接放在 images 下的图。
    /// </summary>
    /// <param name="olderThan">
    /// 只删在这个时间点之前写下的文件（<c>null</c> = 全都算）。流程运行开始时传本次开始的时间：
    /// 上一次运行留下的会被清掉，本次稍后截的比它新，不会被误删。
    /// </param>
    /// <returns>删除的文件数。</returns>
    public static int CollectGarbage(DateTime? olderThan = null)
    {
        try
        {
            if (!Directory.Exists(Dir)) return 0;

            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in ActionRegistry.All) CollectAction(a, live);

            // 全局子动作也在用模板图：漏了它们，只有子动作引用的截图会被当成孤儿删掉。
            foreach (var def in SubActionStore.LoadAll())
                foreach (var step in ProgramWalker.AllSteps(def))
                    AddRef(ProgramWalker.LiteralText(step, "image"), live);

            int removed = 0;
            foreach (string file in Directory.GetFiles(Dir, ShotPrefix + "*.png"))
            {
                if (live.Contains(Path.GetFileName(file))) continue;
                if (olderThan is DateTime cutoff && File.GetLastWriteTime(file) >= cutoff) continue;
                try { File.Delete(file); removed++; }
                catch (Exception ex) { CrashLog.Write("ImageLibrary.CollectGarbage", ex); }
            }
            if (removed > 0)
                AppLog.Info("Resource", $"回收没人引用的截图 {removed} 个");
            return removed;
        }
        catch (Exception ex)
        {
            CrashLog.Write("ImageLibrary.CollectGarbage", ex);
            return 0;
        }
    }

    private static void CollectAction(ActionDto? a, HashSet<string> live)
    {
        if (a == null) return;

        // 所有单元的 image 参数（含嵌套块与内联取值步骤）
        foreach (var step in ProgramWalker.AllSteps(a.Program))
            AddRef(ProgramWalker.LiteralText(step, "image"), live);
    }

    /// <summary>登记引用；只关心直接落在 <see cref="Dir"/> 下的文件（绝对路径 / 其它目录不参与回收）。</summary>
    private static void AddRef(string? image, HashSet<string> live)
    {
        if (string.IsNullOrWhiteSpace(image)) return;
        try
        {
            string full = Path.GetFullPath(Resolve(image));
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Dir));
            if (!string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase)) return;
            live.Add(Path.GetFileName(full));
        }
        catch { /* 非法路径按「不引用」处理 */ }
    }
}
