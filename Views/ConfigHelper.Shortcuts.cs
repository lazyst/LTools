using System;
using System.Linq;
using System.Windows;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「通用 → 截图 Pro」里的两个快捷键入口与摘要。
/// </summary>
/// <remarks>
/// 一个是**全局热键**（<see cref="CaptureShortcutsWindow"/>：截图 / 贴剪贴板这些动作在任意程序里
/// 按什么键，读写 <c>configs/hotkeys.json</c>），另一个是**覆盖层内的快捷键**
/// （<see cref="CaptureKeymapWindow"/>：框选 / 绘图 / 贴图窗 / 录屏面板里面的按键，读写
/// <c>configs/settings.json</c> 的 <c>CaptureShortcuts</c> 段）。两者互不影响。
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>把全局热键摘要刷成当前绑定（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulateCaptureShortcuts()
    {
        CaptureShortcutsSummary.Text = "已绑：" + CaptureShortcuts.Summary(HotkeyRegistry.File);
    }

    private void CaptureShortcuts_Click(object sender, RoutedEventArgs e)
    {
        CaptureShortcutsWindow.Show(this);
        PopulateCaptureShortcuts();
    }

    /// <summary>覆盖层内快捷键的摘要（每组条目数 + 改过几项）。</summary>
    private void PopulateCaptureKeymap()
    {
        int changed = 0;
        foreach (CaptureShortcutScope scope in Enum.GetValues<CaptureShortcutScope>())
            changed += CaptureShortcutTable.CustomCount(scope);

        CaptureKeymapHint.Text =
            $"覆盖层 {CaptureShortcutTable.Of(CaptureShortcutScope.Screenshot).Count()} 项 · " +
            $"绘图 {CaptureShortcutTable.Of(CaptureShortcutScope.Drawing).Count()} 项 · " +
            $"贴图 {CaptureShortcutTable.Of(CaptureShortcutScope.PinToScreen).Count()} 项 · " +
            $"录屏 {CaptureShortcutTable.Of(CaptureShortcutScope.Recording).Count()} 项" +
            (changed == 0 ? "，全部为默认值。" : $"，已改 {changed} 项。");
    }

    private void CaptureKeymap_Click(object sender, RoutedEventArgs e)
    {
        CaptureKeymapWindow.Show(this);
        PopulateCaptureKeymap();
    }
}
