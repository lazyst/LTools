using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Features.Builtin;

namespace LTools.Features.CapturePro;

/// <summary>一个可以用全局快捷键触发的截图动作（对应一条内置功能引用）。</summary>
/// <param name="Id">稳定键，与 Snow Shot <c>settingscatalog.cpp</c> 的动作名对齐（换显示名不影响它）。</param>
/// <param name="Reference"><c>hotkeys.json</c> 里存的引用（<see cref="FeatureCatalog"/> 的功能 Id）。</param>
/// <param name="Label">设置里那一行的名字。</param>
/// <param name="Hint">一句话说明（悬停提示 + 设置里的灰字）。</param>
/// <param name="Defaults">首次播种写进去的快捷键（canonical 写法；空数组 = 不播种）。</param>
internal sealed record CaptureShortcut(
    string Id, string Reference, string Label, string Hint, string[] Defaults);

/// <summary>
/// 截图 Pro 的全局快捷键：**就是 <c>configs/hotkeys.json</c> 里一个名为「截图 Pro」的预设组**。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>presentation/globalshortcutmanager.cpp</c> + <c>settingscatalog.cpp</c> 的
/// 「全局快捷键」页：每个采集 / 贴图动作可以绑 1–2 个全局快捷键，短按即触发；默认只播四条
/// （<c>F1</c> 截图、<c>Ctrl+F1</c> 截图并复制、<c>F3</c> 贴剪贴板、<c>Ctrl+F3</c> 恢复上一张关闭的贴图）。
/// </para>
/// <para>
/// LTools 已经有全局快捷键体系（<see cref="HotkeyRegistry"/> + 低级键盘钩子，支持单键与 CapsLock 修饰键），
/// 所以这里**不再造第二套注册**（那会和「快捷键」页各说一套、冲突也查不出来）：本类只负责
/// 「截图动作清单 → 这个组里的绑定」的读写、播种与摘要，运行期匹配、名单过滤、冲突判定都走既有实现。
/// </para>
/// <para>
/// 删掉某条不会因为重启又被塞回来：播种只看 <see cref="HotkeyFile.CaptureSeedVersion"/>，
/// 与 CapsLock 经典组的 <see cref="HotkeyFile.SeedVersion"/> 分开记。
/// </para>
/// </remarks>
internal static class CaptureShortcuts
{
    /// <summary>预设组名（用户可以在「快捷键」页里改名 / 停用整组）。</summary>
    public const string GroupName = "截图 Pro";

    /// <summary>每个动作最多绑几键（照上游 <c>maxShortcutCount = 2</c>）。</summary>
    public const int MaxPerAction = 2;

