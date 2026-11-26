using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using LTools.Config;
using LTools.Core;
using LTools.Features;

namespace LTools.Features.CapturePro;

/// <summary>四张快捷键表（照 Snow Shot 的 <c>screenshot_shortcuts</c> / <c>drawing_shortcuts</c> / <c>pin_to_screen_shortcuts</c> / <c>screen_recording_shortcuts</c>）。</summary>
internal enum CaptureShortcutScope
{
    Screenshot,
    Drawing,
    PinToScreen,
    Recording,
}

/// <summary>一条快捷键：作用域 + 上游动作 Id + 中文说明 + 上游默认组合键。</summary>
internal sealed record CaptureShortcutEntry(
    CaptureShortcutScope Scope,
    string Id,
    string Label,
    string[] Defaults);

/// <summary>
/// 截图 Pro 的快捷键表：默认值逐条照 <c>storage/configurationschema.cpp</c>，
/// 用户改过的项存在 <c>configs/settings.json</c> 的 <c>CaptureShortcuts</c> 段。
/// </summary>
/// <remarks>
/// <para>
/// 匹配、归一化、读写都是纯函数（<c>--selftest</c> 直接断言）；界面只按结果派发。
/// 修饰键**精确匹配**：<c>Ctrl+C</c> 不会被 <c>Ctrl+Shift+C</c> 命中（与 <see cref="HotkeyChord"/> 同口径）。
/// </para>
/// <para>
/// 「按住」类动作（锁正方形 / 锁比例 / 整个选区）走 <see cref="Held"/>，不占 keydown 事件——
/// 上游同样是 <c>press / release</c> 触发器。
/// </para>
/// </remarks>
internal static class CaptureShortcutTable
{
    /// <summary>全部条目（顺序照上游：覆盖层 → 绘图 → 贴图 → 录屏）。</summary>
    public static readonly IReadOnlyList<CaptureShortcutEntry> All = new[]
    {
        // ---- 覆盖层（screenshot_shortcuts/*）----
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "move_tool", "移动选区 / 标注", new[] { "M", "Ctrl+E" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "move_cursor_up", "光标上移", new[] { "W", "Up" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "move_cursor_down", "光标下移", new[] { "S", "Down" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "move_cursor_left", "光标左移", new[] { "A", "Left" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "move_cursor_right", "光标右移", new[] { "D", "Right" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "move_entire_selection", "按住拖动整个选区", new[] { "Space" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "keep_selection_width_and_height_consistent", "按住锁正方形", new[] { "Shift" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "selection_aspect_ratio_snap", "按住锁当前比例", new[] { "Q" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "switch_selection_between_window_and_window_sub_element", "整窗 / 窗口子元素", new[] { "Tab" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "next_selection_type", "下一个选区类型", new[] { "Ctrl+Tab" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "previous_selection_type", "上一个选区类型", new[] { "Ctrl+Shift+Tab" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "previous_screenshot_history", "上一张采集（更早）", new[] { "," }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "next_screenshot_history", "下一张采集（更新）", new[] { "." }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "select_previously_selected_area", "上一个选过的区域", new[] { "R" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "select_next_previously_selected_area", "下一个选过的区域", new[] { "Shift+R" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "duplicate_latest_region", "复制最后一块区域", new[] { "R" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "toggle_cursor_visibility", "显示 / 隐藏光标", new[] { "`" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "recapture", "重新截图", new[] { "Alt+R" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "toggle_coordinate_mode", "全局 / 相对坐标", new[] { "Shift+P" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "toggle_guides", "参考线", new[] { "Alt" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "copy_color", "复制颜色", new[] { "C" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "table_recognition", "表格识别", new[] { "Ctrl+X" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "qr_code_recognition", "二维码识别", new[] { "Ctrl+Q" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "video_recording", "录屏", new[] { "Ctrl+R" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "text_recognition", "文字识别", new[] { "Ctrl+D" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "text_translation", "翻译", new[] { "Ctrl+T" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "scrolling_screenshot", "长截图", new[] { "L" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "quick_save", "快速保存", new[] { "Ctrl+Shift+S" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "print", "打印", new[] { "Ctrl+P" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "save_as_file", "另存为", new[] { "Ctrl+S" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "pin_to_screen", "贴图", new[] { "Ctrl+F" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "pin_window_to_screen", "贴窗口", new[] { "Alt+W" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "cancel_screenshot", "退出截图", new[] { "Esc" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "copy_to_clipboard", "复制", new[] { "Ctrl+C", "Enter" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "undo", "撤销", new[] { "Ctrl+Z" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Screenshot, "redo", "重做", new[] { "Ctrl+Y" }),

        // ---- 绘图工具（drawing_shortcuts/*）----
        // 上游有默认值的照抄；上游留空的那几项（line / speech_bubble / distance / angle /
        // bracket / magnifier / spotlight）用 LTools 一直在用的字母补上，避免「本来能按出来的工具
        // 升级后没了」。上游没有的 LTools 专有工具（polyline / highlight_rect / mosaic /
        // brush_filter / auto_filter / picker / 两把橡皮）同样各占一个不冲突的字母。
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "select", "选择", new[] { "V", "Q" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "shape", "形状", new[] { "1", "R" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "arrow", "箭头", new[] { "2", "A" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "line", "直线", new[] { "I" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "polyline", "折线", new[] { "L" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "distance", "测距", new[] { "D" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "angle", "测角", new[] { "G" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "bracket", "括号", new[] { "B" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "speech_bubble", "气泡", new[] { "O" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "brush", "画笔", new[] { "3", "P" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "picker", "取色", new[] { "C" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "spotlight", "聚光", new[] { "S" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "highlight", "荧光笔", new[] { "4", "H" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "highlight_rect", "矩形荧光", new[] { "J" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "text", "文字", new[] { "5", "T" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "serial_number", "序号", new[] { "6", "N" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "magnifier", "放大镜", new[] { "Z" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "watermark", "水印", new[] { "9", "W" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "mosaic", "马赛克", new[] { "0", "M" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "filter", "马赛克 / 滤镜", new[] { "7", "F" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "brush_filter", "涂抹滤镜", new[] { "Y" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "auto_filter", "自动打码", new[] { "X" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "eraser", "橡皮", new[] { "8", "E" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "rect_eraser", "矩形橡皮", new[] { "K" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "brush_eraser", "画笔橡皮", new[] { "U" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Drawing, "reset_canvas", "清空画布", new[] { "Ctrl+Shift+Z" }),

        // ---- 贴图窗口（pin_to_screen_shortcuts/*）----
        // 只列真能做的事：上游的打印 / 旋转翻转 / 还原变换 / 动图播放控制 / 调整窗口大小 /
        // 销毁窗口在 LTools 贴图上还没有对应能力，不注册（见 docs/CAPTURE_PARITY.md §5）。
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "copy_to_clipboard", "复制图片", new[] { "Ctrl+C" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "copy_original_content", "复制原文", new[] { "Ctrl+Shift+C" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "save_as_file", "另存为", new[] { "Ctrl+S" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "show_text_recognition_results", "显示识别结果", new[] { "Ctrl+D" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "drawing_mode", "标注", new[] { "Space" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "thumbnail_mode", "缩略图模式", new[] { "R" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "hide_to_top", "收到屏幕顶部", new[] { "H" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "toggle_click_through", "点击穿透", new[] { "Ctrl+M" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "toggle_lock", "锁定位置", new[] { "L" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "always_on_top", "置顶", new[] { "T" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "show_border", "显示边框", new[] { "B" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "show_shadow", "显示阴影", new[] { "Y" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "close_window", "关闭", new[] { "Esc" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "increase_opacity", "提高不透明度", new[] { "]" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "decrease_opacity", "降低不透明度", new[] { "[" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "increase_scale", "放大", new[] { "." }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "decrease_scale", "缩小", new[] { "," }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "move_cursor_up", "上移", new[] { "W", "Up" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "move_cursor_down", "下移", new[] { "S", "Down" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "move_cursor_left", "左移", new[] { "A", "Left" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "move_cursor_right", "右移", new[] { "D", "Right" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "batch_select_all", "全选贴图", new[] { "Ctrl+Alt+A" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "batch_save_as_file", "另存所选", new[] { "Ctrl+Alt+S" }),
        new CaptureShortcutEntry(CaptureShortcutScope.PinToScreen, "batch_copy_files_to_clipboard", "复制所选为文件", new[] { "Ctrl+Alt+C" }),

        // ---- 录屏面板（screen_recording_shortcuts/*）----
        new CaptureShortcutEntry(CaptureShortcutScope.Recording, "export", "结束并导出", new[] { "Ctrl+E" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Recording, "toggle_recording", "开始 / 暂停 / 继续", new[] { "Ctrl+S" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Recording, "copy_to_clipboard", "复制最近一条录屏", new[] { "Ctrl+C" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Recording, "end_recording", "关闭录屏面板", new[] { "Esc" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Recording, "previous_recording_area", "上一块录屏区域", new[] { "R" }),
        new CaptureShortcutEntry(CaptureShortcutScope.Recording, "next_recording_area", "下一块录屏区域", new[] { "Shift+R" }),
    };

    private static readonly object Gate = new();
    private static CaptureShortcutsFile? _cached;
    private static readonly Dictionary<CaptureShortcutScope, List<(CaptureShortcutEntry Entry, CaptureChord[] Chords)>> Parsed = new();

    /// <summary>
    /// 自检 / 冒烟用：把读写锚点指到临时目录（<c>null</c> = 用真实配置）。
    /// </summary>
    /// <remarks>自检进程里 <see cref="ConfigStore.ConfigPath"/> 是空的，回落锚点就是开发机的仓库根；
    /// 不加这个口子，「改一项再恢复默认」的断言会把真实 <c>configs/settings.json</c> 写一遍。</remarks>
    internal static string? AnchorOverride { get; set; }

    private static string Anchor => AnchorOverride ?? ConfigStore.Anchor;

    /// <summary>这一作用域的全部条目（顺序固定）。</summary>
    public static IEnumerable<CaptureShortcutEntry> Of(CaptureShortcutScope scope) =>
        All.Where(e => e.Scope == scope);

    /// <summary>按 Id 找条目。</summary>
    public static CaptureShortcutEntry? Find(CaptureShortcutScope scope, string id) =>
        All.FirstOrDefault(e => e.Scope == scope && e.Id == id);

    /// <summary>生效的组合键（用户改过就是用户的，否则是上游默认）。</summary>
    public static IReadOnlyList<string> Chords(CaptureShortcutScope scope, string id)
    {
        var entry = Find(scope, id);
        if (entry == null) return Array.Empty<string>();

        var map = MapOf(scope);
        return map.TryGetValue(id, out var custom) ? custom.ToArray() : entry.Defaults;
    }

    /// <summary>显示文案：<c>Ctrl+C / Enter</c>；没有快捷键时是空串。</summary>
    public static string Describe(CaptureShortcutScope scope, string id)
    {
        var list = Chords(scope, id);
        if (list.Count == 0) return "";
        return string.Join(" / ", list.Select(t => CaptureChord.TryParse(t, out var c) ? c.Display() : t));
    }

    /// <summary>「复制（Ctrl+C）」这类提示：有快捷键就补在括号里。</summary>
    public static string WithChord(string tip, CaptureShortcutScope scope, string id)
    {
        string chords = Describe(scope, id);
        return chords.Length == 0 ? tip : tip + "（" + chords + "）";
    }

    /// <summary>这一项是不是还是默认值。</summary>
    public static bool IsDefault(CaptureShortcutScope scope, string id) =>
        !MapOf(scope).ContainsKey(id);

    /// <summary>存一项（空列表 = 明确「没有快捷键」）。</summary>
    public static void Set(CaptureShortcutScope scope, string id, IEnumerable<string> chords)
    {
        var list = Normalize(chords);
        Save(file => Map(file, scope)[id] = list);
    }

    /// <summary>恢复一项的默认值。</summary>
    public static void Reset(CaptureShortcutScope scope, string id) =>
        Save(file => Map(file, scope).Remove(id));

    /// <summary>恢复一整组的默认值。</summary>
    public static void ResetAll(CaptureShortcutScope scope) =>
        Save(file => Map(file, scope).Clear());

    /// <summary>改过的项数（设置卡片摘要用）。</summary>
    public static int CustomCount(CaptureShortcutScope scope) => MapOf(scope).Count;

    /// <summary>配置被外部改过（导入备份 / 冒烟切目录）时丢掉缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = null;
            Parsed.Clear();
        }
    }

    /// <summary>组合键列表归一化（解析得了的写成 canonical 显示形式，解析不了的保留原文）。</summary>
    public static List<string> Normalize(IEnumerable<string> chords)
    {
        var result = new List<string>();
        foreach (string raw in chords)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string text = CaptureChord.TryParse(raw, out var chord) ? chord.Display() : raw.Trim();
            if (!result.Contains(text, StringComparer.OrdinalIgnoreCase)) result.Add(text);
        }

        return result;
    }

    /// <summary>
    /// 在某个作用域里找命中的动作（修饰键精确匹配）。<paramref name="id"/> 为空串 = 没命中。
    /// </summary>
    public static bool Match(CaptureShortcutScope scope, ushort vk, bool ctrl, bool shift, bool alt,
        bool win, out string id)
    {
        id = "";
        if (vk == 0) return false;

        foreach (var (entry, chords) in ParsedOf(scope))
        {
            foreach (var chord in chords)
            {
                if (!chord.Matches(vk, ctrl, shift, alt, win)) continue;
                id = entry.Id;
                return true;
            }
        }

        return false;
    }

    /// <summary>WPF 键盘事件版本（覆盖层 / 编辑窗用；<c>Key.System</c> 已由调用方换成 <c>SystemKey</c>）。</summary>
    public static bool Match(CaptureShortcutScope scope, KeyEventArgs e, out string id)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        return Match(scope, vk,
            (mods & ModifierKeys.Control) != 0,
            (mods & ModifierKeys.Shift) != 0,
            (mods & ModifierKeys.Alt) != 0,
            (mods & ModifierKeys.Windows) != 0, out id);
    }

    /// <summary>
    /// 这个动作现在还绑着一个「裸修饰键」吗（轻按 Alt 切参考线、轻按 Shift 换色值格式）。
    /// 用户把那条改掉之后，轻按就不该再有那个语义。
    /// </summary>
    public static bool BindsBareModifier(CaptureShortcutScope scope, string id, ushort vk)
    {
        bool IsModifier(ushort value) =>
            value is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

        bool Same(ushort a, ushort b) => (a, b) switch
        {
            (0xA0 or 0xA1, 0xA0 or 0xA1) => true,
            (0xA2 or 0xA3, 0xA2 or 0xA3) => true,
            (0xA4 or 0xA5, 0xA4 or 0xA5) => true,
            (0x5B or 0x5C, 0x5B or 0x5C) => true,
            _ => a == b,
        };

        foreach (string text in Chords(scope, id))
        {
            if (!CaptureChord.TryParse(text, out var chord)) continue;
            if (IsModifier(chord.Vk) && Same(chord.Vk, vk)) return true;
        }

        return false;
    }

    /// <summary>
    /// 「按住」类动作现在按没按住（锁正方形 / 锁比例 / 整个选区）。只看这个动作自己配的组合键。
    /// </summary>
    public static bool Held(CaptureShortcutScope scope, string id)
    {
        var entry = Find(scope, id);
        if (entry == null) return false;

        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;
        bool win = (mods & ModifierKeys.Windows) != 0;

        foreach (string text in Chords(scope, id))
        {
            if (!CaptureChord.TryParse(text, out var chord)) continue;
            if (chord.Vk is 0xA0 or 0xA1)     // 裸 Shift
            {
                if (shift && !ctrl && !alt && !win) return true;
                continue;
            }

            if (chord.Vk is 0xA2 or 0xA3)     // 裸 Ctrl
            {
                if (ctrl && !shift && !alt && !win) return true;
                continue;
            }

            if (chord.Vk is 0xA4 or 0xA5)     // 裸 Alt
            {
                if (alt && !ctrl && !shift && !win) return true;
                continue;
            }

            if (chord.Vk is 0x5B or 0x5C)     // 裸 Win
            {
                if (win && !ctrl && !shift && !alt) return true;
                continue;
            }

            if (chord.HasCtrl != ctrl || chord.HasShift != shift || chord.HasAlt != alt ||
                chord.HasWin != win) continue;

            if (Keyboard.IsKeyDown(KeyInterop.KeyFromVirtualKey(chord.Vk))) return true;
        }

        return false;
    }

    /// <summary>绘图快捷键对应的工具（不是工具类动作返回 null）。</summary>
    public static CaptureTool? ToolOf(string id) => id switch
    {
        "select" => CaptureTool.Select,
        "shape" => CaptureTool.Shape,
        "arrow" => CaptureTool.Arrow,
        "line" => CaptureTool.Line,
        "polyline" => CaptureTool.Polyline,
        "distance" => CaptureTool.Distance,
        "angle" => CaptureTool.Angle,
        "bracket" => CaptureTool.Bracket,
        "speech_bubble" => CaptureTool.SpeechBubble,
        "brush" => CaptureTool.Pen,
        "picker" => CaptureTool.Picker,
        "spotlight" => CaptureTool.Spotlight,
        "highlight" => CaptureTool.Highlighter,
        "highlight_rect" => CaptureTool.HighlightRect,
        "text" => CaptureTool.Text,
        "serial_number" => CaptureTool.Serial,
        "magnifier" => CaptureTool.Magnifier,
        "watermark" => CaptureTool.Watermark,
        "mosaic" => CaptureTool.Mosaic,
        "filter" => CaptureTool.Filter,
        "brush_filter" => CaptureTool.BrushFilter,
        "auto_filter" => CaptureTool.AutoFilter,
        "eraser" => CaptureTool.Eraser,
        "rect_eraser" => CaptureTool.RectEraser,
        "brush_eraser" => CaptureTool.BrushEraser,
        _ => null,
    };

    /// <summary>工具对应的绘图快捷键动作 Id（按钮悬停提示用）。</summary>
    public static string? IdOfTool(CaptureTool tool) => tool switch
    {
        CaptureTool.Select => "select",
        CaptureTool.Shape => "shape",
        CaptureTool.Arrow => "arrow",
        CaptureTool.Line => "line",
        CaptureTool.Polyline => "polyline",
        CaptureTool.Distance => "distance",
        CaptureTool.Angle => "angle",
        CaptureTool.Bracket => "bracket",
        CaptureTool.SpeechBubble => "speech_bubble",
        CaptureTool.Pen => "brush",
        CaptureTool.Picker => "picker",
        CaptureTool.Spotlight => "spotlight",
        CaptureTool.Highlighter => "highlight",
        CaptureTool.HighlightRect => "highlight_rect",
        CaptureTool.Text => "text",
        CaptureTool.Serial => "serial_number",
        CaptureTool.Magnifier => "magnifier",
        CaptureTool.Watermark => "watermark",
        CaptureTool.Mosaic => "mosaic",
        CaptureTool.Filter => "filter",
        CaptureTool.BrushFilter => "brush_filter",
        CaptureTool.AutoFilter => "auto_filter",
        CaptureTool.Eraser => "eraser",
        CaptureTool.RectEraser => "rect_eraser",
        CaptureTool.BrushEraser => "brush_eraser",
        _ => null,
    };

    // ============ 读写 ============

    private static Dictionary<string, List<string>> MapOf(CaptureShortcutScope scope)
    {
        var file = Snapshot();
        return scope switch
        {
            CaptureShortcutScope.Screenshot => file.Screenshot,
            CaptureShortcutScope.Drawing => file.Drawing,
            CaptureShortcutScope.PinToScreen => file.PinToScreen,
            _ => file.Recording,
        };
    }

    private static Dictionary<string, List<string>> Map(CaptureShortcutsFile file, CaptureShortcutScope scope)
    {
        Dictionary<string, List<string>> map = scope switch
        {
            CaptureShortcutScope.Screenshot => file.Screenshot,
            CaptureShortcutScope.Drawing => file.Drawing,
            CaptureShortcutScope.PinToScreen => file.PinToScreen,
            _ => file.Recording,
        };

        if (map == null)
        {
            map = new Dictionary<string, List<string>>();
            switch (scope)
            {
                case CaptureShortcutScope.Screenshot: file.Screenshot = map; break;
                case CaptureShortcutScope.Drawing: file.Drawing = map; break;
                case CaptureShortcutScope.PinToScreen: file.PinToScreen = map; break;
                default: file.Recording = map; break;
            }
        }

        return map;
    }

    private static IReadOnlyList<(CaptureShortcutEntry Entry, CaptureChord[] Chords)> ParsedOf(
        CaptureShortcutScope scope)
    {
        lock (Gate)
        {
            if (Parsed.TryGetValue(scope, out var cached)) return cached;
        }

        var built = new List<(CaptureShortcutEntry, CaptureChord[])>();
        foreach (var entry in Of(scope))
        {
            var chords = new List<CaptureChord>();
            foreach (string text in Chords(scope, entry.Id))
            {
                if (CaptureChord.TryParse(text, out var chord)) chords.Add(chord);
            }

            if (chords.Count > 0) built.Add((entry, chords.ToArray()));
        }

        lock (Gate) Parsed[scope] = built;
        return built;
    }

    private static CaptureShortcutsFile Snapshot()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
        }

        CaptureShortcutsFile loaded;
        try
        {
            loaded = SettingsStore.Read(Anchor).CaptureShortcuts ?? new CaptureShortcutsFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Shortcuts", "读取截图快捷键失败：" + ex.Message);
            loaded = new CaptureShortcutsFile();
        }

        loaded.Screenshot ??= new Dictionary<string, List<string>>();
        loaded.Drawing ??= new Dictionary<string, List<string>>();
        loaded.PinToScreen ??= new Dictionary<string, List<string>>();
        loaded.Recording ??= new Dictionary<string, List<string>>();

        lock (Gate) return _cached = loaded;
    }

    private static void Save(Action<CaptureShortcutsFile> mutate)
    {
        CaptureShortcutsFile next;
        lock (Gate)
        {
            next = Clone(Snapshot());
            mutate(next);
            _cached = Clone(next);
            Parsed.Clear();
        }

        try
        {
            SettingsStore.Modify(Anchor, file =>
            {
                file.CaptureShortcuts ??= new CaptureShortcutsFile();
                var target = file.CaptureShortcuts;
                target.Screenshot = CloneMap(next.Screenshot);
                target.Drawing = CloneMap(next.Drawing);
                target.PinToScreen = CloneMap(next.PinToScreen);
                target.Recording = CloneMap(next.Recording);
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Shortcuts", "保存截图快捷键失败：" + ex.Message);
        }
    }

    private static CaptureShortcutsFile Clone(CaptureShortcutsFile file) => new()
    {
        Screenshot = CloneMap(file.Screenshot),
        Drawing = CloneMap(file.Drawing),
        PinToScreen = CloneMap(file.PinToScreen),
        Recording = CloneMap(file.Recording),
    };

    private static Dictionary<string, List<string>> CloneMap(Dictionary<string, List<string>>? map) =>
        map == null
            ? new Dictionary<string, List<string>>()
            : map.ToDictionary(p => p.Key, p => new List<string>(p.Value ?? new List<string>()),
                StringComparer.Ordinal);
}

/// <summary>一条解析好的组合键（修饰键 + 主键 VK），与上游的便携写法互转。</summary>
internal readonly record struct CaptureChord(ushort Vk, bool Ctrl, bool Shift, bool Alt, bool Win)
{
    private static readonly string[] ModifierOrder = { "Ctrl", "Shift", "Alt", "Win" };

    public bool HasCtrl => Ctrl;
    public bool HasShift => Shift;
    public bool HasAlt => Alt;
    public bool HasWin => Win;

    /// <summary>解析 <c>Ctrl+Shift+Z</c> / <c>Esc</c> / <c>Shift</c> / <c>,</c> / <c>`</c>。</summary>
    public static bool TryParse(string? text, out CaptureChord chord)
    {
        chord = default;

        string[] parts = (text ?? "").Split('+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        bool ctrl = false, shift = false, alt = false, win = false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "alt":
                case "menu":
                    alt = true;
                    break;
                case "win":
                case "meta":
                    win = true;
                    break;
                default:
                    return false;
            }
        }

        string main = parts[^1];
        if (!KeyStroke.TryResolveKey(main, out ushort vk)) return false;

        // 裸修饰键（上游的「按住 Shift / Alt」）不算 Ctrl / Shift 修饰位，主键就是它自己
        if (parts.Length == 1 && vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C)
        {
            chord = new CaptureChord(vk, false, false, false, false);
            return true;
        }

        chord = new CaptureChord(vk, ctrl, shift, alt, win);
        return true;
    }

    /// <summary>命中判定：修饰键精确匹配，左右修饰键视为同一个。</summary>
    public bool Matches(ushort vk, bool ctrl, bool shift, bool alt, bool win)
    {
        if (Normalize(Vk) != Normalize(vk)) return false;
        if (HasCtrl != ctrl || HasShift != shift || HasAlt != alt || HasWin != win) return false;
        return true;
    }

    /// <summary>显示写法：<c>Ctrl+Shift+Z</c> / <c>Esc</c> / <c>Shift</c>。</summary>
    public string Display()
    {
        var parts = new List<string>();
        if (HasCtrl) parts.Add(ModifierOrder[0]);
        if (HasShift) parts.Add(ModifierOrder[1]);
        if (HasAlt) parts.Add(ModifierOrder[2]);
        if (HasWin) parts.Add(ModifierOrder[3]);
        parts.Add(KeyName(Vk));
        return string.Join("+", parts);
    }

    private static ushort Normalize(ushort vk) => vk switch
    {
        0xA1 => 0xA0,
        0xA3 => 0xA2,
        0xA5 => 0xA4,
        0x5C => 0x5B,
        _ => vk,
    };

    private static string KeyName(ushort vk) => vk switch
    {
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= '0' and <= '9' => ((char)vk).ToString(),
        0xA0 or 0xA1 => "Shift",
        0xA2 or 0xA3 => "Ctrl",
        0xA4 or 0xA5 => "Alt",
        0x5B or 0x5C => "Win",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x20 => "Space",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x26 => "Up",
        0x28 => "Down",
        0x25 => "Left",
        0x27 => "Right",
        0xBD => "-",
        0xBB => "=",
        0xDB => "[",
        0xDD => "]",
        0xDC => "\\",
        0xBA => ";",
        0xDE => "'",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        _ => "0x" + vk.ToString("X2"),
    };
}
