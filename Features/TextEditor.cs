using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// vim 风格文本编辑（对应原版 lib/TextEdit.ahk）。
/// 在 CapsLock 按住 + 工具启用时分发字母/符号键到光标移动/选择/删除/编辑动作。
/// 每个动作用 <see cref="InputHelper"/> 注入按键序列（与 AHK Send 等价）。
/// </summary>
/// <remarks>
/// 约定：本类只处理 keydown（CapsLock 按住期间）；已吞键的 keyup 由 <see cref="AppState.SwallowedVks"/>
/// 在 <see cref="Hooks.KeyboardHook"/> 里一并吞掉，保持事件平衡。
/// </remarks>
internal static class TextEditor
{
    /// <summary>处理一次 CapsLock+组合键。返回 true 表示已吞掉该键。</summary>
    public static bool TryHandle(ushort vk)
    {
        // Alt 状态（a/g/h/;/m// 的 Alt 分支判定）
        bool alt = (Win32.GetAsyncKeyState(Win32.VkMenu) & 0x8000) != 0;

        switch (vk)
        {
            // —— 光标移动 ——
            case 'A': MoveWordLeftOrPageStart(alt); return true;
            case 'S': MoveLeft(); return true;
            case 'D': MoveDown(); return true;
            case 'E': MoveUp(); return true;
            case 'F': MoveRight(); return true;
            case 'G': MoveWordRightOrPageEnd(alt); return true;
            case 'W': MoveHome(); return true;
            case 'R': MoveEnd(); return true;

            // —— 文本选择 ——
            case 'H': SelectWordLeftOrPageStart(alt); return true;
            case 'J': SelectLeft(); return true;
            case 'K': SelectDown(); return true;
            case 'I': SelectUp(); return true;
            case 'L': SelectRight(); return true;
            case Win32.VkOem1: SelectWordRightOrPageEnd(alt); return true; // ;:
            case 'U': SelectHome(); return true;
            case 'O': SelectEnd(); return true;

            // —— 删除操作 ——
            case Win32.VkOemComma: DeleteLeft(); return true;       // ,
            case Win32.VkOemPeriod: DeleteRight(); return true;      // .
            case Win32.VkBack: DeleteLine(); return true;            // Backspace
            case 'M': DeleteToLineBeginningOrPageStart(alt); return true;
            case Win32.VkOem2: DeleteToLineEndOrPageEnd(alt); return true; // /

            // —— 特殊操作 ——
            case Win32.VkReturn: EnterWherever(); return true;
            case Win32.VkRshift: IndexWherever(); return true;
            case 'Z': InputHelper.Combo((ushort)Win32.VkControl, 'Z'); return true; // undo
            case 'Y': InputHelper.Combo((ushort)Win32.VkControl, 'Y'); return true; // redo
            case 'B': InputHelper.Combo((ushort)0x5B, (ushort)Win32.VkTab); return true; // Win+Tab 任务视图

            // —— 独立剪贴板（交工作线程，不阻塞钩子）——
            case 'X': ClipboardIndependent.Cut(); return true;
            case 'C': ClipboardIndependent.Copy(); return true;
            case 'V': ClipboardIndependent.Paste(); return true;

            // —— 符号跳转（交工作线程，不阻塞钩子）——
            case 'P': SymbolJump.Start(); return true;

            default: return false;
        }
    }

    // —— 移动 ——
    static void MoveLeft() => InputHelper.Tap((ushort)Win32.VkLeft);
    static void MoveRight() => InputHelper.Tap((ushort)Win32.VkRight);
    static void MoveUp() => InputHelper.Tap((ushort)Win32.VkUp);
    static void MoveDown() => InputHelper.Tap((ushort)Win32.VkDown);
    static void MoveWordLeft() => InputHelper.Combo((ushort)Win32.VkControl, (ushort)Win32.VkLeft);
    static void MoveWordRight() => InputHelper.Combo((ushort)Win32.VkControl, (ushort)Win32.VkRight);
    static void MoveHome() => InputHelper.Tap((ushort)Win32.VkHome);
    static void MoveEnd() => InputHelper.Tap((ushort)Win32.VkEnd);
    static void MoveToPageBeginning() => InputHelper.ComboReleasingHeldModifiers((ushort)Win32.VkControl, (ushort)Win32.VkHome);
    static void MoveToPageEnd() => InputHelper.ComboReleasingHeldModifiers((ushort)Win32.VkControl, (ushort)Win32.VkEnd);
    static void MoveWordLeftOrPageStart(bool alt) { if (alt) MoveToPageBeginning(); else MoveWordLeft(); }
    static void MoveWordRightOrPageEnd(bool alt) { if (alt) MoveToPageEnd(); else MoveWordRight(); }

