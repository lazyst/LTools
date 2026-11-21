using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features;

/// <summary>
/// 速记动作 / 取值器的薄封装：写速记（v1 的 <c>quickNote</c> 动作 / 新动作单元的 <c>data.noteWrite</c>）与读速记（<see cref="ProbeKind.note"/>）。
/// 数据层复用 <see cref="NoteRepository"/>，目录与 CapsLock+N 的速记窗口完全一致（<see cref="AppPaths.NotesDir"/>）。
/// </summary>
/// <remarks>
/// 速记属于用户资产，不参与 <see cref="ImageLibrary"/> 那种按引用回收：写进去就一直在，
/// 只有用户自己在速记窗口里删除才会消失。
/// </remarks>
internal static class NoteActions
{
    /// <summary>结果里的时间格式（与速记列表一致，便于直接显示 / 比较）。</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// 写一条速记。标题留空时取正文第一行，分类留空 = 未分类。
    /// <paramref name="notesRoot"/> 只为自测留的口子（默认数据根的 <c>速记/</c>）。
    /// </summary>
    public static VarValue Write(NoteWriteRequest request, string? notesRoot = null)
    {
        string body = request.Body ?? "";
        if (string.IsNullOrWhiteSpace(body))
            throw new FlowException("写速记缺少正文");

        string root = string.IsNullOrWhiteSpace(notesRoot) ? AppPaths.NotesDir : notesRoot!;
        var repo = new NoteRepository(root);

        // 追加模式先找目标（给了路径就用它，否则取同分类最近修改的一条；都没有 = 照新建处理）
        NoteEntry? target = request.Append ? ResolveAppendTarget(repo, request, root) : null;

        // 追加时标题一律沿用目标（哪怕为空）：带标题重算文件名会把「无标题的旧笔记」改名
        string title = target != null ? target.Title : (request.Title ?? "").Trim();
        if (target == null && title.Length == 0) title = FirstLine(body);
        string category = target?.Category ?? (request.Category ?? "").Trim();

        DateTime now = DateTime.Now;
        string text = target == null ? body : ComposeAppend(target.Body, body, request.Stamp, now);

        var entry = new NoteEntry
        {
            Title = title,
            Category = category,
            Body = text,
            Mtime = now,
        };

        string path;
        if (target == null)
        {
            path = repo.Save(entry, null);
        }
        else
        {
            repo.WriteExisting(target.Path, text, now);
            path = target.Path;
        }

        // 图片：复制进 <笔记名>.files/，把正文里的原路径换成相对链接（没出现的就补在末尾）
        if (request.Images is { Count: > 0 })
        {
            string[] sources = request.Images
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToArray();
            if (sources.Length > 0)
            {
                var links = repo.ImportAttachments(path, sources);
                string embedded = EmbedImages(text, sources, links);
                if (!string.Equals(embedded, text, StringComparison.Ordinal))
                {
                    entry.Body = embedded;
                    if (target == null) path = repo.Save(entry, path); // 同一条：路径不变
                    else repo.WriteExisting(path, embedded, now);
                }
            }
        }

        string actualTitle = repo.Load(path)?.Title ?? title;

        return new VarValue
        {
            Text = path,
            File = path,
            Title = actualTitle,
            Time = entry.Mtime.ToString(TimeFormat, CultureInfo.InvariantCulture),
            Flag = true,
        };
    }

    /// <summary>
    /// 读速记：按分类 + 关键字（标题或正文包含，大小写不敏感）取最近修改的一条。
    /// 没有匹配不是错误——<c>found = false</c>、<c>count = 0</c>。
    /// </summary>
    public static VarValue Read(Probe probe, RunContext ctx)
    {
        string category = Template.Render(probe.NoteCategory ?? "", ctx).Trim();
        string keyword = Template.Render(probe.Text ?? "", ctx).Trim();
        return ReadCore(category, keyword, AppPaths.NotesDir);
    }

