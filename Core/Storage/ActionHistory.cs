using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LTools.Config;
using LTools.Features;

namespace LTools.Core.Storage;

/// <summary>一个历史版本：动作保存前的样子（只存 <c>action.json</c>，不复制 <c>files/</c>）。</summary>
internal sealed record ActionHistoryEntry(
    string File,
    DateTime SavedAt,
    string Name,
    int StepCount);

/// <summary>
/// 动作保存前的自动快照（<c>actions/&lt;id&gt;/history/&lt;时间戳&gt;-&lt;内容哈希&gt;.json</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 只有一个写入点：<see cref="ActionStore.WriteIfChanged"/> 发现<b>内容真的变了</b>时，
/// 先把<b>旧的那份</b>存进 history。所以「保存两次没改动」不会刷出重复快照，
/// 编辑器 / AI 采纳 / 宏挂载 / 导入备份全都自动覆盖，不需要每处各写一遍。
/// </para>
/// <para>
/// 文件名里的哈希用来去重：同样的旧内容不会存第二份（改回去再改回来也不会刷屏）。
/// 只留最近 <see cref="Keep"/> 份，按时间戳倒序裁掉更老的。
/// </para>
/// <para>
/// <b>不复制 <c>files/</c></b>：模板图按内容去重、可能被硬链接共享，逐份复制代价大且会把
/// 去重打散。所以历史版本还原的是「步骤 + 变量」，图片以动作目录里当前的为准。
/// </para>
/// </remarks>
internal static class ActionHistory
{
    public const int Keep = 10;

    public static string DirOf(string actionDir) => Path.Combine(actionDir, "history");

    /// <summary>历史版本列表（新的在前）。读不出来的文件直接跳过，不影响别的条目。</summary>
    public static List<ActionHistoryEntry> List(string anchorPath, string id)
    {
        var entries = new List<ActionHistoryEntry>();
        string dir = DirOf(ActionStore.ActionDir(anchorPath, id));
        if (!Directory.Exists(dir)) return entries;

        foreach (string file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var action = ReadFile(file);
                if (action == null) continue;
                entries.Add(new ActionHistoryEntry(
                    Path.GetFileName(file),
                    File.GetLastWriteTime(file),
                    string.IsNullOrWhiteSpace(action.Name) ? action.Id : action.Name,
                    action.Program?.Steps.Sum(s => s.SelfAndDescendants().Count()) ?? 0));
            }
            catch (Exception ex)
            {
                AppLog.Warn("ActionHistory", $"历史版本读不出来，已跳过：{Path.GetFileName(file)}（{ex.Message}）");
            }
        }

        entries.Sort((a, b) => string.CompareOrdinal(b.File, a.File));
        return entries;
    }

    /// <summary>读某个历史版本（还原用）。文件没了 / 解析失败返回 null。</summary>
    public static ActionDto? Read(string anchorPath, string id, string file)
    {
        string path = Path.Combine(DirOf(ActionStore.ActionDir(anchorPath, id)), file);
        return File.Exists(path) ? ReadFile(path) : null;
    }

    /// <summary>
    /// 把「即将被覆盖的旧内容」存一份。由 <see cref="ActionStore.WriteIfChanged"/> 在
    /// 确认内容变化之后调用，别在别处另开一份快照逻辑。
    /// </summary>
    internal static void CaptureBeforeChange(string actionPath, string previousJson)
    {
        if (string.IsNullOrWhiteSpace(previousJson)) return;

        try
        {
            string dir = DirOf(Path.GetDirectoryName(actionPath) ?? "");
            Directory.CreateDirectory(dir);

            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previousJson)))
                [..8].ToLowerInvariant();

            // 最新一份就是同样内容 → 不重复存（改回去再改回来不会刷出两条一样的）。
            string newest = Directory.GetFiles(dir, "*.json")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? "";
            if (newest.Length > 0 && newest.EndsWith(hash + ".json", StringComparison.OrdinalIgnoreCase)) return;

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string target = Path.Combine(dir, $"{stamp}-{hash}.json");
            for (int i = 2; File.Exists(target); i++)
                target = Path.Combine(dir, $"{stamp}-{i}-{hash}.json");

            File.WriteAllText(target, previousJson, new UTF8Encoding(false));
            Prune(dir);
        }
        catch (Exception ex)
        {
            // 快照失败绝不能挡住保存本身。
            AppLog.Warn("ActionHistory", $"存历史版本失败（不影响保存）：{ex.Message}");
        }
    }

    private static void Prune(string dir)
    {
        foreach (string old in Directory.GetFiles(dir, "*.json")
                     .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                     .Skip(Keep))
        {
            try { File.Delete(old); }
            catch { /* 被占用就留着，下次再清 */ }
        }
    }

    private static ActionDto? ReadFile(string path)
    {
        string json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<ActionDto>(json, ConfigBundle.Options);
    }
}
