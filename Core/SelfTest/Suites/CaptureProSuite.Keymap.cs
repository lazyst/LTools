using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 截图 Pro 的快捷键表（P9-f，照上游 <c>screenshot_shortcuts</c> / <c>drawing_shortcuts</c> /
/// <c>pin_to_screen_shortcuts</c> / <c>screen_recording_shortcuts</c>）：默认值、组合键解析与显示、
/// 归一化、覆盖与恢复、精确匹配、工具映射。
/// </summary>
/// <remarks>
/// 全是纯函数（只有「改一项再恢复默认」那组碰磁盘，锚点指到临时目录）。
/// 真窗口里的录入 / 清空 / 恢复默认由仓库外 <c>capture-overlay-smoke keymap</c> 验。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void ShortcutTableCases()
    {
        // ============ 条目：四组数量 + Id 不重 ============
        Check.Equal(36, CaptureShortcutTable.Of(CaptureShortcutScope.Screenshot).Count(),
            "覆盖层 36 项（上游 37 项，upload_to_cloud 不注册）");
        Check.Equal(26, CaptureShortcutTable.Of(CaptureShortcutScope.Drawing).Count(), "绘图 26 项");
        Check.Equal(24, CaptureShortcutTable.Of(CaptureShortcutScope.PinToScreen).Count(),
            "贴图 24 项（上游 39 项，没有对应能力的 15 项不注册）");
        Check.Equal(6, CaptureShortcutTable.Of(CaptureShortcutScope.Recording).Count(), "录屏 6 项");

        foreach (var scope in Enum.GetValues<CaptureShortcutScope>())
        {
            var ids = CaptureShortcutTable.Of(scope).Select(e => e.Id).ToList();
            Check.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count(), $"{scope} 的 Id 不重复");
        }

        // 每条默认值都要解析得出来（写错一个字母，那一项就永远命不中）
        foreach (var entry in CaptureShortcutTable.All)
        {
            foreach (string text in entry.Defaults)
            {
                Check.True(CaptureChord.TryParse(text, out _),
                    $"{entry.Scope}/{entry.Id} 的默认值「{text}」解析得出");
            }
        }

        // ============ 上游有默认值的那几条，逐条核对 ============
        void Default(CaptureShortcutScope scope, string id, params string[] expected)
        {
            var actual = CaptureShortcutTable.Find(scope, id)?.Defaults ?? Array.Empty<string>();
            Check.Equal(string.Join(" / ", expected), string.Join(" / ", actual), $"{id} 的默认值");
        }

        Default(CaptureShortcutScope.Screenshot, "move_tool", "M", "Ctrl+E");
        Default(CaptureShortcutScope.Screenshot, "move_cursor_up", "W", "Up");
        Default(CaptureShortcutScope.Screenshot, "move_cursor_down", "S", "Down");
        Default(CaptureShortcutScope.Screenshot, "move_cursor_left", "A", "Left");
        Default(CaptureShortcutScope.Screenshot, "move_cursor_right", "D", "Right");
        Default(CaptureShortcutScope.Screenshot, "move_entire_selection", "Space");
        Default(CaptureShortcutScope.Screenshot, "keep_selection_width_and_height_consistent", "Shift");
        Default(CaptureShortcutScope.Screenshot, "selection_aspect_ratio_snap", "Q");
        Default(CaptureShortcutScope.Screenshot, "switch_selection_between_window_and_window_sub_element", "Tab");
        Default(CaptureShortcutScope.Screenshot, "next_selection_type", "Ctrl+Tab");
        Default(CaptureShortcutScope.Screenshot, "previous_selection_type", "Ctrl+Shift+Tab");
        Default(CaptureShortcutScope.Screenshot, "previous_screenshot_history", ",");
        Default(CaptureShortcutScope.Screenshot, "next_screenshot_history", ".");
        Default(CaptureShortcutScope.Screenshot, "select_previously_selected_area", "R");
        Default(CaptureShortcutScope.Screenshot, "select_next_previously_selected_area", "Shift+R");
        Default(CaptureShortcutScope.Screenshot, "duplicate_latest_region", "R");
        Default(CaptureShortcutScope.Screenshot, "toggle_cursor_visibility", "`");
        Default(CaptureShortcutScope.Screenshot, "recapture", "Alt+R");
        Default(CaptureShortcutScope.Screenshot, "toggle_coordinate_mode", "Shift+P");
        Default(CaptureShortcutScope.Screenshot, "toggle_guides", "Alt");
        Default(CaptureShortcutScope.Screenshot, "copy_color", "C");
        Default(CaptureShortcutScope.Screenshot, "table_recognition", "Ctrl+X");
        Default(CaptureShortcutScope.Screenshot, "qr_code_recognition", "Ctrl+Q");
        Default(CaptureShortcutScope.Screenshot, "video_recording", "Ctrl+R");
        Default(CaptureShortcutScope.Screenshot, "text_recognition", "Ctrl+D");
        Default(CaptureShortcutScope.Screenshot, "text_translation", "Ctrl+T");
        Default(CaptureShortcutScope.Screenshot, "scrolling_screenshot", "L");
        Default(CaptureShortcutScope.Screenshot, "quick_save", "Ctrl+Shift+S");
        Default(CaptureShortcutScope.Screenshot, "print", "Ctrl+P");
        Default(CaptureShortcutScope.Screenshot, "save_as_file", "Ctrl+S");
        Default(CaptureShortcutScope.Screenshot, "pin_to_screen", "Ctrl+F");
        Default(CaptureShortcutScope.Screenshot, "pin_window_to_screen", "Alt+W");
        Default(CaptureShortcutScope.Screenshot, "cancel_screenshot", "Esc");
        Default(CaptureShortcutScope.Screenshot, "copy_to_clipboard", "Ctrl+C", "Enter");
        Default(CaptureShortcutScope.Screenshot, "undo", "Ctrl+Z");
        Default(CaptureShortcutScope.Screenshot, "redo", "Ctrl+Y");

        Default(CaptureShortcutScope.Drawing, "select", "V", "Q");
        Default(CaptureShortcutScope.Drawing, "shape", "1", "R");
        Default(CaptureShortcutScope.Drawing, "arrow", "2", "A");
        Default(CaptureShortcutScope.Drawing, "brush", "3", "P");
        Default(CaptureShortcutScope.Drawing, "highlight", "4", "H");
        Default(CaptureShortcutScope.Drawing, "text", "5", "T");
        Default(CaptureShortcutScope.Drawing, "serial_number", "6", "N");
        Default(CaptureShortcutScope.Drawing, "filter", "7", "F");
        Default(CaptureShortcutScope.Drawing, "eraser", "8", "E");
        Default(CaptureShortcutScope.Drawing, "watermark", "9", "W");
        Default(CaptureShortcutScope.Drawing, "reset_canvas", "Ctrl+Shift+Z");

        Default(CaptureShortcutScope.PinToScreen, "copy_to_clipboard", "Ctrl+C");
        Default(CaptureShortcutScope.PinToScreen, "copy_original_content", "Ctrl+Shift+C");
        Default(CaptureShortcutScope.PinToScreen, "save_as_file", "Ctrl+S");
        Default(CaptureShortcutScope.PinToScreen, "show_text_recognition_results", "Ctrl+D");
        Default(CaptureShortcutScope.PinToScreen, "drawing_mode", "Space");
        Default(CaptureShortcutScope.PinToScreen, "thumbnail_mode", "R");
        Default(CaptureShortcutScope.PinToScreen, "hide_to_top", "H");
        Default(CaptureShortcutScope.PinToScreen, "toggle_click_through", "Ctrl+M");
        Default(CaptureShortcutScope.PinToScreen, "toggle_lock", "L");
        Default(CaptureShortcutScope.PinToScreen, "always_on_top", "T");
        Default(CaptureShortcutScope.PinToScreen, "show_border", "B");
        Default(CaptureShortcutScope.PinToScreen, "show_shadow", "Y");
        Default(CaptureShortcutScope.PinToScreen, "close_window", "Esc");
        Default(CaptureShortcutScope.PinToScreen, "increase_opacity", "]");
        Default(CaptureShortcutScope.PinToScreen, "decrease_opacity", "[");
        Default(CaptureShortcutScope.PinToScreen, "increase_scale", ".");
        Default(CaptureShortcutScope.PinToScreen, "decrease_scale", ",");
        Default(CaptureShortcutScope.PinToScreen, "move_cursor_up", "W", "Up");
        Default(CaptureShortcutScope.PinToScreen, "move_cursor_down", "S", "Down");
        Default(CaptureShortcutScope.PinToScreen, "move_cursor_left", "A", "Left");
        Default(CaptureShortcutScope.PinToScreen, "move_cursor_right", "D", "Right");
        Default(CaptureShortcutScope.PinToScreen, "batch_select_all", "Ctrl+Alt+A");
        Default(CaptureShortcutScope.PinToScreen, "batch_save_as_file", "Ctrl+Alt+S");
        Default(CaptureShortcutScope.PinToScreen, "batch_copy_files_to_clipboard", "Ctrl+Alt+C");

        // 上游有、LTools 贴图还没有的能力不注册（否则就是按了没反应的假条目）
        foreach (string dropped in new[]
                 {
                     "print", "batch_image_stitching", "toggle_playback", "next_frame", "previous_frame",
                     "decrease_playback_speed", "increase_playback_speed", "reset_playback_speed",
                     "resize_window", "destroy_window", "rotate_clockwise", "rotate_counterclockwise",
                     "flip_horizontal", "flip_vertical", "reset_transform",
                 })
        {
            Check.True(CaptureShortcutTable.Find(CaptureShortcutScope.PinToScreen, dropped) == null,
                $"贴图表里没有 {dropped}（LTools 贴图没有这个能力）");
        }

        Default(CaptureShortcutScope.Recording, "export", "Ctrl+E");        Default(CaptureShortcutScope.Recording, "toggle_recording", "Ctrl+S");
        Default(CaptureShortcutScope.Recording, "copy_to_clipboard", "Ctrl+C");
        Default(CaptureShortcutScope.Recording, "end_recording", "Esc");
        Default(CaptureShortcutScope.Recording, "previous_recording_area", "R");
        Default(CaptureShortcutScope.Recording, "next_recording_area", "Shift+R");

        // 绘图工具里每个字母只能属于一个工具（同一作用域内按出来得是确定的那个）
        var letters = new List<(string Chord, string Id)>();
        foreach (var entry in CaptureShortcutTable.Of(CaptureShortcutScope.Drawing))
        {
            foreach (string text in entry.Defaults)
            {
                if (!CaptureChord.TryParse(text, out var chord)) continue;
                if (chord.HasCtrl || chord.HasShift || chord.HasAlt || chord.HasWin) continue;
                letters.Add((text, entry.Id));
            }
        }

        var duplicate = letters.GroupBy(l => l.Chord, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        Check.True(duplicate == null,
            duplicate == null
                ? "绘图工具的默认字母互不冲突"
                : $"绘图工具的默认字母冲突：{duplicate.Key} = {string.Join(" / ", duplicate.Select(l => l.Id))}");

        // ============ 组合键解析 / 显示 ============
        Check.True(CaptureChord.TryParse("Ctrl+Shift+Z", out var combo), "解析 Ctrl+Shift+Z");
        Check.Equal((ushort)'Z', combo.Vk, "主键是 Z");
        Check.True(combo.HasCtrl && combo.HasShift && !combo.HasAlt && !combo.HasWin, "三个修饰位对");
        Check.Equal("Ctrl+Shift+Z", combo.Display(), "显示回 Ctrl+Shift+Z");

        Check.True(CaptureChord.TryParse("shift", out var bare), "解析裸修饰键 shift");
        Check.Equal((ushort)0xA0, bare.Vk, "裸 Shift 就是 0xA0");
        Check.False(bare.HasCtrl || bare.HasShift || bare.HasAlt || bare.HasWin, "裸修饰键不带修饰位");
        Check.Equal("Shift", bare.Display(), "显示成 Shift");

        Check.True(CaptureChord.TryParse(",", out var comma), "解析逗号");
        Check.Equal((ushort)0xBC, comma.Vk, "逗号 = 0xBC");
        Check.Equal(",", comma.Display(), "逗号显示回 ,");

        Check.True(CaptureChord.TryParse("`", out var grave), "解析反引号");
        Check.Equal((ushort)0xC0, grave.Vk, "反引号 = 0xC0");

        Check.True(CaptureChord.TryParse("Return", out var ret), "Return 是 Enter 的别名");
        Check.Equal((ushort)0x0D, ret.Vk, "Return = 0x0D");
        Check.Equal("Enter", ret.Display(), "统一显示成 Enter");

        Check.False(CaptureChord.TryParse("", out _), "空串解析失败");
        Check.False(CaptureChord.TryParse("Ctrl+不是键", out _), "认不出的键名解析失败");

        // 左右修饰键归一：录到右 Shift 也能命中表里的裸 Shift
        Check.True(CaptureChord.TryParse("Shift", out var leftShift), "裸 Shift");
        Check.True(leftShift.Matches(0xA1, false, false, false, false), "右 Shift 也算裸 Shift");

        // ============ 归一化 ============
        var normalized = CaptureShortcutTable.Normalize(new[] { "ctrl+shift+z", "Ctrl+Shift+Z", " ", "F9" });
        Check.Equal("Ctrl+Shift+Z / F9", string.Join(" / ", normalized), "归一化去重 + 统一写法");

        // ============ 覆盖与恢复（锚点指到临时目录）============
        string dir = Path.Combine(Path.GetTempPath(), "ltools-selftest-keymap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "configs"));
        CaptureShortcutTable.AnchorOverride = dir;
        CaptureShortcutTable.Invalidate();

        try
        {
            Check.True(CaptureShortcutTable.IsDefault(CaptureShortcutScope.Drawing, "text"), "默认时 text 算默认值");
            Check.Equal("5 / T", CaptureShortcutTable.Describe(CaptureShortcutScope.Drawing, "text"), "默认显示 5 / T");

            CaptureShortcutTable.Set(CaptureShortcutScope.Drawing, "text", new[] { "ctrl+shift+f9" });
            Check.False(CaptureShortcutTable.IsDefault(CaptureShortcutScope.Drawing, "text"), "改过之后不算默认值");
            Check.Equal("Ctrl+Shift+F9", CaptureShortcutTable.Describe(CaptureShortcutScope.Drawing, "text"),
                "改过的值按 canonical 显示");
            Check.Equal(1, CaptureShortcutTable.CustomCount(CaptureShortcutScope.Drawing), "改过的项数 = 1");

            Check.True(CaptureShortcutTable.Match(CaptureShortcutScope.Drawing, 0x78, true, true, false, false,
                out string hit), "Ctrl+Shift+F9 命中");
            Check.Equal("text", hit, "命中的是 text");

            Check.False(CaptureShortcutTable.Match(CaptureShortcutScope.Drawing, 0x78, true, false, false, false,
                out _), "Ctrl+F9 不命中（修饰键精确匹配）");

            CaptureShortcutTable.Reset(CaptureShortcutScope.Drawing, "text");
            Check.True(CaptureShortcutTable.IsDefault(CaptureShortcutScope.Drawing, "text"), "恢复默认后又是默认值");
            Check.Equal("5 / T", CaptureShortcutTable.Describe(CaptureShortcutScope.Drawing, "text"), "恢复默认回到 5 / T");

            // 空列表 = 明确「不绑」，不是回落默认
            CaptureShortcutTable.Set(CaptureShortcutScope.Drawing, "text", Array.Empty<string>());
            Check.False(CaptureShortcutTable.IsDefault(CaptureShortcutScope.Drawing, "text"), "清空也是「改过」");
            Check.Equal(0, CaptureShortcutTable.Chords(CaptureShortcutScope.Drawing, "text").Count, "清空后没有组合键");
            Check.False(CaptureShortcutTable.Match(CaptureShortcutScope.Drawing, 0x54, false, false, false, false, out _),
                "清空后按 T 不再换工具");

            CaptureShortcutTable.ResetAll(CaptureShortcutScope.Drawing);
            Check.Equal(0, CaptureShortcutTable.CustomCount(CaptureShortcutScope.Drawing), "整组恢复默认");
        }
        finally
        {
            CaptureShortcutTable.AnchorOverride = null;
            CaptureShortcutTable.Invalidate();
            try { Directory.Delete(dir, true); } catch (Exception) { /* 删不掉不影响结论 */ }
        }

        // ============ 精确匹配（默认值，不碰磁盘）============
        Check.True(CaptureShortcutTable.Match(CaptureShortcutScope.Screenshot, 0x43, true, false, false, false,
            out string copy), "Ctrl+C 命中");
        Check.Equal("copy_to_clipboard", copy, "Ctrl+C = 复制");
        Check.True(CaptureShortcutTable.Match(CaptureShortcutScope.Screenshot, 0x0D, false, false, false, false,
            out string enter), "Enter 也命中");
        Check.Equal("copy_to_clipboard", enter, "Enter = 复制");
        Check.False(CaptureShortcutTable.Match(CaptureShortcutScope.Screenshot, 0x43, true, true, false, false, out _),
            "Ctrl+Shift+C 不命中 Ctrl+C");

        Check.True(CaptureShortcutTable.Match(CaptureShortcutScope.Recording, 0x53, true, false, false, false,
            out string toggle), "Ctrl+S 命中录屏的开关");
        Check.Equal("toggle_recording", toggle, "Ctrl+S = 开始 / 暂停");

        Check.False(CaptureShortcutTable.Match(CaptureShortcutScope.Screenshot, 0, false, false, false, false, out _),
            "vk = 0 不命中");

        // ============ 裸修饰键 ============
        Check.True(CaptureShortcutTable.BindsBareModifier(CaptureShortcutScope.Screenshot, "toggle_guides", 0xA4),
            "参考线还绑着裸 Alt");
        Check.False(CaptureShortcutTable.BindsBareModifier(CaptureShortcutScope.Screenshot, "toggle_guides", 0xA0),
            "参考线没绑裸 Shift");
        Check.True(CaptureShortcutTable.BindsBareModifier(CaptureShortcutScope.Screenshot,
            "keep_selection_width_and_height_consistent", 0xA1), "锁正方形还绑着裸 Shift（右 Shift 也算）");

        // ============ 工具映射 ============
        foreach (CaptureTool tool in Enum.GetValues<CaptureTool>())
        {
            string? id = CaptureShortcutTable.IdOfTool(tool);
            Check.True(id != null, $"{tool} 有对应的绘图快捷键 Id");
            if (id == null) continue;

            Check.Equal(tool, CaptureShortcutTable.ToolOf(id), $"{tool} ↔ {id} 双向对得上");
            Check.True(CaptureShortcutTable.Find(CaptureShortcutScope.Drawing, id) != null,
                $"{id} 在绘图表里有条目");
        }

        Check.True(CaptureShortcutTable.ToolOf("no_such_tool") == null, "认不出的 Id 查不到工具");

        // ============ 录屏面板读同一份表 ============
        Check.Equal("Ctrl+E", RecordShortcuts.ChordsOf(RecordShortcutAction.Export), "录屏导出 = Ctrl+E");
        Check.Equal("Ctrl+Z", RecordShortcuts.ChordsOf(RecordShortcutAction.Undo), "撤销读的是覆盖层那张表");
        Check.Equal("5 / T", RecordShortcuts.ChordsOf(RecordShortcutAction.ToolText), "文字标注 = 5 / T");
        Check.Equal("3 / P", RecordShortcuts.ChordsOf(RecordShortcutAction.ToolPen), "画笔 = 3 / P");

        var idle = new RecordShortcutState();
        Check.Equal(RecordShortcutAction.ToggleRecording,
            RecordShortcuts.Match(0x53, true, false, false, false, idle), "录屏 Ctrl+S = 开始");
        Check.Equal(RecordShortcutAction.None,
            RecordShortcuts.Match(0x53, true, true, false, false, idle), "录屏 Ctrl+Shift+S 不命中");
        Check.Equal(RecordShortcutAction.ToolSelect,
            RecordShortcuts.Match(0x56, false, false, false, false, idle), "录屏 V = 选择工具");

        // ============ 贴图窗口动作表 ============
        Check.Equal(24, PinShortcuts.All.Count, "贴图动作 24 个");
        foreach (var entry in PinShortcuts.All)
        {
            Check.Equal(entry.Action, PinShortcuts.ActionOf(entry.Id), $"{entry.Id} ↔ 动作对得上");
            Check.True(CaptureShortcutTable.Chords(CaptureShortcutScope.PinToScreen, entry.Id).Count > 0,
                $"{entry.Id} 有生效的组合键");
        }

        Check.Equal(PinShortcutAction.Copy, PinShortcuts.Match(0x43, true, false, false, false), "Ctrl+C = 复制");
        Check.Equal(PinShortcutAction.CopyOriginal,
            PinShortcuts.Match(0x43, true, true, false, false), "Ctrl+Shift+C = 复制原文");
        Check.Equal(PinShortcutAction.None, PinShortcuts.Match(0x43, false, false, false, false),
            "裸 C 不是贴图动作（Ctrl+C 才复制）");
        Check.Equal(PinShortcutAction.ShowBorder, PinShortcuts.Match(0x42, false, false, false, false), "B = 边框");
        Check.Equal(PinShortcutAction.Close, PinShortcuts.Match(0x1B, false, false, false, false), "Esc = 关闭");
        Check.Equal(PinShortcutAction.MoveRight, PinShortcuts.Match(0x27, false, false, false, false), "Right = 右移");
        Check.Equal(PinShortcutAction.SelectAll, PinShortcuts.Match(0x41, true, false, true, false),
            "Ctrl+Alt+A = 全选贴图");
        Check.Equal(PinShortcutAction.None, PinShortcuts.Match(0, false, false, false, false), "vk = 0 不命中");
        Check.Equal("Ctrl+C", PinShortcuts.ChordsOf(PinShortcutAction.Copy), "复制 = Ctrl+C");
        Check.Equal("复制图片（Ctrl+C）", PinShortcuts.WithChord("复制图片", PinShortcutAction.Copy),
            "WithChord 拼出括号里的组合键");
        Check.Equal("复制图片", PinShortcuts.WithChord("复制图片", PinShortcutAction.None), "没有动作就不加括号");

        // ============ 提示文案 ============
        Check.Equal("复制（Ctrl+C / Enter）",
            CaptureShortcutTable.WithChord("复制", CaptureShortcutScope.Screenshot, "copy_to_clipboard"),
            "WithChord 拼出括号里的组合键");
        Check.Equal("关闭", CaptureShortcutTable.WithChord("关闭", CaptureShortcutScope.Screenshot, "no_such_id"),
            "没绑定的项不加括号");
    }
}
