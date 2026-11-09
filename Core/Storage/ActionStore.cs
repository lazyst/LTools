using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LTools.Config;
using LTools.Features;
using LTools.Native;

namespace LTools.Core.Storage;

/// <summary>
/// 动作目录（见 <c>docs/ARCHITECTURE.md</c> P2）：一个动作一个目录。
/// <code>
/// actions/&lt;id&gt;/action.json   动作体（名称 / 图标 / 说明 / 变量 / 步骤）
/// actions/&lt;id&gt;/files/        该动作自己的模板图 / 脚本（草稿采纳时整体搬进来）
/// trash/&lt;时间戳&gt;/&lt;id&gt;/       被删除的动作目录（保留一段时间，可手工捞回）
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <b>目录存在（含空）就是权威</b>：只要 <c>actions/</c> 在，动作清单就只从目录读，
/// 不再回落到 <c>configs/actions.json</c>——否则「删光所有动作」会在下次启动时被旧清单复活。
/// </para>
/// <para>
/// 目录名就是动作 Id（<see cref="ActionDto.Id"/>，<c>a1..aN</c>），菜单 / 面板 / 悬浮都按 Id 引用，
/// 因此不存在「改名要同步改引用」的问题。<see cref="SaveAll"/> 只写内容变了的文件，
/// 并把不在清单里的目录移进 <c>trash/</c>；删除动作因此只是「从清单消失」。
/// </para>
/// <para>
/// 运行期资源根：动作里的图片可以写 <c>files/x.png</c> 或裸文件名，先在这个动作的
/// <c>files/</c> 下找（<see cref="ResourceRootFor"/>），再看 <c>resources/images</c> 与
/// <c>resources/user</c>。所以采纳草稿不需要改写图片路径，搬目录即可。
/// </para>
/// </remarks>
internal static class ActionStore
{
    /// <summary>单个动作目录里的文件名。</summary>
    private const string ActionFileName = "action.json";

    /// <summary>动作私有资源目录名。</summary>
    private const string FilesDirName = "files";

    /// <summary>删除保留期：超过这个天数的 trash 目录在启动时清掉。</summary>
    private const int TrashKeepDays = 30;

    /// <summary>
    /// 当前配置锚点（<see cref="ConfigStore.Initialize"/> 设置）。
    /// 供运行 / 编辑器在不传路径的地方找到动作的 <c>files/</c>。
    /// </summary>
    public static string Anchor { get; set; } = "";

    // ============ 路径 ============

    /// <summary>动作根目录（数据根下的 <c>actions/</c>）。</summary>
    public static string Root(string anchorPath) => Path.Combine(BaseDir(anchorPath), "actions");

    /// <summary>某个动作的目录。</summary>
    public static string ActionDir(string anchorPath, string id) => Path.Combine(Root(anchorPath), id);

    /// <summary>某个动作的 <c>action.json</c>。</summary>
    public static string ActionPath(string anchorPath, string id) =>
        Path.Combine(ActionDir(anchorPath, id), ActionFileName);

    /// <summary>某个动作的私有资源目录（<c>files/</c>）。</summary>
    public static string FilesDir(string anchorPath, string id) =>
        Path.Combine(ActionDir(anchorPath, id), FilesDirName);

    /// <summary>动作目录是否已经建立（含空目录）——是则动作清单以目录为权威。</summary>
    public static bool HasStore(string anchorPath) => Directory.Exists(Root(anchorPath));

    /// <summary>当前动作的额外资源根（<c>files/</c> 存在时返回绝对路径，否则 null）。</summary>
    /// <remarks>
    /// 正式运行与编辑器都经由此处传入 <c>ResourceRoot</c>；草稿试运行由调用方另行指定。
    /// </remarks>
    public static string? ResourceRootFor(string? actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrEmpty(Anchor)) return null;
        if (!IsSafeId(actionId)) return null;
        string dir = FilesDir(Anchor, actionId);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>数据根下的 <c>trash/</c>（删除的动作目录与孤儿图都落这里，保留一段时间）。</summary>
    public static string TrashDir(string anchorPath) => Path.Combine(BaseDir(anchorPath), "trash");

    private static string BaseDir(string anchorPath)
    {
        if (string.IsNullOrWhiteSpace(anchorPath)) return AppPaths.DataRoot;
        string full = Path.GetFullPath(anchorPath);
        return Path.GetDirectoryName(full) ?? AppPaths.DataRoot;
    }

    /// <summary>目录名 / 文件名安全：拒绝空、<c>.</c>、<c>..</c> 与非法字符（正常 Id 是 <c>a1</c>）。</summary>
    private static bool IsSafeId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        && id != "."
        && id != ".."
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    // ============ 读 ============

