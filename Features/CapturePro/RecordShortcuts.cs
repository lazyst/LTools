using System;
using System.Collections.Generic;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>录屏面板上的键盘动作（照上游 <c>screen_recording_shortcuts/*</c> 与 <c>drawing_shortcuts/*</c>）。</summary>
internal enum RecordShortcutAction
{
    None,

    // ---- 录制控制（上游 screen_recording_shortcuts）----
    Export,           // export：结束并导出（面板上的方块按钮）
    ToggleRecording,  // toggle_recording：开始 / 暂停 / 继续
    CopyToClipboard,  // copy_to_clipboard：复制最近一条
    EndRecording,     // end_recording：关闭面板（抬起才触发）
    PreviousArea,     // previous_recording_area：上一块录屏区域
    NextArea,         // next_recording_area：下一块录屏区域

    // ---- 画布历史（上游 canvashistoryshortcuts）----
    Undo,
    Redo,
    ResetCanvas,

    // ---- 绘制工具（上游 drawing_shortcuts）----
    ToolSelect,
    ToolRect,
    ToolArrow,
    ToolPen,
    ToolHighlighter,
    ToolText,
    ToolSerial,
    ToolFilter,
    ToolEraser,
    ToolWatermark,
}

/// <summary>
/// 录屏面板此刻的状态（决定哪些快捷键能触发）：照上游「快捷键等价于点一下那个按钮」，
/// 所以能不能触发完全看对应按钮是不是可点。
/// </summary>
internal sealed class RecordShortcutState
{
    public bool Recording { get; set; }
    public bool Paused { get; set; }

    /// <summary>正在收尾 / 导出中：所有录制控制都先别动。</summary>
    public bool Busy { get; set; }

    /// <summary>采集历史里有录屏（复制 / 裁剪 / 另存为 能在空闲时用）。</summary>
    public bool HasLastRecord { get; set; }

    /// <summary>第二行（导出设置）摆着的时候才让点「上一块 / 下一块区域」（上游同样的门槛）。</summary>
    public bool ExportRowVisible { get; set; }

    /// <summary>录屏区域历史里还有别的可用（至少一条不是当前这块）。</summary>
    public bool AreaHistory { get; set; }

    public bool CanUndo { get; set; }
    public bool CanRedo { get; set; }

    /// <summary>画布可画（没在收尾、没有正在编辑的文字）。</summary>
    public bool DrawingAllowed { get; set; } = true;

    public RecordShortcutState Clone() => (RecordShortcutState)MemberwiseClone();
}

/// <summary>一条快捷键：动作 + 默认组合键（一个动作可以有多组，照上游的字符串列表）+ 说明。</summary>
internal sealed record RecordShortcutEntry(
    RecordShortcutAction Action,
    IReadOnlyList<string> Chords,
    string Label);

/// <summary>
/// 录屏面板的键盘快捷键（照 Snow Shot 的 <c>screenrecordingshortcutcontroller.cpp</c> +
/// <c>drawing_shortcuts</c> / <c>canvashistoryshortcuts.h</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 全是纯函数：<c>--selftest</c> 直接断言「哪个键命中哪个动作」「什么状态下能触发」
/// 「哪些动作在抬起时才触发」，界面只按结果调按钮。
/// </para>
/// <para>
/// **平台差异**：上游用窗口作用域（录屏工具条是普通窗口，焦点在它上面时快捷键才生效）；
/// LTools 的录屏条刻意不抢焦点（<c>WS_EX_NOACTIVATE</c>，录制时用户还在别的程序里操作），
/// 所以同一套语义改由全局键盘钩子投递，并且只在**指针停在录屏条上**时生效——
/// 见 <see cref="RecordShortcutRoute"/>。功能一致、作用范围更保守（不会把别处按的 Esc 吃掉）。
/// </para>
/// </remarks>
internal static class RecordShortcuts
{
    /// <summary>默认组合键与说明（顺序照上游：录制控制 → 画布历史 → 绘制工具）。</summary>
    public static readonly IReadOnlyList<RecordShortcutEntry> All = new[]
    {
        new RecordShortcutEntry(RecordShortcutAction.Export, new[] { "Ctrl+E" }, "结束并导出"),
        new RecordShortcutEntry(RecordShortcutAction.ToggleRecording, new[] { "Ctrl+S" }, "开始 / 暂停 / 继续"),
        new RecordShortcutEntry(RecordShortcutAction.CopyToClipboard, new[] { "Ctrl+C" }, "复制最近一条录屏"),
        new RecordShortcutEntry(RecordShortcutAction.EndRecording, new[] { "Esc" }, "关闭录屏面板"),
        new RecordShortcutEntry(RecordShortcutAction.PreviousArea, new[] { "R" }, "上一块录屏区域"),
        new RecordShortcutEntry(RecordShortcutAction.NextArea, new[] { "Shift+R" }, "下一块录屏区域"),

        new RecordShortcutEntry(RecordShortcutAction.Undo, new[] { "Ctrl+Z" }, "撤销标注"),
        new RecordShortcutEntry(RecordShortcutAction.Redo, new[] { "Ctrl+Y", "Ctrl+Shift+Z" }, "重做标注"),

        new RecordShortcutEntry(RecordShortcutAction.ToolSelect, new[] { "V" }, "选择 / 移动标注"),
        new RecordShortcutEntry(RecordShortcutAction.ToolRect, new[] { "1" }, "矩形标注"),
        new RecordShortcutEntry(RecordShortcutAction.ToolArrow, new[] { "2" }, "箭头标注"),
        new RecordShortcutEntry(RecordShortcutAction.ToolPen, new[] { "3", "P" }, "画笔"),
        new RecordShortcutEntry(RecordShortcutAction.ToolHighlighter, new[] { "4", "H" }, "荧光笔"),
        new RecordShortcutEntry(RecordShortcutAction.ToolText, new[] { "5", "T" }, "文字标注"),
        new RecordShortcutEntry(RecordShortcutAction.ToolSerial, new[] { "6", "N" }, "序号标注"),
        new RecordShortcutEntry(RecordShortcutAction.ToolFilter, new[] { "7", "F" }, "马赛克 / 滤镜"),
        new RecordShortcutEntry(RecordShortcutAction.ToolEraser, new[] { "8", "E" }, "橡皮"),
        new RecordShortcutEntry(RecordShortcutAction.ToolWatermark, new[] { "9" }, "水印"),
    };

    /// <summary>动作的默认组合键文案（按钮悬停提示用；没有就是空串）。</summary>
    public static string ChordsOf(RecordShortcutAction action)
    {
        foreach (var entry in All)
        {
            if (entry.Action != action) continue;
            return string.Join(" / ", entry.Chords);
        }

        return "";
    }

    /// <summary>「复制（Ctrl+C）」这类按钮提示：有快捷键就补在括号里。</summary>
    public static string WithChord(string tip, RecordShortcutAction action)
    {
        string chords = ChordsOf(action);
        return chords.Length == 0 ? tip : tip + "（" + chords + "）";
    }

    public static string LabelOf(RecordShortcutAction action)
    {
        foreach (var entry in All)
        {
            if (entry.Action == action) return entry.Label;
        }

        return "";
    }

    /// <summary>这个工具对应的快捷键动作（按钮悬停提示用）。</summary>
    public static RecordShortcutAction ActionOf(RecordTool tool) => tool switch
    {
        RecordTool.Select => RecordShortcutAction.ToolSelect,
        RecordTool.Rect => RecordShortcutAction.ToolRect,
        RecordTool.Arrow => RecordShortcutAction.ToolArrow,
        RecordTool.Pen => RecordShortcutAction.ToolPen,
        RecordTool.Highlighter => RecordShortcutAction.ToolHighlighter,
        RecordTool.Text => RecordShortcutAction.ToolText,
        RecordTool.Serial => RecordShortcutAction.ToolSerial,
        RecordTool.Filter => RecordShortcutAction.ToolFilter,
        RecordTool.Eraser => RecordShortcutAction.ToolEraser,
        RecordTool.Watermark => RecordShortcutAction.ToolWatermark,
        _ => RecordShortcutAction.None,
    };

    /// <summary>对应哪个绘制工具（不是工具类动作返回 null）。</summary>
    public static RecordTool? ToolOf(RecordShortcutAction action) => action switch
    {
        RecordShortcutAction.ToolSelect => RecordTool.Select,
        RecordShortcutAction.ToolRect => RecordTool.Rect,
        RecordShortcutAction.ToolArrow => RecordTool.Arrow,
        RecordShortcutAction.ToolPen => RecordTool.Pen,
        RecordShortcutAction.ToolHighlighter => RecordTool.Highlighter,
        RecordShortcutAction.ToolText => RecordTool.Text,
        RecordShortcutAction.ToolSerial => RecordTool.Serial,
        RecordShortcutAction.ToolFilter => RecordTool.Filter,
        RecordShortcutAction.ToolEraser => RecordTool.Eraser,
        RecordShortcutAction.ToolWatermark => RecordTool.Watermark,
        _ => null,
    };

    /// <summary>这个动作要在**抬起**时才触发（上游 <c>end_recording</c> 的 Release 触发器）。</summary>
    public static bool FiresOnRelease(RecordShortcutAction action) => action == RecordShortcutAction.EndRecording;

    /// <summary>
    /// 现在能不能触发。照上游「快捷键 = 点按钮」：按钮不可点就不触发，也不吞键。
    /// </summary>
    public static bool CanActivate(RecordShortcutAction action, RecordShortcutState state)
    {
        if (action == RecordShortcutAction.None || state == null) return false;

        if (ToolOf(action) is not null) return state.DrawingAllowed && !state.Busy;

        return action switch
        {
            RecordShortcutAction.Export => (state.Recording || state.Paused) && !state.Busy,
            RecordShortcutAction.ToggleRecording => !state.Busy,
            RecordShortcutAction.CopyToClipboard => (state.Recording || state.Paused || state.HasLastRecord) && !state.Busy,
            RecordShortcutAction.EndRecording => true,
            RecordShortcutAction.PreviousArea => state.ExportRowVisible && state.AreaHistory && !state.Recording
                                                 && !state.Paused && !state.Busy,
            RecordShortcutAction.NextArea => state.ExportRowVisible && state.AreaHistory && !state.Recording
                                             && !state.Paused && !state.Busy,
            RecordShortcutAction.Undo => state.CanUndo && state.DrawingAllowed && !state.Busy,
            RecordShortcutAction.Redo => state.CanRedo && state.DrawingAllowed && !state.Busy,
            RecordShortcutAction.ResetCanvas => state.DrawingAllowed && !state.Busy,
            _ => false,
        };
    }

    /// <summary>
    /// 在给定的组合键里找第一个能触发的动作。修饰键必须**精确匹配**（照
    /// <c>HotkeyRegistry.ModifiersMatch</c>）：<c>Ctrl+C</c> 不会被 <c>Ctrl+Shift+C</c> 命中。
    /// </summary>
    public static RecordShortcutAction Match(ushort vk, bool ctrl, bool shift, bool alt, bool win,
        RecordShortcutState state)
    {
        if (vk == 0) return RecordShortcutAction.None;

        foreach (var entry in All)
        {
            if (!CanActivate(entry.Action, state)) continue;

            foreach (string text in entry.Chords)
            {
                if (!HotkeyChord.TryParse(text, out var chord, out _)) continue;
                if (chord.Vk != vk) continue;

                if (chord.HasCtrl != ctrl || chord.HasShift != shift || chord.HasAlt != alt || chord.HasWin != win)
                    continue;

                return entry.Action;
            }
        }

        return RecordShortcutAction.None;
    }

    /// <summary>动作的默认组合键是不是「裸键」（没有 Ctrl / Shift / Alt / Win）。</summary>
    public static bool IsBare(RecordShortcutAction action)
    {
        foreach (var entry in All)
        {
            if (entry.Action != action || entry.Chords.Count == 0) continue;
            if (!HotkeyChord.TryParse(entry.Chords[0], out var chord, out _)) return false;
            return !chord.HasCtrl && !chord.HasShift && !chord.HasAlt && !chord.HasWin;
        }

        return false;
    }
}
