using System;
using System.Collections.Generic;
using System.IO;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 图像模板的存放约定：默认放在数据根的 <c>resources/images/</c>（备份服务已整体打包 resources/，
/// 导出 / 导入无需额外处理）；也允许引用任意绝对路径。
/// </summary>
/// <remarks>
/// 「框选截图」生成的模板按 <c>shot-时间戳.png</c> 命名，由 <see cref="CollectGarbage"/> 按引用回收：
/// 删掉动作 / 改掉模板后旧截图不会自己消失，不回收就会一直堆在 resources/images 里。
/// </remarks>
internal static class ImageLibrary
{
    /// <summary>模板目录。</summary>
    public static string Dir => Path.Combine(AppPaths.ResourceDir, "images");

    /// <summary>本应用生成的模板文件名前缀（用户自己放进去的文件不动）。</summary>
    private const string ShotPrefix = "shot-";

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

    // ============ 引用回收 ============

    /// <summary>
    /// 按引用回收模板截图：删除不被任何找图 / 变量赋值 / 条件 / 循环条件引用的 <c>shot-*.png</c>。
    ///
    /// 与 <see cref="MacroStore.CollectGarbage"/> 同一套思路（引用来自所有动作，含组合里的嵌套节点与
    /// 旧 Steps），在「启动」与「删除动作」时各跑一次。只删本应用生成的 <see cref="ShotPrefix"/> 命名，
    /// 用户自己放进 resources/images、用绝对路径引用的文件不在回收范围内。
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

    /// <summary>登记引用；只关心直接落在 resources/images 下的文件（绝对路径 / 其它目录不参与回收）。</summary>
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