    /// <summary>全部可绑的截图动作（顺序 = 设置对话框与播种顺序）。</summary>
    public static readonly IReadOnlyList<CaptureShortcut> All = new[]
    {
        new CaptureShortcut("screenshot", "common.capture", "截图",
            "拖框截图（打开覆盖层，工具条 / 标注都在）", new[] { "f1" }),
        new CaptureShortcut("screenshot_delay", "ltools.captureDelay", "延时截图",
            "先等设置里的秒数（1–10，默认 3）再冻结画面", Array.Empty<string>()),
        new CaptureShortcut("screenshot_fixed", "ltools.capturePin", "截图并贴图",
            "框完立刻贴到屏幕上（不摆工具条）", Array.Empty<string>()),
        new CaptureShortcut("screenshot_ocr", "ltools.captureRecognize", "截图并识别",
            "框完立刻做文字识别并打开结果窗", Array.Empty<string>()),
        new CaptureShortcut("screenshot_ocr_copy", "ltools.captureRecognizeCopy", "截图并识别复制",
            "识别完直接把全文写进剪贴板（结果窗照开）", Array.Empty<string>()),
        new CaptureShortcut("screenshot_translation", "ltools.captureTranslate", "截图并翻译",
            "识别完按「翻译」设置里的目标语言自动翻一遍（没配端点会先弹设置）", Array.Empty<string>()),
        new CaptureShortcut("screenshot_scrolling", "ltools.captureScrolling", "截图并长截图",
            "框完立刻进长截图（自己滚或自动滚，拼成一张长图）", Array.Empty<string>()),
        new CaptureShortcut("screenshot_copy", "ltools.captureCopy", "截图并复制",
            "框完立刻复制到剪贴板", new[] { "ctrl+f1" }),
        new CaptureShortcut("screenshot_save", "ltools.captureSave", "截图并另存为",
            "框完立刻弹「另存为」", Array.Empty<string>()),
        new CaptureShortcut("screenshot_quick_save", "ltools.captureQuickSave", "截图并快速保存",
            "框完立刻按快速保存规则落盘（不弹框）", Array.Empty<string>()),
        new CaptureShortcut("screenshot_full_screen", "ltools.captureScreen", "截取当前显示器",
            "不弹覆盖层：直接抓光标所在显示器并复制", Array.Empty<string>()),
        new CaptureShortcut("screenshot_focused_window", "ltools.captureWindow", "截取前台窗口",
            "不弹覆盖层：直接抓前台窗口并复制", Array.Empty<string>()),
        new CaptureShortcut("screen_record", "common.record", "录屏",
            "打开录屏面板（选区 / 主屏 / 全部屏幕 / 前台窗口）", Array.Empty<string>()),
        new CaptureShortcut("open_capture_history", "ltools.captureHistory", "采集历史",
            "打开采集历史（截图 / 录屏都在里面）", Array.Empty<string>()),
        new CaptureShortcut("open_pin_to_screen_management", "ltools.pinManager", "贴图管理",
            "打开贴图管理页（浏览 / 批量贴回 / 删除）", Array.Empty<string>()),
        new CaptureShortcut("pin_clipboard_content", "common.pinClipboard", "贴剪贴板",
            "把剪贴板里的图片 / 文件 / 文字贴到屏幕上", new[] { "f3" }),
        new CaptureShortcut("pin_selected_files", "common.pinSelectedFiles", "贴选中的文件",
            "把资源管理器里选中的图片 / 文本文件贴到屏幕上", Array.Empty<string>()),
        new CaptureShortcut("restore_last_closed_windows", "ltools.pinRestoreLast", "恢复上一张关闭的贴图",
            "把最近关掉的那张贴回原来的位置", new[] { "ctrl+f3" }),
        new CaptureShortcut("hide_show_all_windows", "ltools.pinToggleAll", "隐藏 / 显示所有贴图",
            "一键把屏幕上的贴图全收起来，再按一次放回来", Array.Empty<string>()),
        new CaptureShortcut("toggle_window_under_cursor_always_on_top", "common.windowPin", "窗口置顶",
            "把鼠标位置所在的窗口切换置顶", Array.Empty<string>()),
        new CaptureShortcut("open_settings", "ltools.settings", "打开设置",
            "打开 LTools 设置（停在记住的那一页）", Array.Empty<string>()),
        new CaptureShortcut("toggle_global_hotkeys", "ltools.hotkeysToggle", "暂停 / 恢复全局快捷键",
            "紧急开关：暂停所有全局快捷键（这条本身例外，随时能恢复）", Array.Empty<string>()),
    };

    public static CaptureShortcut? Find(string id) =>
        All.FirstOrDefault(action => string.Equals(action.Id, id, StringComparison.Ordinal));

    public static CaptureShortcut? FindByReference(string reference) =>
        All.FirstOrDefault(action =>
            string.Equals(action.Reference, reference, StringComparison.Ordinal));

    /// <summary>「截图 Pro」这个组（没有就是 null —— 不改文件）。</summary>
    public static HotkeyGroup? GroupOf(HotkeyFile file) =>
        file.Groups.FirstOrDefault(group =>
            string.Equals(group.Name, GroupName, StringComparison.Ordinal));

    /// <summary>取（没有就建）「截图 Pro」组，放在最后 = 匹配优先级最低（不抢 CapsLock 经典组）。</summary>
    public static HotkeyGroup EnsureGroup(HotkeyFile file)
    {
        var group = GroupOf(file);
        if (group != null) return group;

        group = new HotkeyGroup { Id = file.NewId(), Name = GroupName, Enabled = true, BuiltIn = true };
        file.Groups.Add(group);
        return group;
    }

