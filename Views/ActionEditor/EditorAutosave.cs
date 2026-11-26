using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 动作编辑器的自动存盘（P3）：编辑中每 10 秒把**没保存的内容**写进
/// <c>cache/editor/&lt;动作id&gt;.autosave.json</c>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么放 <c>cache/</c>：它只是「刚才那个进程崩了」的应急副本，不是用户资产——
/// <c>cache/</c> 不进备份、下次运行还可以整体清掉，所以这里既不需要引用回收，也不用担心备份里
/// 混进半成品。真正的落盘仍然只有 <c>actions/&lt;id&gt;/action.json</c>（或 <c>subactions/&lt;id&gt;.json</c>）。
/// </para>
/// <para>
/// 三条路径：<see cref="Write"/>（10 秒一次）→ 打开动作时 <see cref="IsNewerThan"/> 判断
/// 「比正式文件新才算数」→ 恢复 / 放弃后 <see cref="Delete"/>。正式文件比它新（别处改过、
/// 或者本来就保存过）时把这份自动存盘当过期副本直接删掉，不打扰用户。
/// </para>
/// <para>
/// 写文件走 <c>.tmp</c> 再替换：写到一半崩掉不会留下半个 JSON 让下次「恢复」直接报错。
/// </para>
/// </remarks>
internal static class EditorAutosave
{
    /// <summary>自动存盘的文件名后缀（前面是动作 / 子动作的 Id）。</summary>
    public const string FileSuffix = ".autosave.json";

    private const string DirName = "editor";

    /// <summary>自动存盘目录；<paramref name="root"/> 只给自测传临时目录用。</summary>
    public static string DirFor(string? root = null) =>
        Path.Combine(root ?? AppPaths.DataRoot, "cache", DirName);

    /// <summary>某个动作（或子动作）的自动存盘文件。</summary>
    public static string FileFor(string id, string? root = null) =>
        Path.Combine(DirFor(root), id + FileSuffix);

    /// <summary>写一份；Id 为空 / 写不进去都只是少一层保险，不抛。</summary>
    public static bool Write(string id, ActionProgram program, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(id) || program == null) return false;

        string path = FileFor(id, root);
        try
        {
            AtomicFile.WriteJson(path, program, ConfigBundle.Options);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Editor", $"自动存盘失败（{id}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>读一份；文件不存在 / 内容坏掉都返回 null（坏文件顺手删掉）。</summary>
    public static ActionProgram? TryRead(string id, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        string path = FileFor(id, root);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<ActionProgram>(File.ReadAllText(path), ConfigBundle.Options);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Editor", $"自动存盘读不出来（{id}）：{ex.Message}");
            Delete(id, root);
            return null;
        }
    }

    /// <summary>
    /// 这份自动存盘是不是比正式文件新（= 值得问一句「恢复吗」）。
    /// 正式文件不存在时算新；自动存盘不存在时算旧。
    /// </summary>
    public static bool IsNewerThan(string id, string sourceFile, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        string path = FileFor(id, root);
        if (!File.Exists(path)) return false;
        if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile)) return true;

        try { return File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(sourceFile); }
        catch { return false; }
    }

    /// <summary>删掉自动存盘（保存 / 放弃 / 过期都走这里，删不掉只记一笔）。</summary>
    public static void Delete(string id, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        try
        {
            string path = FileFor(id, root);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Editor", $"自动存盘删不掉（{id}）：{ex.Message}");
        }
    }

    /// <summary>「它在什么时候存的」（恢复提示里用）；文件不在就返回空串。</summary>
    public static string TimeText(string id, string? root = null)
    {
        try
        {
            string path = FileFor(id, root);
            return File.Exists(path)
                ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "";
        }
        catch { return ""; }
    }
}
