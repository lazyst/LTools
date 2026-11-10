using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LTools.Config;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.Storage;

/// <summary>
/// 动作目录孤儿图回收（见 <c>docs/ARCHITECTURE.md</c> P5）：<c>actions/&lt;id&gt;/files/</c> 里
/// 没有任何步骤引用的图片移进 <c>trash/&lt;时间戳&gt;/orphans/&lt;id&gt;/</c>，**不直接删**。
/// </summary>
/// <remarks>
/// <para>
/// 引用来源是步骤的 <c>image</c> 参数（含嵌套块、内联取值步骤）。出现动态引用
/// （<c>${变量}</c>、<c>{"var": …}</c>、内联取值单元）时**整个动作跳过**——算不准就不动它。
/// </para>
/// <para>
/// 判定与运行期同一套路径语义：<c>files/x.png</c> 与裸 <c>x.png</c> 都先在本动作的
/// <c>files/</c> 下找；绝对路径 / <c>resources/…</c> 这类外部的图不归动作目录管，不参与。
/// </para>
/// </remarks>
internal static class OrphanFiles
{
    /// <summary>参与回收的文件类型（动作目录里的脚本等其它文件一律不动）。</summary>
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    /// <summary>
    /// 扫一遍所有动作的 <c>files/</c>，把没人引用的图片移进 trash。返回移动的文件数。
    /// 失败只记录，不打断启动。
    /// </summary>
    public static int Collect(string anchorPath, IEnumerable<ActionDto> actions)
    {
        int moved = 0;
        string? stamp = null;
        try
        {
            foreach (var action in actions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.Id)) continue;
                string filesDir = ActionStore.FilesDir(anchorPath, action.Id);
                if (!Directory.Exists(filesDir)) continue;

                List<string> images;
                try
                {
                    images = Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories)
                        .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                        .ToList();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Resource", $"扫描动作素材失败（跳过）：{action.Id}：{ex.Message}");
                    continue;
                }
                if (images.Count == 0) continue;

                if (!TryCollectRefs(action, out var referenced))
                {
                    AppLog.Info("Resource", $"动作「{action.Name}」有动态图片引用，跳过孤儿检查");
                    continue;
                }

                foreach (string file in images)
                {
                    string relative = Path.GetRelativePath(filesDir, file).Replace('\\', '/');
                    if (referenced.Contains(relative)) continue;

                    stamp ??= DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    if (MoveToTrash(anchorPath, stamp, action.Id, filesDir, file))
                    {
                        moved++;
                        AppLog.Info("Resource", $"动作「{action.Name}」的孤儿图移入 trash：{relative}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("OrphanFiles.Collect", ex);
            AppLog.Warn("Resource", $"孤儿图检查失败：{ex.Message}");
        }
        if (moved > 0) AppLog.Info("Resource", $"动作目录孤儿图回收 {moved} 个（trash 里可手工捞回）");
        return moved;
    }

    /// <summary>
    /// 收集动作体里引用的 <c>files/</c> 相对路径。返回 false = 引用算不准（没有动作体 / 动态引用），
    /// 调用方应跳过这个动作。
    /// </summary>
    private static bool TryCollectRefs(ActionDto action, out HashSet<string> referenced)
    {
        referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var program = action.Program;
        if (program == null) return false;

        // 变量默认值也可能直接写死一张模板图（步骤里用 {"var": "img"} 传）。
        foreach (var variable in program.Variables)
            if (!TryAddVariableDefault(variable, referenced)) return false;

        foreach (var step in ProgramWalker.AllSteps(program))
        {
            if (!step.In.TryGetValue("image", out var arg)) continue;
            if (arg.Kind != StepArgKind.literal) return false;   // 变量 / 内联取值：算不准
            string text = arg.Literal.AsText();
            if (text.Contains("${", StringComparison.Ordinal)) return false;
            if (!TryAddPath(text, referenced)) return false;     // 含 .. 之类不可信的路径
        }
        return true;
    }

    private static bool TryAddVariableDefault(VariableDef variable, HashSet<string> referenced)
    {
        if (variable.Type != ValueKind.text) return true;
        string text = variable.Default?.AsText() ?? "";
        if (text.Length == 0) return true;
        if (text.Contains("${", StringComparison.Ordinal)) return false;
        return TryAddPath(text, referenced);
    }

    /// <summary>把一个图片参数值归一成 <c>files/</c> 下的相对路径；返回 false = 无法安全判定。</summary>
    private static bool TryAddPath(string raw, HashSet<string> referenced)
    {
        string p = raw.Trim().Replace('\\', '/');
        if (p.Length == 0) return true;
        if (Path.IsPathRooted(p) || p.Contains(':')) return true;   // 绝对路径 / 盘符：外部资源，不归这里管

        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        if (p.StartsWith("files/", StringComparison.OrdinalIgnoreCase)) p = p["files/".Length..];
        if (p.Length == 0) return true;
        if (p.Split('/').Any(seg => seg == "..")) return false;

        referenced.Add(p);
        return true;
    }

    /// <summary>把文件移进 <c>trash/&lt;时间戳&gt;/orphans/&lt;动作&gt;/</c>；重名加序号。</summary>
    private static bool MoveToTrash(string anchorPath, string stamp, string actionId,
        string filesDir, string file)
    {
        try
        {
            string relative = Path.GetRelativePath(filesDir, file);
            string trashRoot = Path.Combine(ActionStore.TrashDir(anchorPath), stamp, "orphans", actionId);
            string target = Path.Combine(trashRoot, relative);
            for (int n = 2; File.Exists(target); n++)
                target = Path.Combine(trashRoot, Path.GetFileNameWithoutExtension(relative) + $"-{n}" + Path.GetExtension(relative));

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("OrphanFiles.MoveToTrash", ex);
            AppLog.Warn("Resource", $"孤儿图移入 trash 失败（保留原文件）：{file}：{ex.Message}");
            return false;
        }
    }
}
