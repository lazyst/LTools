using System;
using System.Collections.Generic;
using System.Windows.Controls;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「通用 → 截图 Pro」里的四个指针动作下拉与「删除贴图前先确认」。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>screenshot.double-click-action</c> / <c>screenshot.middle-mouse-button-action</c> /
/// <c>pin-to-screen.double-click-action</c> / <c>pin-to-screen.middle-mouse-button-action</c> 四项设置。
/// 选项表在 <see cref="PointerActions"/>（贴图那两个<b>只列已经能做的</b>），
/// 读写与缓存都在 <see cref="PointerSettings"/>，这里只负责把控件和它接起来。
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>把四个下拉与一个勾选框刷成当前设置（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulatePointerActions()
    {
        FillPointerCombo(ScreenshotDoubleClickCombo, PointerActions.OverlayOptions,
            PointerActions.Key(PointerSettings.OverlayDoubleClick));
        FillPointerCombo(ScreenshotMiddleClickCombo, PointerActions.OverlayOptions,
            PointerActions.Key(PointerSettings.OverlayMiddleClick));
        FillPointerCombo(PinDoubleClickCombo, PointerActions.PinOptions,
            PointerActions.Key(PointerSettings.PinDoubleClick));
        FillPointerCombo(PinMiddleClickCombo, PointerActions.PinOptions,
            PointerActions.Key(PointerSettings.PinMiddleClick));

        PinConfirmDestroyBox.IsChecked = PointerSettings.ConfirmBeforeDestroyingWindow;
    }

    private static void FillPointerCombo(ComboBox combo,
        IReadOnlyList<(string Key, string Label)> options, string current)
    {
        combo.Items.Clear();
        foreach (var (key, label) in options)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = key });

        combo.SelectedIndex = -1;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag is string key && key == current)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>下拉里选中的英文键（没选中 = <c>none</c>）。</summary>
    private static string PointerKeyOf(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: string key } ? key : "none";

    private void ScreenshotPointer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        // 两个下拉共用一个处理器：按发事件的控件分开写。
        if (ReferenceEquals(sender, ScreenshotDoubleClickCombo))
            PointerSettings.SetOverlayDoubleClick(PointerKeyOf(ScreenshotDoubleClickCombo));
        else if (ReferenceEquals(sender, ScreenshotMiddleClickCombo))
            PointerSettings.SetOverlayMiddleClick(PointerKeyOf(ScreenshotMiddleClickCombo));
    }

    private void PinPointer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        if (ReferenceEquals(sender, PinDoubleClickCombo))
            PointerSettings.SetPinDoubleClick(PointerKeyOf(PinDoubleClickCombo));
        else if (ReferenceEquals(sender, PinMiddleClickCombo))
            PointerSettings.SetPinMiddleClick(PointerKeyOf(PinMiddleClickCombo));
    }

    private void PinConfirmDestroy_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_initializing) return;
        PointerSettings.SetConfirmBeforeDestroyingWindow(PinConfirmDestroyBox.IsChecked == true);
    }
}
