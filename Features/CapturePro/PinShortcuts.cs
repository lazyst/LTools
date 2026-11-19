using System;
using System.Collections.Generic;

namespace LTools.Features.CapturePro;

/// <summary>贴图窗口上的键盘动作（照上游 <c>pin_to_screen_shortcuts/*</c>）。</summary>
internal enum PinShortcutAction
{
    None,

    // ---- 内容 ----
    Copy,              // copy_to_clipboard：复制这张图
    CopyOriginal,      // copy_original_content：文字 / 色卡贴图复制原文
    SaveAs,            // save_as_file：另存为
    Ocr,               // show_text_recognition_results：显示识别结果
    Annotate,          // drawing_mode：进入标注

    // ---- 窗口 ----
    Thumbnail,         // thumbnail_mode：缩略图模式
    HideToTop,         // hide_to_top：收到屏幕顶部
    ClickThrough,      // toggle_click_through：点击穿透
    Lock,              // toggle_lock：锁定位置
    AlwaysOnTop,       // always_on_top：置顶
    ShowBorder,        // show_border：显示边框
    ShowShadow,        // show_shadow：显示阴影
    Close,             // close_window：关闭

    // ---- 外观 / 位置 ----
    OpacityUp,         // increase_opacity
    OpacityDown,       // decrease_opacity
    ZoomIn,            // increase_scale
    ZoomOut,           // decrease_scale
    MoveUp,            // move_cursor_up
    MoveDown,          // move_cursor_down
    MoveLeft,          // move_cursor_left
    MoveRight,         // move_cursor_right

    // ---- 批量 ----
    SelectAll,         // batch_select_all
    SaveSelected,      // batch_save_as_file
    CopyFilesSelected, // batch_copy_files_to_clipboard
}

/// <summary>
/// 贴图窗口的快捷键表：默认值逐条照 <c>storage/configurationschema.cpp</c> 的
/// <c>pin_to_screen_shortcuts/*</c>，用户改过的项存在 <c>settings.json</c> 的
/// <c>CaptureShortcuts</c> 段（与截图覆盖层、绘图、录屏同一份）。
/// </summary>
/// <remarks>
/// <para>
/// **只列真能做的事**：上游有、LTools 贴图还没有的能力（旋转 / 翻转 / 还原变换、
/// 动图播放控制、打印、重新调整窗口大小、销毁窗口）不在这里出现，写进
/// <c>docs/CAPTURE_PARITY.md</c> §5 的平台差异——宁可少一条，也不放一条按了没反应的键。
/// </para>
/// <para>
/// 贴图窗是 <c>WS_EX_NOACTIVATE</c>（点一下不抢前台，后续注入的键鼠不落到贴图上），
/// 拿不到 <c>PreviewKeyDown</c>，所以键由全局钩子投递（<see cref="PinShortcutRoute"/>），
/// 作用范围 = **指针停在这张贴图上**。
/// </para>
/// </remarks>
internal static class PinShortcuts
{
    /// <summary>动作 + 它在快捷键表里的位置 + 说明。</summary>
    internal sealed record Entry(PinShortcutAction Action, string Id, string Label);

    /// <summary>全部动作（顺序 = 匹配顺序）。</summary>
    public static readonly IReadOnlyList<Entry> All = new[]
    {
        new Entry(PinShortcutAction.Copy, "copy_to_clipboard", "复制图片"),
        new Entry(PinShortcutAction.CopyOriginal, "copy_original_content", "复制原文"),
        new Entry(PinShortcutAction.SaveAs, "save_as_file", "另存为"),
        new Entry(PinShortcutAction.Ocr, "show_text_recognition_results", "显示识别结果"),
        new Entry(PinShortcutAction.Annotate, "drawing_mode", "标注"),

        new Entry(PinShortcutAction.Thumbnail, "thumbnail_mode", "缩略图模式"),
        new Entry(PinShortcutAction.HideToTop, "hide_to_top", "收到屏幕顶部"),
        new Entry(PinShortcutAction.ClickThrough, "toggle_click_through", "点击穿透"),
        new Entry(PinShortcutAction.Lock, "toggle_lock", "锁定位置"),
        new Entry(PinShortcutAction.AlwaysOnTop, "always_on_top", "置顶"),
        new Entry(PinShortcutAction.ShowBorder, "show_border", "显示边框"),
        new Entry(PinShortcutAction.ShowShadow, "show_shadow", "显示阴影"),
        new Entry(PinShortcutAction.Close, "close_window", "关闭"),

        new Entry(PinShortcutAction.OpacityUp, "increase_opacity", "提高不透明度"),
        new Entry(PinShortcutAction.OpacityDown, "decrease_opacity", "降低不透明度"),
        new Entry(PinShortcutAction.ZoomIn, "increase_scale", "放大"),
        new Entry(PinShortcutAction.ZoomOut, "decrease_scale", "缩小"),
        new Entry(PinShortcutAction.MoveUp, "move_cursor_up", "上移"),
        new Entry(PinShortcutAction.MoveDown, "move_cursor_down", "下移"),
        new Entry(PinShortcutAction.MoveLeft, "move_cursor_left", "左移"),
        new Entry(PinShortcutAction.MoveRight, "move_cursor_right", "右移"),

        new Entry(PinShortcutAction.SelectAll, "batch_select_all", "全选贴图"),
        new Entry(PinShortcutAction.SaveSelected, "batch_save_as_file", "另存所选"),
        new Entry(PinShortcutAction.CopyFilesSelected, "batch_copy_files_to_clipboard", "复制所选为文件"),
    };

    /// <summary>动作的生效组合键文案（菜单 / 悬停提示用；没有就是空串）。</summary>
    public static string ChordsOf(PinShortcutAction action)
    {
        foreach (var entry in All)
        {
            if (entry.Action != action) continue;
            return CaptureShortcutTable.Describe(CaptureShortcutScope.PinToScreen, entry.Id);
        }

        return "";
    }

    /// <summary>「复制图片（Ctrl+C）」这类菜单标题：有快捷键就补在括号里。</summary>
    public static string WithChord(string text, PinShortcutAction action)
    {
        string chords = ChordsOf(action);
        return chords.Length == 0 ? text : text + "（" + chords + "）";
    }

    public static string LabelOf(PinShortcutAction action)
    {
        foreach (var entry in All)
        {
            if (entry.Action == action) return entry.Label;
        }

        return "";
    }

    /// <summary>这个 Id 对应哪个动作（认不出返回 <see cref="PinShortcutAction.None"/>）。</summary>
    public static PinShortcutAction ActionOf(string id)
    {
        foreach (var entry in All)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal)) return entry.Action;
        }

        return PinShortcutAction.None;
    }

    /// <summary>
    /// 在给定的组合键里找第一个动作。修饰键必须**精确匹配**（<c>Ctrl+C</c> 不会被
    /// <c>Ctrl+Shift+C</c> 命中）。
    /// </summary>
    public static PinShortcutAction Match(ushort vk, bool ctrl, bool shift, bool alt, bool win)
    {
        if (vk == 0) return PinShortcutAction.None;

        foreach (var entry in All)
        {
            foreach (string text in CaptureShortcutTable.Chords(CaptureShortcutScope.PinToScreen, entry.Id))
            {
                if (!CaptureChord.TryParse(text, out var chord)) continue;
                if (!chord.Matches(vk, ctrl, shift, alt, win)) continue;
                return entry.Action;
            }
        }

        return PinShortcutAction.None;
    }
}
