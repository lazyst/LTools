using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 速记数据层（深模块）：隔离所有文件 IO。每条速记 = <c>速记/&lt;分类&gt;/&lt;yyyy-MM-dd&gt; &lt;标题&gt;.md</c>；
/// 标题存于文件名、正文存于文件内容（Markdown 源码）、分类=一级子目录名。UI/协调器不直接碰文件。
/// </summary>
internal sealed class NoteRepository
{
    private readonly string _root;

    /// <summary>速记文件的扩展名。改这里就全链路生效（列表 / 计数 / 命名 / 迁移），别再各处写 <c>.md</c>。</summary>
    internal const string Extension = ".md";

    /// <summary>旧版速记的扩展名：只在一次性迁移里认，写完就不再产生。</summary>
    private const string LegacyExtension = ".txt";

    /// <summary>速记附件认的图片扩展名（粘贴 / 拖入 / 冗余清理共用同一份）。</summary>
    internal static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

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

    /// <summary>
    /// 列表行摘要：正文里第一行真正的内容（跳过重复标题的 <c># 标题</c> 行与围栏记号），去掉 Markdown 记号。
    /// 只读前 4 KB，读完就停——列表刷新时对每条笔记都调一次，别整篇读。
    /// </summary>
    public string Snippet(string path, int maxChars = 60)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[4096];
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) return "";

            bool skippedHeading = false;
            foreach (string raw in new string(buffer, 0, read).Replace("\r\n", "\n").Split('\n'))
            {
                string line = CleanSnippet(NoteActions.StripMarkdownPrefix(raw));
                if (line.Length == 0) continue;
                if (line.StartsWith("```", StringComparison.Ordinal)) continue;
                if (!skippedHeading && raw.TrimStart().StartsWith('#'))
                {
                    skippedHeading = true; // 第一行是标题：和文件名重复，跳过看下一行
                    continue;
                }
                return line.Length <= maxChars ? line : line[..maxChars];
            }
        }
        catch { /* 读不了就不显示摘要，不挡列表 */ }
        return "";
    }

    private static readonly Regex SnippetLink = new(@"!?\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);

    /// <summary>摘要里不要 Markdown 记号：链接留文字、去掉 <c>**</c> / 反引号 / 删除线。</summary>
    private static string CleanSnippet(string line)
    {
        line = SnippetLink.Replace(line, "$1");
        line = line.Replace("**", "").Replace("__", "").Replace("~~", "").Replace("`", "");
        return Regex.Replace(line, @"\s{2,}", " ").Trim();
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

    /// <summary>统计分类下速记数。</summary>
    public int CountNotes(string name)
    {
        string clean = CleanCategoryName(name);
        string dir = System.IO.Path.Combine(_root, clean);
        if (!Directory.Exists(dir)) return 0;
        try { return Directory.GetFiles(dir, "*" + Extension).Length; }
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
            try { files = Directory.GetFiles(dir, "*" + Extension); }
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

    /// <summary>删除单条（连带它的 <c>&lt;笔记名&gt;.files</c> 附件目录）。</summary>
    public void Delete(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { File.Delete(path); }
        catch (Exception ex) { CrashLog.Write("速记删除失败: " + path, ex); }

        try
        {
            string attachments = AttachmentsDirFor(path);
            if (Directory.Exists(attachments)) Directory.Delete(attachments, recursive: true);
        }
        catch (Exception ex) { CrashLog.Write("速记附件删除失败: " + path, ex); }
    }

    /// <summary>这条速记的附件目录：<c>速记/&lt;分类&gt;/&lt;笔记名&gt;.files</c>（和笔记同目录、同名）。</summary>
    public static string AttachmentsDirFor(string notePath)
    {
        string dir = System.IO.Path.GetDirectoryName(notePath) ?? "";
        string stem = System.IO.Path.GetFileNameWithoutExtension(notePath);
        return System.IO.Path.Combine(dir, stem + ".files");
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
                    ? date.ToString("yyyy-MM-dd_HHmmss") + Extension
                    : date.ToString("yyyy-MM-dd") + " " + CleanFileNameFromTitle(title) + Extension;
                if (fileName.Length > 80) fileName = fileName[..77] + Extension;

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

    /// <summary>
    /// 一次性迁移：把旧版 <c>.txt</c> 速记改名为 <c>.md</c>（内容一个字都不动）。
    /// 返回没能改名的条数——文件被别的程序占用时改名会失败，下次启动 / 下次打开速记窗口再重试。
    /// </summary>
    /// <remarks>不做「.txt / .md 双读」：那会永久多一条读路径，也更难说清「为什么这条显示两次」。</remarks>
    public int MigrateTxtToMd()
    {
        if (!Directory.Exists(_root)) return 0;
        int failed = 0;
        foreach (string dir in NoteDirectories())
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*" + LegacyExtension); }
            catch { continue; }

            foreach (string file in files)
            {
                try
                {
                    string target = System.IO.Path.ChangeExtension(file, Extension);
                    File.Move(file, AvoidCollision(target, null));
                }
                catch (Exception ex)
                {
                    failed++;
                    CrashLog.Write("速记迁移 .txt → .md 失败: " + file, ex);
                }
            }
        }
        return failed;
    }

    /// <summary>清掉太久没保存的粘贴草稿附件夹（速记根下的 <c>.draft-*.files</c>）。返回删掉几个。</summary>
    public int CleanDraftAttachments(TimeSpan maxAge)
    {
        if (!Directory.Exists(_root)) return 0;
        int removed = 0;
        try
        {
            foreach (string dir in Directory.GetDirectories(_root, ".draft-*.files"))
            {
                try
                {
                    if (DateTime.Now - Directory.GetLastWriteTime(dir) <= maxAge) continue;
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch { /* 被占用就下次再清 */ }
            }
        }
        catch { /* 目录枚举失败：不影响打开速记 */ }
        return removed;
    }

    /// <summary>
    /// 清理附件冗余（只搬进 <c>trash/&lt;时间戳&gt;/…</c>，不硬删；trash 由 <c>ActionStore.PruneTrash</c> 留 30 天）。
    /// 两类：① 没有同名速记的 <c>&lt;笔记名&gt;.files/</c>（在资源管理器里删 / 改名 / 挪走了 .md）；
    /// ② 笔记还在、但正文里已经没有任何引用的图片文件（删掉了图片那一行）。
    /// </summary>
    /// <remarks>
    /// 只认图片扩展名（和粘贴 / 拖入时一致），别的文件一概不动；草稿夹 <c>.draft-*.files</c> 不碰（另有 7 天清理）。
    /// ② 用「正文里出现没出现过这个文件名」判断，宁可漏清也不误删：出现一次就留下。
    /// </remarks>
    public (int Folders, int Files) CollectAttachmentGarbage()
    {
        if (!Directory.Exists(_root)) return (0, 0);

        string? stamp = null;
        string? trash = null;
        int folders = 0, files = 0;

        foreach (string dir in NoteDirectories())
        {
            string[] attachments;
            try { attachments = Directory.GetDirectories(dir, "*.files"); }
            catch { continue; }

            foreach (string attachmentDir in attachments)
            {
                string folderName = System.IO.Path.GetFileName(attachmentDir);
                if (folderName.StartsWith(".draft-", StringComparison.OrdinalIgnoreCase)) continue;

                string notePath = System.IO.Path.Combine(dir, folderName[..^".files".Length] + Extension);
                if (!File.Exists(notePath))
                {
                    stamp ??= DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    trash ??= TrashRoot();
                    if (MoveDirToTrash(attachmentDir, System.IO.Path.Combine(trash, stamp, "note-files")))
                    {
                        folders++;
                        AppLog.Info("Note", $"没有对应速记的附件夹移入 trash：{System.IO.Path.GetRelativePath(_root, attachmentDir)}");
                    }
                    continue;
                }

                string body;
                try { body = File.ReadAllText(notePath, new UTF8Encoding(false)); }
                catch { continue; } // 读不了正文：这一条整份跳过，别凭猜测搬文件

                string[] attachmentsFiles;
                try { attachmentsFiles = Directory.GetFiles(attachmentDir); }
                catch { continue; }

                foreach (string file in attachmentsFiles)
                {
                    if (!ImageExtensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant())) continue;
                    string name = System.IO.Path.GetFileName(file);
                    if (body.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;

                    stamp ??= DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    trash ??= TrashRoot();
                    if (MoveFileToTrash(file, System.IO.Path.Combine(trash, stamp, "note-images")))
                    {
                        files++;
                        AppLog.Info("Note", $"正文里已没引用的图片移入 trash：{System.IO.Path.GetRelativePath(_root, file)}");
                    }
                }
            }
        }

        if (folders > 0 || files > 0)
            AppLog.Info("Note", $"附件冗余清理：{folders} 个附件夹、{files} 张图移入 trash");
        return (folders, files);
    }

    /// <summary>速记相关的垃圾目录：数据根的 <c>trash/</c>（和动作目录共用同一个 trash，一起按 30 天清）。</summary>
    private string TrashRoot()
    {
        string root = System.IO.Path.GetFullPath(_root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        string? parent = System.IO.Path.GetDirectoryName(root);
        return System.IO.Path.Combine(parent ?? root, "trash");
    }

    private static bool MoveDirToTrash(string dir, string trashDir)
    {
        try
        {
            string target = System.IO.Path.Combine(trashDir, System.IO.Path.GetFileName(dir));
            for (int n = 2; Directory.Exists(target) || File.Exists(target); n++)
                target = System.IO.Path.Combine(trashDir, System.IO.Path.GetFileName(dir) + "-" + n);
            Directory.CreateDirectory(trashDir);
            Directory.Move(dir, target);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("速记附件夹移入 trash 失败: " + dir, ex);
            return false;
        }
    }

    private static bool MoveFileToTrash(string file, string trashDir)
    {
        try
        {
            string target = System.IO.Path.Combine(trashDir, System.IO.Path.GetFileName(file));
            for (int n = 2; File.Exists(target) || Directory.Exists(target); n++)
                target = System.IO.Path.Combine(trashDir,
                    System.IO.Path.GetFileNameWithoutExtension(file) + "-" + n + System.IO.Path.GetExtension(file));
            Directory.CreateDirectory(trashDir);
            File.Move(file, target);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("速记图片移入 trash 失败: " + file, ex);
            return false;
        }
    }

    /// <summary>速记根目录 + 各分类子目录（跳过 <c>.迁移备份</c> 这类点前缀目录）。</summary>
    private IReadOnlyList<string> NoteDirectories()    {
        var dirs = new List<string> { _root };
        try
        {
            foreach (string dir in Directory.GetDirectories(_root))
            {
                string name = System.IO.Path.GetFileName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                if (name.StartsWith(".")) continue;
                dirs.Add(dir);
            }
        }
        catch { /* 静默：迁移是尽力而为 */ }
        return dirs;
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

    /// <summary>标题 → 文件名：<c>yyyy-MM-dd &lt;清理后标题&gt;.md</c>；标题空则 <c>yyyy-MM-dd_HHmmss.md</c>。</summary>
    private static string BuildFileName(string title, DateTime dt)
    {
        if (string.IsNullOrWhiteSpace(title))
            return dt.ToString("yyyy-MM-dd_HHmmss") + Extension;
        string clean = CleanFileNameFromTitle(title);
        if (clean.Length > 60) clean = clean.Substring(0, 60);
        return dt.ToString("yyyy-MM-dd") + " " + clean + Extension;
    }

    private static string AvoidCollision(string path, string? oldPath)
    {
        if (string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return path;
        string dir = System.IO.Path.GetDirectoryName(path)!;
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        string ext = System.IO.Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = System.IO.Path.Combine(dir, name + " (" + i + ")" + ext);
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