    /// <summary>
    /// <see cref="Read"/> 的实现（模板已渲染好）；<paramref name="notesRoot"/> 只为自测留口子。
    /// 顺带带上列表里那一行摘要（<see cref="NoteRepository.Snippet"/>）——正文可能很长，
    /// 「最近记了啥」只要一行时不必让调用方自己截。
    /// </summary>
    internal static VarValue ReadCore(string category, string keyword, string notesRoot)
    {
        var repo = new NoteRepository(notesRoot);
        var matches = repo.List(category.Length == 0 ? null : category,
                                keyword.Length == 0 ? null : keyword);

        if (matches.Count == 0)
            return new VarValue { Text = "", Flag = false, Number = 0 };

        var latest = matches[0];
        string body = repo.Load(latest.Path)?.Body ?? latest.Body;

        return new VarValue
        {
            Text = body,
            File = latest.Path,
            Title = latest.Title,
            Time = latest.Mtime.ToString(TimeFormat, CultureInfo.InvariantCulture),
            Snippet = repo.Snippet(latest.Path),
            Flag = true,
            Number = matches.Count,
        };
    }

    /// <summary>追加模式的目标：<c>target</c> 指向的文件，否则同分类下最近修改的一条；没有就返回 null（新建）。</summary>
    private static NoteEntry? ResolveAppendTarget(NoteRepository repo, NoteWriteRequest request, string notesRoot)
    {
        string target = (request.Target ?? "").Trim();
        if (target.Length > 0)
        {
            // 相对路径按数据根算（与配置里其它路径一个口径）
            string dataRoot = System.IO.Path.GetDirectoryName(
                System.IO.Path.GetFullPath(notesRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? "";
            string path = System.IO.Path.IsPathRooted(target)
                ? target
                : System.IO.Path.Combine(dataRoot, target);
            if (!File.Exists(path))
                throw ActionErrors.NotFound("追加目标不存在：" + path);
            return repo.Load(path) ?? throw ActionErrors.NotFound("读不出追加目标：" + path);
        }

        string category = (request.Category ?? "").Trim();
        var matches = repo.List(category.Length == 0 ? NoteRepository.Unclassified : category, null);
        return matches.Count == 0 ? null : repo.Load(matches[0].Path);
    }

    /// <summary>
    /// 把新正文接在旧正文后面：两段之间空一行；<paramref name="stamp"/> 时先写一行
    /// <c>## yyyy-MM-dd HH:mm</c>（日志式速记一眼能看出这次是什么时候追加的）。
    /// 换行跟随旧正文（旧文件是 <c>\r\n</c> 就继续用 <c>\r\n</c>），不把一份文件混成两种行尾。
    /// </summary>
    internal static string ComposeAppend(string existing, string addition, bool stamp, DateTime now)
    {
        string nl = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var sb = new StringBuilder(existing.TrimEnd());
        if (sb.Length > 0) sb.Append(nl).Append(nl);
        if (stamp)
            sb.Append("## ").Append(now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
              .Append(nl).Append(nl);
        sb.Append(addition.TrimEnd()).Append(nl);
        return sb.ToString();
    }

    /// <summary>
    /// 正文里的图片：出现了原路径就原地换成相对链接（保留用户自己写的 alt / 尖括号），
    /// 一次都没出现就补一行 <c>![名字](链接)</c>（与速记窗口粘图后插入的形状一致）。
    /// </summary>
    internal static string EmbedImages(string body, IReadOnlyList<string> sources, IReadOnlyList<string> links)
    {
        string text = body;
        var appended = new StringBuilder();

        for (int i = 0; i < sources.Count && i < links.Count; i++)
        {
            string source = sources[i];
            string link = links[i];
            bool replaced = false;

            // 用户可能写的是原路径、正斜杠版、反斜杠版；路径大小写不敏感
            foreach (string candidate in new[]
                     {
                         source,
                         source.Replace('\\', '/'),
                         source.Replace('/', '\\'),
                     }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (candidate.Length == 0) continue;
                string pattern = Regex.Escape(candidate);
                if (!Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)) continue;
                text = Regex.Replace(text, pattern, link.Replace("$", "$$"), RegexOptions.IgnoreCase);
                replaced = true;
                break;
            }

            if (replaced) continue;
            if (appended.Length == 0) appended.Append('\n');
            appended.Append("![").Append(System.IO.Path.GetFileNameWithoutExtension(source))
                    .Append("](").Append(link).Append(")\n");
        }

        return appended.Length == 0 ? text : text.TrimEnd() + "\n" + appended;
    }

    /// <summary>正文第一行有效内容（跳过开头空行、去掉 Markdown 记号，最多 40 字）作为默认标题。</summary>
    internal static string FirstLine(string body)
    {
        string first = "";
        foreach (string line in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            first = StripMarkdownPrefix(line);
            break;
        }
        return first.Length <= 40 ? first : first[..40];
    }

    /// <summary>
    /// 去掉行首的 Markdown 记号（<c># 标题</c> / <c>- 项</c> / <c>1. 项</c> / <c>&gt; 引用</c> / <c>- [ ] 待办</c>），
    /// 只取第一行当标题时用。记号后面必须跟着空白才算：<c>-5 度</c>、<c>1.5 倍</c> 原样保留。
    /// 整行都被去空时退回原行（别把标题弄成空串）。
    /// </summary>
    internal static string StripMarkdownPrefix(string line)
    {
        string original = line.Trim();
        string text = original;
        while (true)
        {
            string next = text.TrimStart();
            if (next.Length == 0) break;

            // 标题：1–6 个 #，后面必须跟空白（`#标题` 不算，但也不该误伤）
            int hashes = 0;
            while (hashes < next.Length && next[hashes] == '#') hashes++;
            if (hashes is > 0 and <= 6 && hashes < next.Length && char.IsWhiteSpace(next[hashes]))
            {
                text = next[(hashes + 1)..];
                continue;
            }

            // 引用
            if (next[0] == '>')
            {
                text = next[1..];
                continue;
            }

            // 待办：[ ] / [x]
            if (next.Length >= 3 && next[0] == '[' && next[2] == ']' && (next[1] == ' ' || next[1] is 'x' or 'X'))
            {
                text = next[3..];
                continue;
            }

            // 列表：- * + 或 1. 1)，后面必须跟空白
            int marker = 0;
            if (next[0] is '-' or '*' or '+')
            {
                marker = 1;
            }
            else
            {
                int digits = 0;
                while (digits < next.Length && char.IsAsciiDigit(next[digits])) digits++;
                if (digits > 0 && digits + 1 < next.Length && next[digits] is '.' or ')') marker = digits + 1;
            }
            if (marker > 0 && marker < next.Length && char.IsWhiteSpace(next[marker]))
            {
                text = next[(marker + 1)..];
                continue;
            }

            break;
        }

        string result = text.Trim();
        return result.Length == 0 ? original : result;
    }
}

/// <summary>
/// 「写速记」的入参。动作单元把参数求值后装成它，不再为了复用这套实现去拼一个
/// <see cref="ActionDto"/>。
/// </summary>
internal sealed record NoteWriteRequest
{
    /// <summary>正文（支持 ${变量}，由调用方先求值）。</summary>
    public string? Body { get; init; }

    /// <summary>标题（留空则取正文第一行）。</summary>
    public string? Title { get; init; }

    /// <summary>分类（留空 = 未分类；不存在时自动创建）。</summary>
    public string? Category { get; init; }

    /// <summary>true = 追加到已有速记（见 <see cref="Target"/>），false = 新建一条。</summary>
    public bool Append { get; init; }

    /// <summary>
    /// 追加目标（文件路径；相对路径按数据根算）。留空 = 取 <see cref="Category"/> 下最近修改的一条，
    /// 该分类里一条都没有时照新建处理。
    /// </summary>
    public string? Target { get; init; }

    /// <summary>追加时先写一行 <c>## yyyy-MM-dd HH:mm</c>（日志式速记默认要）。</summary>
    public bool Stamp { get; init; } = true;

    /// <summary>
    /// 图片（文件路径）：复制进这条速记的 <c>&lt;笔记名&gt;.files/</c>，正文里的原路径换成相对链接，
    /// 正文里没出现的补在末尾。用来把「截图」这类运行期图片存成速记资产。
    /// </summary>
    public IReadOnlyList<string>? Images { get; init; }
}
