using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 速记数据层（深模块）：隔离所有文件 IO。每条速记 = <c>速记/&lt;分类&gt;/&lt;yyyy-MM-dd&gt; &lt;标题&gt;.txt</c>；
/// 标题存于文件名、正文存于文件内容、分类=一级子目录名。UI/协调器不直接碰文件。
/// </summary>
internal sealed class NoteRepository
{
    private readonly string _root;

    /// <summary>未指定分类时的兜底目录名。</summary>
    internal const string Unclassified = "未分类";

    /// <summary>“全部分类”占位（UI 下拉用，非真实目录）。</summary>
    internal const string AllCategories = "全部";

    public NoteRepository(string root) => _root = root;

    /// <summary>枚举分类（子目录名），保证“未分类”在列。</summary>
    public IReadOnlyList<string> Categories()
    {
        var list = new List<string>();
        try
        {
            if (Directory.Exists(_root))
                foreach (var d in Directory.EnumerateDirectories(_root))
                {
                    string name = System.IO.Path.GetFileName(d.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                    if (name.StartsWith(".")) continue; // 排除备份等点前缀目录
                    list.Add(name);
                }
        }
        catch { /* 静默，UI 兜底 */ }
        if (!list.Contains(Unclassified)) list.Insert(0, Unclassified);
        return list;
    }

    /// <summary>确保分类子目录存在。</summary>
    public void EnsureCategory(string name)
    {
        string clean = CleanCategoryName(name);
        if (string.IsNullOrEmpty(clean)) return;
        string dir = System.IO.Path.Combine(_root, clean);
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { CrashLog.Write("创建速记分类目录失败: " + clean, ex); }
        }
    }

