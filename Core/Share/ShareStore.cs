using System.IO;
using System.Text;
using System.Text.Json;

namespace LTools.Core.Share;

/// <summary>
/// 共享区落盘：<c>items.json</c>（本机发出去的清单）+ <c>files/</c>（剪贴板 / 截图落盘）
/// + <c>inbox/</c>（对方发来的，目录即真相）。
/// </summary>
/// <remarks>
/// <para>
/// **发送不复制文件**：清单只记绝对路径，大文件加进来是瞬间的，跨盘也一样。
/// 代价是原文件移动 / 删除 / 改名后那一项失效（界面标「已失效」，下载 404）。
/// </para>
/// <para>
/// 读清单失败按空表继续（坏文件不挡程序启动）；写清单先写 <c>.tmp</c> 再替换。
/// 所有公开方法内部加锁，供 Kestrel 请求线程与 UI 线程共用。
/// </para>
/// </remarks>
internal sealed class ShareStore
{
    private static ShareStore? _default;

    /// <summary>数据根下的默认实例（窗口与服务共用）。</summary>
    public static ShareStore Default => _default ??= new ShareStore(AppPaths.ShareDir);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Dictionary<string, string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".json"] = "application/json",
        [".csv"] = "text/csv; charset=utf-8",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".7z"] = "application/x-7z-compressed",
        [".rar"] = "application/vnd.rar",
        [".mp4"] = "video/mp4",
        [".mkv"] = "video/x-matroska",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac",
        [".m4a"] = "audio/mp4",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".exe"] = "application/octet-stream",
    };

    private readonly object _gate = new();
    private List<ShareItem>? _items;

    public ShareStore(string root)
    {
        Root = Path.GetFullPath(root);
        FilesDir = Path.Combine(Root, "files");
        InboxDir = Path.Combine(Root, "inbox");
        PartialDir = Path.Combine(InboxDir, ".partial");
        ItemsPath = Path.Combine(Root, "items.json");
    }

    public string Root { get; }
    public string ItemsPath { get; }
    public string FilesDir { get; }
    public string InboxDir { get; }
    public string PartialDir { get; }

    public void EnsureLayout()
    {
        Directory.CreateDirectory(FilesDir);
        Directory.CreateDirectory(InboxDir);
        Directory.CreateDirectory(PartialDir);
    }

    // ============ 本机发出去的清单 ============

    public IReadOnlyList<ShareItem> Items()
    {
        lock (_gate) return LoadLocked().ToList();
    }

    public ShareItem? Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate) return LoadLocked().FirstOrDefault(i => i.Id == id);
    }

    /// <summary>加一条（默认原地引用）。文件必须存在。</summary>
    public ShareItem AddPath(string path, bool copied = false)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("要共享的文件不存在", path);
        var info = new FileInfo(path);

        lock (_gate)
        {
            var items = LoadLocked();
            var item = new ShareItem
            {
                Id = NewItemId(items),
                Name = UniqueDisplayName(items, info.Name),
                Path = path,
                Size = info.Length,
                Mime = MimeFor(info.Name),
                AddedAt = DateTime.Now,
                Copied = copied,
            };
            items.Add(item);
            SaveLocked(items);
            return item;
        }
    }

    /// <summary>移除一条；<see cref="ShareItem.Copied"/> 时连文件一起删，否则不碰原文件。</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var items = LoadLocked();
            int index = items.FindIndex(i => i.Id == id);
            if (index < 0) return false;

            var item = items[index];
            items.RemoveAt(index);
            SaveLocked(items);
            if (item.Copied) TryDelete(item.Path);
            return true;
        }
    }

    /// <summary>把一段文字写进 <c>files/</c> 并加清单（剪贴板文字、自己生成的文本）。</summary>
    public ShareItem AddText(string text, string? name = null)
    {
        EnsureLayout();
        string fileName = SafeName(name ?? $"文字 {Stamp()}.txt");
        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) fileName += ".txt";
        string path = UniquePath(FilesDir, fileName);
        File.WriteAllText(path, text ?? "", new UTF8Encoding(false));
        return AddPath(path, copied: true);
    }

    /// <summary>对方发来的文字：写进 <c>inbox/</c>（**不进**本机发出去的清单）。</summary>
    public string AddInboxText(string text, string? name = null)
    {
        EnsureLayout();
        string fileName = SafeName(name ?? $"文字 {Stamp()}.txt");
        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) fileName += ".txt";

        string path = UniquePath(InboxDir, fileName);
        File.WriteAllText(path, text ?? "", new UTF8Encoding(false));
        return path;
    }

    // ============ 对方发来的 inbox ============

    /// <summary><c>inbox/</c> 下的文件，按修改时间倒序；<c>.partial/</c> 是子目录，天然不会被列出。</summary>
    public IReadOnlyList<ShareInboxFile> Inbox()
    {
        EnsureLayout();
        try
        {
            return Directory.EnumerateFiles(InboxDir)
                .Select(p => new FileInfo(p))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new ShareInboxFile(f.Name, f.Length, f.LastWriteTime, MimeFor(f.Name)))
                .ToList();
        }
        catch
        {
            return Array.Empty<ShareInboxFile>();
        }
    }

    /// <summary>在 <c>inbox/</c> 里找一个不重名的位置（<c>报告 (2).pdf</c>）。</summary>
    public string UniqueInboxPath(string name) => UniquePath(InboxDir, SafeName(name));

    public bool RemoveInbox(string name)
    {
        if (!IsPlainName(name)) return false;
        string path = Path.Combine(InboxDir, name);
        if (!File.Exists(path)) return false;
        TryDelete(path);
        return true;
    }

    /// <summary>未完成分片用的临时文件路径。</summary>
    public string PartialPath(string uploadId) => Path.Combine(PartialDir, SafeName(uploadId) + ".part");

    // ============ 纯函数 ============

    /// <summary>文件名清洗：去路径分隔符与非法字符、限长、避开 Windows 保留名。</summary>
    public static string SafeName(string? name)
    {
        string raw = Path.GetFileName((name ?? "").Trim());
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (c < 32) continue;
            if ("<>:\"/\\|?*".IndexOf(c) >= 0) continue;
            sb.Append(c);
        }

        string s = sb.ToString().Trim().TrimEnd('.');
        if (s.Length > 120) s = s[..120];
        if (s.Length == 0) s = "file";

        string stem = Path.GetFileNameWithoutExtension(s);
        if (ReservedNames.Contains(stem)) s = "_" + s;
        return s;
    }

    /// <summary>URL 里的名字只允许「纯文件名」——不能带目录、不能是 <c>..</c>。</summary>
    public static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name == Path.GetFileName(name)
        && name != "." && name != ".."
        && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0;

    public static string MimeFor(string? name)
    {
        string ext = Path.GetExtension(name ?? "");
        return Mimes.TryGetValue(ext, out string? mime) ? mime : "application/octet-stream";
    }

    public static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HHmm");

    /// <summary>在目录里给 <paramref name="fileName"/> 找一个不冲突的名字。</summary>
    public static string UniquePath(string dir, string fileName)
    {
        Directory.CreateDirectory(dir);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string candidate = Path.Combine(dir, fileName);
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
        return candidate;
    }

    // ============ 内部 ============

    private List<ShareItem> LoadLocked()
    {
        if (_items != null) return _items;
        try
        {
            if (File.Exists(ItemsPath))
                _items = JsonSerializer.Deserialize<List<ShareItem>>(File.ReadAllText(ItemsPath), Json);
        }
        catch
        {
            _items = null;   // 坏了按空表继续，不挡启动
        }
        return _items ??= new List<ShareItem>();
    }

    private void SaveLocked(List<ShareItem> items)
    {
        EnsureLayout();
        AtomicFile.WriteJson(ItemsPath, items, Json);
    }

    private static string NewItemId(List<ShareItem> items)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string id = IdFactory.ShortId(4);
            if (items.All(i => i.Id != id)) return id;
        }
        return IdFactory.ShortId(6);
    }

    private static string UniqueDisplayName(List<ShareItem> items, string name)
    {
        string candidate = name;
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int n = 2; items.Any(i => string.Equals(i.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = $"{stem} ({n}){ext}";
        return candidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 被占用就留着，下次启动的孤儿清理会处理；不挡「移除」这个动作。
        }
    }
}
