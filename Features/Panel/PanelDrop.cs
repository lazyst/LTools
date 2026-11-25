using System;
using System.Collections.Generic;
using System.IO;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Panel;

/// <summary>
/// 把「文件 / 文件夹」变成面板里的动作（拖放、<c>Ctrl+V</c>、右键「从文件创建动作…」共用一份）。
/// </summary>
/// <remarks>
/// <para>
/// 一个路径 = 一个动作，**1 步**：文件夹走「在资源管理器中定位 / 打开文件夹」（<c>file.explorer</c>，
/// <c>operation=open</c>），文件走「用默认程序打开文件」（<c>file.openFile</c>，工作目录 = 文件所在目录）。
/// 文件夹排前面，其余按用户给的顺序。
/// </para>
/// <para>
/// <see cref="Build"/> 只做纯逻辑（名称 / 图标 / 步骤 / 去重 / 跳过），不碰面板与磁盘写入；
/// 落位与落盘在 <see cref="Insert"/>。两段分开是为了 <c>--selftest</c> 能直接断言。
/// </para>
/// </remarks>
internal static class PanelDrop
{
    /// <summary>一次「添加」的结果：建出来的动作、跳过的路径（找不到）、已经有动作的路径。</summary>
    public sealed class Result
    {
        public List<ActionDto> Actions { get; } = new();

        public List<string> Missing { get; } = new();

        public List<string> Duplicates { get; } = new();

        public bool Empty => Actions.Count == 0;
    }

    /// <summary>
    /// 路径 → 动作。
    /// </summary>
    /// <param name="known">这条（绝对）路径是不是已经有动作了；为空 = 不去重。</param>
    /// <param name="probe">路径探测（测试注入用），返回「文件或目录存在」「是不是目录」；为空 = 真去问文件系统。</param>
    public static Result Build(IReadOnlyList<string> paths, Func<string, bool>? known = null,
        Func<string, (bool Exists, bool IsDirectory)>? probe = null)
    {
        known ??= _ => false;
        probe ??= DefaultProbe;

        static (bool Exists, bool IsDirectory) DefaultProbe(string path)
        {
            // 目录必须也算「存在」：只写 File.Exists 会把文件夹当找不到（真实路径才暴露，自测用假探测时踩不到）。
            bool isDirectory = Directory.Exists(path);
            return (isDirectory || File.Exists(path), isDirectory);
        }

        var folders = new List<ActionDto>();
        var files = new List<ActionDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Result();

        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // 资源管理器给的路径偶尔带引号（复制粘贴来的更常见）。
            string text = raw.Trim().Trim('"');
            if (text.Length == 0) continue;

            string full;
            try { full = Normalize(text); }
            catch
            {
                result.Missing.Add(text);
                continue;
            }

            if (!seen.Add(full)) continue;                  // 同一批里重复的只留一个
            (bool exists, bool isDirectory) = probe(full);
            if (!exists)
            {
                result.Missing.Add(full);
                continue;
            }

            if (known(full))
            {
                result.Duplicates.Add(full);
                continue;
            }

            ActionDto action = isDirectory ? FolderAction(full) : FileAction(full);
            (isDirectory ? folders : files).Add(action);
        }