    /// <summary>
    /// 读出全部动作（按 Id 排序）。读不出来的目录只记录并跳过（目录原样保留），
    /// <c>failed</c> 为 true 时调用方应把 <see cref="AppConfig.LoadFailed"/> 置位；
    /// 写入路径（<paramref name="strict"/>）直接抛出，避免残缺清单触发 <see cref="SaveAll"/> 把目录当孤儿搬走。
    /// </summary>
    public static (List<ActionDto> Actions, bool Failed) LoadAll(string anchorPath, bool strict = false)
    {
        var actions = new List<ActionDto>();
        bool failed = false;
        string root = Root(anchorPath);
        if (!Directory.Exists(root)) return (actions, false);

        foreach (string dir in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(dir);
            string file = Path.Combine(dir, ActionFileName);

            if (!IsSafeId(name))
            {
                AppLog.Warn("ActionStore", $"忽略非法动作目录：{name}");
                continue;
            }
            if (!File.Exists(file))
            {
                // 没有 action.json 的目录（手工建的 / 写到一半）不当动作，也不自动删。
                AppLog.Warn("ActionStore", $"忽略没有 {ActionFileName} 的目录：{name}");
                continue;
            }

            try
            {
                var action = ReadWithRetry(file) ?? new ActionDto { Id = name };
                if (string.IsNullOrWhiteSpace(action.Id)) action.Id = name;
                else if (!string.Equals(action.Id, name, StringComparison.Ordinal))
                {
                    // 目录名才是存储身份：手工改过 Id 时以目录名为准，避免下次保存写进另一个目录。
                    AppLog.Warn("ActionStore", $"动作 Id 与目录名不一致（{action.Id} ≠ {name}），以目录名为准");
                    action.Id = name;
                }
                actions.Add(action);
            }
            catch (Exception ex)
            {
                if (strict) throw new InvalidOperationException($"动作无法解析：{file}", ex);
                CrashLog.Write("ActionStore.Load", ex);
                AppLog.Error("ActionStore", $"动作目录读取失败，已跳过：{name}", ex);
                failed = true;
            }
        }

        // 与 ActionLibrary.Save 的落盘顺序一致：按 Id 序数排序，避免读目录顺序影响 diff。
        actions.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return (actions, failed);
    }