    /// <summary>重命名分类（目录改名）。目标已存在或同名抛异常。</summary>
    public void RenameCategory(string oldName, string newName)
    {
        string oldClean = CleanCategoryName(oldName);
        string newClean = CleanCategoryName(newName);
        if (string.IsNullOrEmpty(oldClean) || string.IsNullOrEmpty(newClean))
            throw new ArgumentException("分类名无效");
        string oldDir = System.IO.Path.Combine(_root, oldClean);
        string newDir = System.IO.Path.Combine(_root, newClean);
        if (!Directory.Exists(oldDir)) throw new InvalidOperationException("分类「" + oldClean + "」不存在");
        if (string.Equals(oldClean, newClean, StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(newDir)) throw new InvalidOperationException("分类「" + newClean + "」已存在");
        Directory.Move(oldDir, newDir);
    }

    /// <summary>统计分类下 .txt 数。</summary>
    public int CountNotes(string name)
    {
        string clean = CleanCategoryName(name);
        string dir = System.IO.Path.Combine(_root, clean);
        if (!Directory.Exists(dir)) return 0;
        try { return Directory.GetFiles(dir, "*.txt").Length; }
        catch { return 0; }
    }

    /// <summary>删除分类及其全部笔记。返回删除的 .txt 数。</summary>
    public int DeleteCategory(string name)
    {
        string clean = CleanCategoryName(name);
        string dir = System.IO.Path.Combine(_root, clean);
        if (!Directory.Exists(dir)) return 0;
        int count = CountNotes(clean);
        Directory.Delete(dir, recursive: true);
        return count;
    }

    /// <summary>枚举条目。category 为 null/空/“全部”时跨所有分类；filter 非空时按标题/正文子串过滤。按修改时间倒序。
    /// 列表只需标题+mtime，正文不预读；过滤时仅对标题不匹配的条目懒读正文做子串匹配（保留正文搜索能力）。</summary>
    public IReadOnlyList<NoteEntry> List(string? category, string? filter)
    {
        var result = new List<NoteEntry>();
        bool all = string.IsNullOrEmpty(category) || category == AllCategories;
        var cats = all ? Categories() : new[] { CleanCategoryName(category!) };
        foreach (var cat in cats)
        {
            if (string.IsNullOrEmpty(cat)) continue;
            string dir = System.IO.Path.Combine(_root, cat);
            if (!Directory.Exists(dir)) continue;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.txt"); }
            catch { continue; }
            foreach (var f in files)
                result.Add(BuildEntryLight(f, cat));
        }

        string? flt = string.IsNullOrWhiteSpace(filter) ? null : filter!.Trim();
        if (flt != null)
        {
            // 标题先匹配短路；仅标题不命中的条目才读正文做子串匹配，减少无谓 IO
            var filtered = new List<NoteEntry>(result.Count);
            foreach (var e in result)
            {
                if (e.Title != null && e.Title.Contains(flt)) { filtered.Add(e); continue; }
                string? body = TryReadBody(e.Path);
                if (body != null && body.Contains(flt)) { e.Body = body; filtered.Add(e); }
            }
            result = filtered;
        }

        result.Sort((a, b) => b.Mtime.CompareTo(a.Mtime));
        return result;
    }

    /// <summary>从路径载入单条（标题/分类解析自路径，正文读文件）。</summary>
    public NoteEntry? Load(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        string? dir = System.IO.Path.GetDirectoryName(path);
        string cat = (dir != null && System.IO.Path.GetDirectoryName(dir) == _root.TrimEnd(System.IO.Path.DirectorySeparatorChar))
            ? System.IO.Path.GetFileName(dir!) : Unclassified;
        return BuildEntry(path, cat);
    }

    /// <summary>保存。新建或路径变更（标题/分类变）则写新文件并删旧；仅改正文则原地覆盖。返回最终路径。</summary>
    public string Save(NoteEntry entry, string? oldPath)
    {
        string category = string.IsNullOrEmpty(entry.Category) ? Unclassified : CleanCategoryName(entry.Category);
        EnsureCategory(category);
        string title = entry.Title ?? "";
        // 日期前缀=创建日：旧笔记从旧文件名解析（保留原日期），新笔记取当前
        DateTime creation = ParseCreationDateFromPath(oldPath) ?? DateTime.Now;
        string newFileName = BuildFileName(title, creation);
        string newDir = System.IO.Path.Combine(_root, category);
        string newPath = System.IO.Path.Combine(newDir, newFileName);
        newPath = AvoidCollision(newPath, oldPath);

        try
        {
            File.WriteAllText(newPath, entry.Body ?? "", new UTF8Encoding(false));
            File.SetLastWriteTime(newPath, entry.Mtime);
        }
        catch (Exception ex)
        {
            CrashLog.Write("速记保存失败: " + newPath, ex);
            throw;
        }

        if (!string.IsNullOrEmpty(oldPath)
            && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(oldPath))
        {
            try { File.Delete(oldPath); }
            catch (Exception ex) { CrashLog.Write("速记删旧文件失败: " + oldPath, ex); }
        }

        entry.Path = newPath;
        return newPath;
    }

    /// <summary>删除单条。</summary>
    public void Delete(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { File.Delete(path); }
        catch (Exception ex) { CrashLog.Write("速记删除失败: " + path, ex); }
    }

    // —— 一次性迁移：速记/ 根目录散落旧 .txt → 未分类/<新文件名>（只留正文）——
    public void MigrateRootScatteredNotes()
    {
        if (!Directory.Exists(_root)) return;
        string[] files;
        try { files = Directory.GetFiles(_root, "*.txt"); }
        catch { return; }
        if (files.Length == 0) return;

        EnsureCategory(Unclassified);
        string unclassifiedDir = System.IO.Path.Combine(_root, Unclassified);
        string backupDir = System.IO.Path.Combine(_root, ".迁移备份");
        foreach (var f in files)
        {
            try
            {
                string content;
                try { content = File.ReadAllText(f, new UTF8Encoding(false)); }
                catch { content = File.ReadAllText(f); }

                var (date, title, body) = ParseLegacyNote(content);
                string fileName = string.IsNullOrWhiteSpace(title)
                    ? date.ToString("yyyy-MM-dd_HHmmss") + ".txt"
                    : date.ToString("yyyy-MM-dd") + " " + CleanFileNameFromTitle(title) + ".txt";
                if (fileName.Length > 80) fileName = fileName.Substring(0, 77) + ".txt";

                string newPath = AvoidCollision(System.IO.Path.Combine(unclassifiedDir, fileName), null);
                File.WriteAllText(newPath, body, new UTF8Encoding(false));
                File.SetLastWriteTime(newPath, date);

                // 原文件移入备份（验证后可手删 .迁移备份/），不硬删以防解析边界丢数据
                if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);
                string backupPath = System.IO.Path.Combine(backupDir, System.IO.Path.GetFileName(f));
                backupPath = AvoidCollision(backupPath, null);
                File.Move(f, backupPath);
            }
            catch (Exception ex) { CrashLog.Write("速记迁移失败: " + f, ex); }
        }
    }

    // —— 内部工具 ——

    private NoteEntry BuildEntry(string path, string cat)
    {
        var e = BuildEntryLight(path, cat);
        e.Body = TryReadBody(path) ?? "";
        return e;
    }

    /// <summary>轻量构造（不读正文）：列表只需标题+mtime，正文延迟到 <see cref="Load"/> 或过滤时的懒读。</summary>
    private static NoteEntry BuildEntryLight(string path, string cat)
    {
        return new NoteEntry
        {
            Path = path,
            Category = cat,
            Mtime = File.GetLastWriteTime(path),
            Title = ParseTitleFromFileName(System.IO.Path.GetFileName(path)),
            Body = "",
        };
    }

