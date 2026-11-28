using System;
using System.Windows;
using LTools.Config;
using LTools.Core;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 设置「热键 → 键鼠宏」子页：录制 / 停止运行两个全局热键。
/// </summary>
/// <remarks>
/// <para>
/// 这两个键以前在「键鼠宏 → 设置」里，2026-11-28 随「热键」分组一起收归到这里；
/// 唯一写入口仍是 <c>configs/macros.json</c>（<see cref="MacroSettings.SaveAndReload"/> → <c>MacroFileStore</c>），
/// 「键鼠宏 → 设置」只留一个指路按钮，不再放第二份录入框。
/// </para>
/// <para>
/// 停止运行热键与宏回放 / 动作自动化共用同一个值（见 <see cref="MacroSettings.PlaybackStopHotkey"/>），
/// 文案一律叫「停止运行热键」，别再写「停止回放热键」。
/// </para>
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>挂上两个热键录入框（构造期调一次；录到即写盘）。</summary>
    private void AttachMacroHotkeys()
    {
        HotkeyCapture.Attach(MacroRecordHotkeyBox, raw => SaveMacroHotkey(m => m.RecordHotkey = raw));
        HotkeyCapture.Attach(MacroPlayHotkeyBox, raw => SaveMacroHotkey(m => m.PlaybackStopHotkey = raw));
    }

    /// <summary>回填两个录入框（进子页时调用）。只读 <c>macros.json</c>，不扫动作目录。</summary>
    private void PopulateMacroHotkeys()
    {
        MacroConfig m;
        try
        {
            m = MacroFileStore.Read(ConfigPath).Macros ?? new MacroConfig();
        }
        catch (Exception ex)
        {
            CrashLog.Write("ConfigHelper.PopulateMacroHotkeys", ex);
            return;
        }

        HotkeyCapture.SetHotkey(MacroRecordHotkeyBox, m.RecordHotkey);
        HotkeyCapture.SetHotkey(MacroPlayHotkeyBox, m.PlaybackStopHotkey);
    }

    /// <summary>只写 <c>configs/macros.json</c> 并立刻重载运行期缓存（钩子下一次按键就是新值）。</summary>
    private void SaveMacroHotkey(Action<MacroConfig> change)
    {
        try
        {
            MacroSettings.SaveAndReload(change);
        }
        catch (Exception ex)
        {
            CrashLog.Write("ConfigHelper.MacroHotkey", ex);
            ToastService.Show("保存热键失败：" + ex.Message, "热键", ToastService.LongMs, ToastKind.error);
        }
    }
}