    private static ActionDto? ReadWithRetry(string path)
    {
        const int retries = 15;
        const int delayMs = 200;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<ActionDto>(File.ReadAllText(path, Encoding.UTF8),
                    ConfigBundle.Options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < retries)
            {
                System.Threading.Thread.Sleep(delayMs);
            }
        }
    }

    // ============ 写 ============

    /// <summary>
    /// 逐动作写回：内容没变的文件不动（保留 mtime）；清单里没有的目录移进 <c>trash/</c>。
    /// 目录本身一定会建出来（空清单也建），从此以后目录是动作清单的权威来源。
    /// </summary>
    public static void SaveAll(string anchorPath, IEnumerable<ActionDto> actions)
    {
        string root = Root(anchorPath);
        Directory.CreateDirectory(root);

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            if (action == null) continue;
            if (!IsSafeId(action.Id))
            {
                AppLog.Warn("ActionStore", $"动作 Id 不能当目录名，已跳过落盘：{action.Id}「{action.Name}」");
                continue;
            }

            live.Add(action.Id);
            Directory.CreateDirectory(ActionDir(anchorPath, action.Id));
            WriteIfChanged(ActionPath(anchorPath, action.Id), action);
        }

        foreach (string dir in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(dir);
            if (live.Contains(name)) continue;
            MoveToTrash(anchorPath, dir, name);
        }
    }

    /// <summary>先写临时文件再整体替换，避免写到一半崩溃留下半个 JSON。</summary>
    private static void WriteIfChanged(string path, ActionDto action)
    {
        string json = JsonSerializer.Serialize(action, ConfigBundle.Options);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == json) return;
        }
        catch { /* 读不出来（被占用）就照常写 */ }

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// 把一个草稿的 <c>files/</c> 搬进动作目录（采纳草稿用）。
    /// 保留原来的文件名，因此动作里的 <c>files/x.png</c> 不用改写。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>跨动作素材去重</b>：内容（SHA-256）与别的动作已有的文件相同时，用<b>硬链接</b>
    /// （<see cref="FileLinks.TryCreateHardLink"/>）指向那一份，磁盘上只存一次；
    /// 链接失败（跨卷 / 文件系统不支持）回退成复制。
    /// </para>
    /// <para>
    /// 硬链接共享内容，所以写目标文件前一律先删掉旧名字再建新的：直接覆盖会把共享同一份内容的
    /// 另一个动作的模板图一起改掉。
    /// </para>
    /// </remarks>
    /// <returns>写进动作的存储路径（<c>files/x.png</c>），供采纳回执展示。</returns>
    public static List<string> CopyFilesFrom(string anchorPath, string actionId, string sourceDir)
    {
        var copied = new List<string>();
        if (!IsSafeId(actionId) || !Directory.Exists(sourceDir)) return copied;

        string dest = FilesDir(anchorPath, actionId);
        Directory.CreateDirectory(dest);

        // 其它动作的文件按体积建索引：同体积才值得算哈希（模板图很小，这一步避免全盘扫描）。
        var index = new ContentIndex(anchorPath, actionId);
        int linked = 0;

        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (File.Exists(target))
            {
                if (SameContent(file, target))
                {
                    // 内容没变（重复采纳同一张图）：保持原文件不动，保留硬链接。
                    index.Add(target);
                    copied.Add(FilesDirName + "/" + relative.Replace('\\', '/'));
                    continue;
                }
                // 内容变了：先删名字断开可能存在的硬链接，再写新内容。
                File.Delete(target);
            }

            if (index.FindSameContent(file) is { } shared && FileLinks.TryCreateHardLink(target, shared))
            {
                linked++;
                index.Add(target);
            }
            else
            {
                File.Copy(file, target, overwrite: false);
                index.Add(target);
            }
            copied.Add(FilesDirName + "/" + relative.Replace('\\', '/'));
        }

        if (linked > 0)
            AppLog.Info("ActionStore", $"动作 {actionId}：{linked} 个素材与已有动作共享（硬链接，不重复占空间）");
        copied.Sort(StringComparer.OrdinalIgnoreCase);
        return copied;
    }

    /// <summary>
    /// 「其它动作的 files/」按文件体积索引，按内容哈希查重（构建一次，服务一次采纳）。
    /// </summary>
    private sealed class ContentIndex
    {
        private readonly Dictionary<long, List<string>> _bySize = new();
        private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

        public ContentIndex(string anchorPath, string excludeActionId)
        {
            string root = Root(anchorPath);
            if (!Directory.Exists(root)) return;

            foreach (string dir in Directory.GetDirectories(root))
            {
                if (string.Equals(Path.GetFileName(dir), excludeActionId, StringComparison.OrdinalIgnoreCase)) continue;
                string files = Path.Combine(dir, FilesDirName);
                if (!Directory.Exists(files)) continue;
                try
                {
                    foreach (string file in Directory.GetFiles(files, "*", SearchOption.AllDirectories))
                        Add(file);
                }
                catch (Exception ex)
                {
                    // 某个动作目录读不动不影响采纳，只是少一个可以共享的候选。
                    AppLog.Warn("ActionStore", $"扫描动作素材失败（跳过）：{dir}：{ex.Message}");
                }
            }
        }

        public void Add(string path)
        {
            try
            {
                long size = new FileInfo(path).Length;
                if (!_bySize.TryGetValue(size, out var list)) _bySize[size] = list = new List<string>();
                list.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 文件刚被删 / 被占用：不参与去重。
            }
        }

        /// <summary>找一份内容相同的既有文件；没有返回 null。</summary>
        public string? FindSameContent(string file)
        {
            long size;
            try { size = new FileInfo(file).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            if (!_bySize.TryGetValue(size, out var candidates)) return null;

            string want = Hash(file);
            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                if (string.Equals(Hash(candidate), want, StringComparison.Ordinal)) return candidate;
            }
            return null;
        }

        private string Hash(string path)
        {
            if (_hashes.TryGetValue(path, out string? known)) return known;
            using var fs = File.OpenRead(path);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
            _hashes[path] = hash;
            return hash;
        }
    }

    /// <summary>两个文件内容是否相同（先比长度，再比 SHA-256）。</summary>
    private static bool SameContent(string a, string b)
    {
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            return string.Equals(HashFile(a), HashFile(b), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
    }

    // ============ trash ============

    /// <summary>把一个已删除动作的目录移进 <c>trash/&lt;时间戳&gt;/</c>（失败只记录，不影响保存）。</summary>
    private static void MoveToTrash(string anchorPath, string dir, string name)
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string trash = Path.Combine(TrashDir(anchorPath), stamp);
            string target = Path.Combine(trash, name);
            for (int n = 2; Directory.Exists(target) || File.Exists(target); n++)
                target = Path.Combine(trash, $"{name}-{n}");

            Directory.CreateDirectory(trash);
            Directory.Move(dir, target);
            AppLog.Info("ActionStore", $"已删除的动作目录移入 trash：{name}");
        }
        catch (Exception ex)
        {
            CrashLog.Write("ActionStore.MoveToTrash", ex);
            AppLog.Warn("ActionStore", $"动作目录移入 trash 失败（保留原目录）：{name}：{ex.Message}");
        }
    }

    /// <summary>清掉超过保留期的 trash 目录（启动时调用一次）。失败忽略。</summary>
    public static void PruneTrash(string anchorPath)
    {
        try
        {
            string root = TrashDir(anchorPath);
            if (!Directory.Exists(root)) return;
            DateTime cutoff = DateTime.Now.AddDays(-TrashKeepDays);
            int removed = 0;
            foreach (string dir in Directory.GetDirectories(root))
            {
                try
                {
                    if (Directory.GetLastWriteTime(dir) >= cutoff) continue;
                    Directory.Delete(dir, true);
                    removed++;
                }
                catch (Exception ex)
                {
                    CrashLog.Write("ActionStore.PruneTrash", ex);
                }
            }
            if (removed > 0) AppLog.Info("ActionStore", $"清理过期的动作回收目录 {removed} 个");
        }
        catch (Exception ex)
        {
            CrashLog.Write("ActionStore.PruneTrash", ex);
        }
    }
}