    /// <summary>读正文（UTF-8 优先，降级默认编码）。失败返回 null。</summary>
    private static string? TryReadBody(string path)
    {
        try { return File.ReadAllText(path, new UTF8Encoding(false)); }
        catch { try { return File.ReadAllText(path); } catch { return null; } }
    }

    /// <summary>文件名 → 标题：剥离 <c>yyyy-MM-dd</c> 日期前缀。无标题（_HHmmss 形式）返回空。</summary>
    internal static string ParseTitleFromFileName(string fileName)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var m = Regex.Match(name, @"^(\d{4}-\d{2}-\d{2})(?:_(\d{6}))?\s*(.*)$");
        if (!m.Success) return name.Trim();
        return m.Groups[3].Value.Trim();
    }

    /// <summary>从旧文件名提取创建日期（yyyy-MM-dd 或 yyyy-MM-dd_HHmmss）。无法解析返回 null。</summary>
    private static DateTime? ParseCreationDateFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var m = Regex.Match(System.IO.Path.GetFileNameWithoutExtension(path),
            @"^(\d{4})-(\d{2})-(\d{2})(?:_(\d{2})(\d{2})(\d{2}))?");
        if (!m.Success) return null;
        int y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int mo = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int d = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        int h = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
        int mi = m.Groups[5].Success ? int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) : 0;
        int s = m.Groups[6].Success ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
        try { return new DateTime(y, mo, d, h, mi, s); } catch { return null; }
    }

    /// <summary>标题 → 文件名：<c>yyyy-MM-dd &lt;清理后标题&gt;.txt</c>；标题空则 <c>yyyy-MM-dd_HHmmss.txt</c>。</summary>
    private static string BuildFileName(string title, DateTime dt)
    {
        if (string.IsNullOrWhiteSpace(title))
            return dt.ToString("yyyy-MM-dd_HHmmss") + ".txt";
        string clean = CleanFileNameFromTitle(title);
        if (clean.Length > 60) clean = clean.Substring(0, 60);
        return dt.ToString("yyyy-MM-dd") + " " + clean + ".txt";
    }

    private static string AvoidCollision(string path, string? oldPath)
    {
        if (string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return path;
        string dir = System.IO.Path.GetDirectoryName(path)!;
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = System.IO.Path.Combine(dir, name + " (" + i + ").txt");
            if (!File.Exists(candidate)
                || string.Equals(candidate, oldPath, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return path;
    }

    /// <summary>标题非法字符 → 中文词（沿用原 CleanFileNameFromTitle，文件名保持可读）。</summary>
    internal static string CleanFileNameFromTitle(string title)
    {
        title = title.Replace("\\", "「反斜杠」");
        title = title.Replace("/", "「斜杠」");
        title = title.Replace(":", "「冒号」");
        title = title.Replace("*", "「星号」");
        title = title.Replace("?", "「问号」");
        title = title.Replace("\"", "「引号」");
        title = title.Replace("<", "「小于」");
        title = title.Replace(">", "「大于」");
        title = title.Replace("|", "「竖线」");
        return title.Trim();
    }

    private static string CleanCategoryName(string name)
    {
        foreach (char c in System.IO.Path.GetInvalidPathChars()) name = name.Replace(c.ToString(), "");
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            if (c != System.IO.Path.DirectorySeparatorChar && c != System.IO.Path.AltDirectorySeparatorChar)
                name = name.Replace(c.ToString(), "");
        return name.Trim();
    }

    /// <summary>解析旧追加式 .txt：首行 [yyyy-MM-dd HH:mm:ss]、紧接 ## 标题（可选）、其余正文。返回（日期, 标题, 正文）。</summary>
    internal static (DateTime date, string title, string body) ParseLegacyNote(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        DateTime date = DateTime.Now;
        int idx = 0;

        for (; idx < lines.Length; idx++)
        {
            string t = lines[idx].Trim();
            if (string.IsNullOrEmpty(t)) continue;
            var m = Regex.Match(t, @"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]$");
            if (m.Success)
            {
                DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
                idx++;
            }
            break;
        }

        string title = "";
        for (; idx < lines.Length; idx++)
        {
            string line = lines[idx];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var m = Regex.Match(line, @"^##\s+(.+)$");
            if (m.Success) { title = m.Groups[1].Value.Trim(); idx++; }
            break;
        }

        var bodyLines = new List<string>();
        for (; idx < lines.Length; idx++) bodyLines.Add(lines[idx]);
        while (bodyLines.Count > 0 && string.IsNullOrWhiteSpace(bodyLines[^1])) bodyLines.RemoveAt(bodyLines.Count - 1);
        while (bodyLines.Count > 0 && string.IsNullOrWhiteSpace(bodyLines[0])) bodyLines.RemoveAt(0);
        return (date, title, string.Join("\n", bodyLines));
    }
}
