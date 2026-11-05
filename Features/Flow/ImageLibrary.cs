using System;
using System.Collections.Generic;
using System.IO;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 图像模板的存放约定（都在数据根的 <c>resources/</c> 下，备份服务已整体打包，导出 / 导入无需额外处理）：
/// <list type="bullet">
/// <item><c>resources/images/</c>——程序生成的模板截图（<c>shot-时间戳.png</c>），由 <see cref="CollectGarbage"/> 按引用回收；</item>
/// <item><c>resources/user/</c>——用户自己的素材，文件选择器默认打开这里，从不自动删除。</item>
/// </list>
/// 分成两个目录是为了让回收边界是「目录」而不是「文件名前缀」：程序生成的东西可以放心清，
/// 用户资产不会被误伤。也允许引用任意绝对路径（这类文件不参与回收）。
/// </remarks>
internal static class ImageLibrary
{
    /// <summary>程序生成的模板目录（可回收）。</summary>
    public static string Dir => Path.Combine(AppPaths.ResourceDir, "images");

    /// <summary>用户素材目录（永不回收）。</summary>
    public static string UserDir => Path.Combine(AppPaths.ResourceDir, "user");

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
    public static string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Trim();

        if (Path.IsPathRooted(path)) return path;

        string underImages = Path.Combine(Dir, path);
        if (File.Exists(underImages)) return underImages;

        string underUser = Path.Combine(UserDir, path);
        if (File.Exists(underUser)) return underUser;

        string underRoot = Path.Combine(AppPaths.DataRoot, path);
        if (File.Exists(underRoot)) return underRoot;

        return underImages;
    }

    /// <summary>
    /// 保存到配置时用的路径：images 下的只存文件名（兼容老配置），数据根下的存数据根相对路径
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
    /// 把程序生成的图片存进 <see cref="Dir"/>（<c>shot-时间戳.png</c>，重名加序号），返回绝对路径。
    /// 模板截图与「实时截图」取值器共用；这类文件按引用回收（<see cref="CollectGarbage"/>），
    /// 所以流程运行时截的图不会一直堆着——下次启动按「没人引用」清掉。
    /// </summary>
    public static string SaveGenerated(System.Windows.Media.Imaging.BitmapSource image)
    {
        EnsureDir();
        string stem = $"shot-{DateTime.Now:yyyyMMdd-HHmmss}";
        string path = Path.Combine(Dir, stem + ".png");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(Dir, $"{stem}-{n}.png");

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
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
    /// 按引用回收模板截图：删除不被任何找图 / 变量赋值 / 条件 / 循环条件引用的 <c>shot-*.png</c>。
    ///
    /// 与 <see cref="MacroStore.CollectGarbage"/> 同一套思路（引用来自所有动作，含组合里的嵌套节点与
    /// 旧 Steps），在「启动」与「删除动作」时各跑一次。只扫 <see cref="Dir"/>，用户素材目录
    /// <see cref="UserDir"/> 整个不在回收范围内；<see cref="Dir"/> 里也仍按 <see cref="ShotPrefix"/>
    /// 过滤，避免误删用户以前直接放在 images 下的图。
    /// </summary>
    /// <returns>删除的文件数。</returns>
    public static int CollectGarbage()
    {
        try
        {
            if (!Directory.Exists(Dir)) return 0;

            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in ActionRegistry.All) CollectAction(a, live);

            int removed = 0;
            foreach (string file in Directory.GetFiles(Dir, ShotPrefix + "*.png"))
            {
                if (live.Contains(Path.GetFileName(file))) continue;
                try { File.Delete(file); removed++; }
                catch (Exception ex) { CrashLog.Write("ImageLibrary.CollectGarbage", ex); }
            }
            if (removed > 0)
                AppLog.Info("Resource", $"回收未被引用的模板截图 {removed} 个");
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

        // 组合动作：新流程（含旧 Steps 迁移）里的节点都可能引用模板
        if (a.Type == ActionType.composite)
            foreach (var n in FlowMigration.Ensure(a)) CollectNode(n, live);

        if (a.Steps == null) return;
        foreach (var s in a.Steps) CollectAction(s.Action, live);
    }

    private static void CollectNode(FlowNode n, HashSet<string> live)
    {
        CollectAction(n.Action, live);
        AddRef(n.Set?.Probe.Image, live);
        if (n.If != null)
        {
            AddRef(n.If.Condition.Probe.Image, live);
            foreach (var c in n.If.Then) CollectNode(c, live);
            if (n.If.Else != null)
                foreach (var c in n.If.Else) CollectNode(c, live);
        }
        if (n.Loop != null)
        {
            AddRef(n.Loop.While?.Probe.Image, live);
            foreach (var c in n.Loop.Body) CollectNode(c, live);
        }
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