        result.Actions.AddRange(folders);
        result.Actions.AddRange(files);
        return result;
    }

    /// <summary>
    /// 面板里已经有动作的路径（用来去重）：扫所有动作的 <c>file.openFile</c> / <c>file.explorer</c>，
    /// 只认**字面量**路径；带变量 / 内联取值的算不出来，当没有。
    /// </summary>
    public static HashSet<string> ExistingPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ActionDto action in ActionRegistry.All)
        {
            foreach (Step step in ProgramWalker.AllSteps(action.Program))
            {
                if (!IsOpenStep(step)) continue;
                if (!step.In.TryGetValue("path", out StepArg? arg)) continue;
                if (arg.Kind != StepArgKind.literal) continue;

                string text = arg.Literal.AsText().Trim();
                if (text.Length == 0 || text.Contains("${", StringComparison.Ordinal)) continue;

                try { set.Add(Normalize(text)); }
                catch { /* 写坏的路径跳过，不影响其它动作 */ }
            }
        }

        return set;
    }

    /// <summary>把动作落进分组（<paramref name="index"/> 越界按末尾算），返回真正加进去的条数。</summary>
    public static int Insert(IReadOnlyList<ActionDto> actions, PanelGroupDto group, int index)
    {
        if (actions.Count == 0) return 0;

        int at = Math.Clamp(index, 0, group.Actions.Count);
        int added = 0;

        foreach (ActionDto action in actions)
        {
            // 动作的写入归执行器（宿主里经链路转过去，Id 由它补）。
            if (!ActionWrites.Add(action, out _)) continue;

            // 动作体里的 Id / 名称 / 图标跟外层对齐（落盘时读的是这一份）。
            if (action.Program is { } program)
            {
                program.Id = action.Id;
                program.Name = action.Name;
                program.Icon = action.Icon ?? IconCatalog.Default;
            }

            group.Actions.Insert(at + added, action.Id);
            added++;
        }

        if (added == 0) return 0;

        PanelLibrary.Save();
        return added;
    }

    /// <summary>路径的规范形式（去重、比较用）：绝对路径 + 盘符大写 + 去掉末尾分隔符（盘符根保留）。</summary>
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path.Trim().Trim('"'));

        // 盘符统一成大写：同一个路径写成 c:\ 和 C:\ 在面板里应当只算一条。
        if (full.Length >= 2 && full[1] == ':' && char.IsLetter(full[0])) full = char.ToUpperInvariant(full[0]) + full[1..];

        string root = Path.GetPathRoot(full) ?? "";
        if (full.Length > root.Length) full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full;
    }

    private static bool IsOpenStep(Step step) =>
        string.Equals(step.Unit, "file.openFile", StringComparison.OrdinalIgnoreCase)
        || string.Equals(step.Unit, "file.explorer", StringComparison.OrdinalIgnoreCase);

    private static ActionDto FolderAction(string full)
    {
        string name = FolderName(full);

        var program = new ActionProgram
        {
            Name = name,
            Icon = "folder",
            Description = full,
        };
        program.Steps.Add(new Step
        {
            Unit = "file.explorer",
            In =
            {
                ["operation"] = StepArg.FromText("open"),
                ["path"] = StepArg.FromText(full),
            },
        });

        return new ActionDto
        {
            Name = name,
            Icon = "folder",
            Program = program,
        };
    }

    private static ActionDto FileAction(string full)
    {
        string name = FileName(full);
        string icon = IconFor(Path.GetExtension(full));
        string? workdir = Path.GetDirectoryName(full);

        var program = new ActionProgram
        {
            Name = name,
            Icon = icon,
            Description = full,
        };
        var step = new Step
        {
            Unit = "file.openFile",
            In = { ["path"] = StepArg.FromText(full) },
        };
        if (!string.IsNullOrEmpty(workdir)) step.In["workdir"] = StepArg.FromText(workdir);
        program.Steps.Add(step);

        return new ActionDto
        {
            Name = name,
            Icon = icon,
            Program = program,
        };
    }

    private static string FolderName(string full)
    {
        try
        {
            string name = new DirectoryInfo(full).Name;
            return name.Length > 0 ? name : full;
        }
        catch
        {
            return full;
        }
    }

    /// <summary>文件名去扩展名；<c>.gitignore</c> 这种整名就是扩展名的原样保留。</summary>
    private static string FileName(string full)
    {
        string file = Path.GetFileName(full);
        string name = Path.GetFileNameWithoutExtension(full);
        if (name.Length == 0) name = file;
        return name.Length > 0 ? name : full;
    }

    /// <summary>按扩展名挑一个现成的内置图标（只从 <see cref="IconCatalog"/> 里已有的 id 里选）。</summary>
    private static string IconFor(string extension)
    {
        if (extension.Length == 0) return "file";

        return extension.ToLowerInvariant() switch
        {
            ".exe" or ".com" or ".msi" or ".appx" or ".msix" => "app",
            ".bat" or ".cmd" or ".ps1" or ".ahk" or ".py" or ".js" or ".ts" or ".cs" or ".vbs" => "script",
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff" or ".ico"
                or ".svg" or ".heic" => "image",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" => "video",
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" => "music",
            ".md" or ".txt" or ".log" or ".rtf" => "note",
            ".lnk" or ".url" => "link",
            ".html" or ".htm" => "globe",
            ".json" or ".xml" or ".yml" or ".yaml" or ".ini" or ".toml" or ".csv" => "file",
            _ => "file",
        };
    }
}