    // —— 选择（Shift+ 方向键）——
    static void SelectLeft() => InputHelper.Combo((ushort)Win32.VkShift, (ushort)Win32.VkLeft);
    static void SelectRight() => InputHelper.Combo((ushort)Win32.VkShift, (ushort)Win32.VkRight);
    static void SelectUp() => InputHelper.Combo((ushort)Win32.VkShift, (ushort)Win32.VkUp);
    static void SelectDown() => InputHelper.Combo((ushort)Win32.VkShift, (ushort)Win32.VkDown);
    static void SelectWordLeft() => InputHelper.Combo((ushort)Win32.VkControl, (ushort)Win32.VkShift, (ushort)Win32.VkLeft);
    static void SelectWordRight() => InputHelper.Combo((ushort)Win32.VkControl, (ushort)Win32.VkShift, (ushort)Win32.VkRight);
    static void SelectHome() => InputHelper.Combo((ushort)Win32.VkShift, (ushort)Win32.VkHome);
    static void SelectEnd() => InputHelper.Combo((ushort)Win32.VkShift, (ushort)Win32.VkEnd);
    static void SelectToPageBeginning() => InputHelper.ComboReleasingHeldModifiers((ushort)Win32.VkControl, (ushort)Win32.VkShift, (ushort)Win32.VkHome);
    static void SelectToPageEnd() => InputHelper.ComboReleasingHeldModifiers((ushort)Win32.VkControl, (ushort)Win32.VkShift, (ushort)Win32.VkEnd);
    static void SelectWordLeftOrPageStart(bool alt) { if (alt) SelectToPageBeginning(); else SelectWordLeft(); }
    static void SelectWordRightOrPageEnd(bool alt) { if (alt) SelectToPageEnd(); else SelectWordRight(); }

    // —— 删除 ——
    static void DeleteLeft() => InputHelper.Tap((ushort)Win32.VkBack);
    static void DeleteRight() => InputHelper.Tap((ushort)Win32.VkDelete);
    static void DeleteLine() // {Home}+{End}{Delete}
    {
        InputHelper.Tap((ushort)Win32.VkHome);
        InputHelper.Combo((ushort)Win32.VkShift, (ushort)Win32.VkEnd);
        InputHelper.Tap((ushort)Win32.VkDelete);
    }
    static void DeleteToLineBeginning() // +{Home}{Delete}
    {
        InputHelper.Combo((ushort)Win32.VkShift, (ushort)Win32.VkHome);
        InputHelper.Tap((ushort)Win32.VkDelete);
    }
    static void DeleteToLineEnd() // +{End}{Delete}
    {
        InputHelper.Combo((ushort)Win32.VkShift, (ushort)Win32.VkEnd);
        InputHelper.Tap((ushort)Win32.VkDelete);
    }
    static void DeleteToPageBeginning() // ^+{Home}{Delete}（Alt 物理按下，需临时释放再恢复）
    {
        InputHelper.ComboThenTapReleasingHeldModifiers(
            new ushort[] { (ushort)Win32.VkControl, (ushort)Win32.VkShift, (ushort)Win32.VkHome },
            (ushort)Win32.VkDelete);
    }
    static void DeleteToPageEnd() // ^+{End}{Delete}（Alt 物理按下，需临时释放再恢复）
    {
        InputHelper.ComboThenTapReleasingHeldModifiers(
            new ushort[] { (ushort)Win32.VkControl, (ushort)Win32.VkShift, (ushort)Win32.VkEnd },
            (ushort)Win32.VkDelete);
    }
    static void DeleteToLineBeginningOrPageStart(bool alt) { if (alt) DeleteToPageBeginning(); else DeleteToLineBeginning(); }
    static void DeleteToLineEndOrPageEnd(bool alt) { if (alt) DeleteToPageEnd(); else DeleteToLineEnd(); }

    // —— 插入 ——
    static void EnterWherever() // {End}{Enter}
    {
        InputHelper.Tap((ushort)Win32.VkEnd);
        InputHelper.Tap((ushort)Win32.VkReturn);
    }
    static void IndexWherever() // {Home}{Enter}{Up}
    {
        InputHelper.Tap((ushort)Win32.VkHome);
        InputHelper.Tap((ushort)Win32.VkReturn);
        InputHelper.Tap((ushort)Win32.VkUp);
    }
}