    /// <summary>这个动作当前绑的快捷键（canonical，按绑定顺序，最多 <see cref="MaxPerAction"/> 条）。</summary>
    public static IReadOnlyList<string> ChordsOf(HotkeyFile file, CaptureShortcut action)
    {
        var group = GroupOf(file);
        if (group == null) return Array.Empty<string>();

        return group.Bindings
            .Where(binding => string.Equals(binding.ActionId, action.Reference, StringComparison.Ordinal))
            .Select(binding => HotkeyChord.Normalize(binding.Chord))
            .Where(chord => chord.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxPerAction)
            .ToArray();
    }

    /// <summary>
    /// 换成这一组快捷键：先删掉该动作在本组里的全部绑定，再把合法的、去重后的前两条写回去。
    /// 空串 / 解析不了的写法直接忽略（对话框里录不出来，手改坏了也不该占一个空位）。
    /// </summary>
    /// <returns>真正写进去的条数。</returns>
    public static int SetChords(HotkeyFile file, CaptureShortcut action, IEnumerable<string>? chords)
    {
        var group = EnsureGroup(file);

        group.Bindings.RemoveAll(binding =>
            string.Equals(binding.ActionId, action.Reference, StringComparison.Ordinal));

        var cleaned = (chords ?? Array.Empty<string>())
            .Select(HotkeyChord.Normalize)
            .Where(chord => chord.Length > 0 && HotkeyChord.TryParse(chord, out _, out _))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxPerAction)
            .ToArray();

        foreach (string chord in cleaned)
        {
            group.Bindings.Add(new HotkeyBinding
            {
                Id = file.NewId(),
                Enabled = true,
                Chord = chord,
                ActionId = action.Reference,
            });
        }

        return cleaned.Length;
    }

    /// <summary>
    /// 首次播种（只播一次）：照上游默认值写四条，<c>capture_seed_version</c> 记到 1。
    /// 用户之后删掉 / 改掉都不会被塞回来。
    /// </summary>
    /// <returns>真写了东西返回 true（调用方据此决定要不要落盘 / 重新加载）。</returns>
    public static bool Seed(HotkeyFile file)
    {
        if (file.CaptureSeedVersion >= 1) return false;

        file.CaptureSeedVersion = 1;

        var group = EnsureGroup(file);
        foreach (var action in All)
        {
            if (action.Defaults.Length == 0) continue;

            // 已经有绑定（手写配置 / 上一版留下的）就不动它
            if (ChordsOf(file, action).Count > 0) continue;

            foreach (string chord in action.Defaults.Take(MaxPerAction))
                group.Bindings.Add(new HotkeyBinding
                {
                    Id = file.NewId(),
                    Enabled = true,
                    Chord = HotkeyChord.Normalize(chord),
                    ActionId = action.Reference,
                });
        }

        return true;
    }

    /// <summary>设置页那一行按钮下面的摘要：已绑的写「F1 截图 · Ctrl+F1 截图并复制」，没绑 = 「还没设置」。</summary>
    public static string Summary(HotkeyFile file, int max = 4)
    {
        var parts = new List<string>();
        foreach (var action in All)
        {
            foreach (string chord in ChordsOf(file, action))
            {
                parts.Add(HotkeyChord.Format(chord) + " " + action.Label);
                if (parts.Count >= max)
                    return string.Join(" · ", parts) + " · …";
            }
        }

        return parts.Count == 0 ? "还没设置" : string.Join(" · ", parts);
    }

    /// <summary>
    /// 与别的（启用中的）绑定撞车的动作：撞车的两条绑定的 Id 由 <see cref="HotkeyRegistry.FindConflicts(HotkeyFile)"/>
    /// 给出，这里只把 Id 映射回动作。对话框据此标「冲突」。
    /// </summary>
    public static IReadOnlyCollection<string> Conflicting(HotkeyFile file)
    {
        var conflicts = HotkeyRegistry.FindConflicts(file);
        if (conflicts.Count == 0) return Array.Empty<string>();

        var result = new HashSet<string>(StringComparer.Ordinal);
        var group = GroupOf(file);
        if (group == null) return result;

        foreach (var binding in group.Bindings)
        {
            if (!conflicts.Contains(binding.Id)) continue;
            var action = FindByReference(binding.ActionId);
            if (action != null) result.Add(action.Id);
        }

        return result;
    }
}
