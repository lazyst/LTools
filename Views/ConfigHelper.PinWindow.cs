using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「通用 → 截图 Pro · 贴图窗口」：新贴图的边框 / 阴影默认开关、六个状态色、
/// 滚轮缩放的锚点与两个步长、窗口按钮与关闭确认。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>pin_to_screen/*</c> 那十二项，读写与归一化全在 <see cref="PinSettings"/>
/// （落 <c>configs/settings.json</c> 的 <c>PinToScreen</c> 段）。
/// 「这张贴图自己的边框 / 阴影」是右键菜单里的开关，存进 <c>pins/pins.json</c>，不在这张卡片上。
/// </para>
/// <para>
/// 颜色按钮复用 <see cref="CaptureColorDialog"/>（与截图界面那六个色块同一个取色框），
/// 色值统一按 <c>#RRGGBBAA</c> 存，认 6 位简写。
/// </para>
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>把这一卡片刷成当前设置（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulatePinWindow()
    {
        PinShowBorderBox.IsChecked = PinSettings.ShowBorderByDefault;
        PinShowShadowBox.IsChecked = PinSettings.ShowShadowByDefault;
        PinWindowButtonsBox.IsChecked = PinSettings.ShowWindowButtons;
        PinConfirmCloseBox.IsChecked = PinSettings.ConfirmBeforeClosingWindow;
        PinAutoOcrBox.IsChecked = PinSettings.AutomaticTextRecognition;
        PinAutoResizeBox.IsChecked = PinSettings.AutoResizeWindow;
        PinQuickAnnotateBox.IsChecked = PinSettings.QuickAnnotationForSelectedText;

        FillPinZoomModes();
        FillPinTextSelectionModes();
        FillPinDuplicateActions();

        FillPinStepCombo(PinZoomStepCombo, PinSettings.ZoomStepPercent, new[] { 5, 10, 15, 20, 25, 50 });
        FillPinStepCombo(PinOpacityStepCombo, PinSettings.OpacityStepPercent, new[] { 1, 5, 10, 15, 20, 25 });

        ShowColor(PinBorderColorSwatch, PinBorderColorText, PinSettings.BorderColor);
        ShowColor(PinBorderActiveColorSwatch, PinBorderActiveColorText, PinSettings.BorderActiveColor);
        ShowColor(PinLockedBorderColorSwatch, PinLockedBorderColorText, PinSettings.LockedBorderColor);
        ShowColor(PinShadowColorSwatch, PinShadowColorText, PinSettings.ShadowColor);
        ShowColor(PinShadowActiveColorSwatch, PinShadowActiveColorText, PinSettings.ShadowActiveColor);
        ShowColor(PinLockedShadowColorSwatch, PinLockedShadowColorText, PinSettings.LockedShadowColor);
    }

    private void FillPinZoomModes()
    {
        if (PinWheelModeCombo.Items.Count != PinSettings.ZoomModes.Length)
        {
            PinWheelModeCombo.Items.Clear();
            foreach (var (key, label) in PinSettings.ZoomModes)
                PinWheelModeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        }

        string current = PinSettings.ZoomMode;
        PinWheelModeCombo.SelectedIndex = 0;
        for (int i = 0; i < PinWheelModeCombo.Items.Count; i++)
        {
            if (PinWheelModeCombo.Items[i] is ComboBoxItem { Tag: string key } && key == current)
            {
                PinWheelModeCombo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>「识别结果可划选」两个选项（键 = 上游英文键）。</summary>
    private void FillPinTextSelectionModes()
    {
        if (PinTextSelectionCombo.Items.Count != PinSettings.TextSelectionModes.Length)
        {
            PinTextSelectionCombo.Items.Clear();
            foreach (var (key, label) in PinSettings.TextSelectionModes)
                PinTextSelectionCombo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        }

        string current = PinSettings.TextSelectionMode;
        PinTextSelectionCombo.SelectedIndex = 0;
        for (int i = 0; i < PinTextSelectionCombo.Items.Count; i++)
        {
            if (PinTextSelectionCombo.Items[i] is ComboBoxItem { Tag: string key } && key == current)
            {
                PinTextSelectionCombo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>「贴到重复内容时」四个选项（键 = 上游英文键）。</summary>
    private void FillPinDuplicateActions()
    {
        if (PinDuplicateCombo.Items.Count != PinSettings.DuplicateActions.Length)
        {
            PinDuplicateCombo.Items.Clear();
            foreach (var (key, label) in PinSettings.DuplicateActions)
                PinDuplicateCombo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        }

        string current = PinSettings.DuplicateMode;
        PinDuplicateCombo.SelectedIndex = 0;
        for (int i = 0; i < PinDuplicateCombo.Items.Count; i++)
        {
            if (PinDuplicateCombo.Items[i] is ComboBoxItem { Tag: string key } && key == current)
            {
                PinDuplicateCombo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>
    /// 步长下拉：预设几档，当前值不在预设里就补一项（照 <c>FillPointerCombo</c> 同一套写法），
    /// 免得手改过的 7% 在界面上显示成空白。
    /// </summary>
    private static void FillPinStepCombo(ComboBox combo, int current, IReadOnlyList<int> presets)
    {
        combo.Items.Clear();
        var seen = new HashSet<int>();

        foreach (int step in presets)
        {
            seen.Add(step);
            combo.Items.Add(new ComboBoxItem { Content = step + "%", Tag = step });
        }

        if (!seen.Contains(current))
            combo.Items.Add(new ComboBoxItem { Content = current + "%", Tag = current });

        combo.SelectedIndex = 0;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem { Tag: int step } && step == current)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private static int PinStepOf(ComboBox combo, int fallback) =>
        combo.SelectedItem is ComboBoxItem { Tag: int step } ? step : fallback;

    // ============ 勾选 / 下拉 ============

    private void PinWindowToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        PinSettings.SetShowBorderByDefault(PinShowBorderBox.IsChecked == true);
        PinSettings.SetShowShadowByDefault(PinShowShadowBox.IsChecked == true);
        PinSettings.SetShowWindowButtons(PinWindowButtonsBox.IsChecked == true);
        PinSettings.SetConfirmBeforeClosingWindow(PinConfirmCloseBox.IsChecked == true);

        bool recognition = PinSettings.AutomaticTextRecognition;
        bool autoResize = PinSettings.AutoResizeWindow;
        bool quickAnnotate = PinSettings.QuickAnnotationForSelectedText;
        PinSettings.SetAutomaticTextRecognition(PinAutoOcrBox.IsChecked == true);
        PinSettings.SetAutoResizeWindow(PinAutoResizeBox.IsChecked == true);
        PinSettings.SetQuickAnnotationForSelectedText(PinQuickAnnotateBox.IsChecked == true);

        // 已经贴着的图按新设置走：打开自动识别 = 现在就补一次；可划选 / 快速标注开关立刻生效。
        if (recognition != PinSettings.AutomaticTextRecognition ||
            autoResize != PinSettings.AutoResizeWindow ||
            quickAnnotate != PinSettings.QuickAnnotationForSelectedText)
        {
            CaptureWindows.Open(CaptureWindows.PinRefresh);
        }
    }

    private void PinWindowSelect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        if (ReferenceEquals(sender, PinWheelModeCombo))
        {
            string key = PinWheelModeCombo.SelectedItem is ComboBoxItem { Tag: string tag }
                ? tag
                : PinSettings.ZoomMousePosition;
            PinSettings.SetZoomMode(key);
        }
        else if (ReferenceEquals(sender, PinZoomStepCombo))
        {
            PinSettings.SetZoomStepPercent(PinStepOf(PinZoomStepCombo, PinSettings.DefaultZoomStep));
        }
        else if (ReferenceEquals(sender, PinOpacityStepCombo))
        {
            PinSettings.SetOpacityStepPercent(PinStepOf(PinOpacityStepCombo, PinSettings.DefaultOpacityStep));
        }
        else if (ReferenceEquals(sender, PinTextSelectionCombo))
        {
            string key = PinTextSelectionCombo.SelectedItem is ComboBoxItem { Tag: string tag }
                ? tag
                : PinSettings.TextSelectionAlways;
            PinSettings.SetTextSelectionMode(key);
            CaptureWindows.Open(CaptureWindows.PinRefresh);
        }
        else if (ReferenceEquals(sender, PinDuplicateCombo))
        {
            string key = PinDuplicateCombo.SelectedItem is ComboBoxItem { Tag: string tag }
                ? tag
                : PinSettings.DuplicateShakeWindow;
            PinSettings.SetDuplicateMode(key);
        }
    }

    // ============ 六个颜色按钮 ============

    private void PinBorderColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(PinSettings.BorderColor, PinSettings.SetBorderColor,
            PinBorderColorSwatch, PinBorderColorText);

    private void PinBorderActiveColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(PinSettings.BorderActiveColor, PinSettings.SetBorderActiveColor,
            PinBorderActiveColorSwatch, PinBorderActiveColorText);

    private void PinLockedBorderColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(PinSettings.LockedBorderColor, PinSettings.SetLockedBorderColor,
            PinLockedBorderColorSwatch, PinLockedBorderColorText);

    private void PinShadowColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(PinSettings.ShadowColor, PinSettings.SetShadowColor,
            PinShadowColorSwatch, PinShadowColorText);

    private void PinShadowActiveColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(PinSettings.ShadowActiveColor, PinSettings.SetShadowActiveColor,
            PinShadowActiveColorSwatch, PinShadowActiveColorText);

    private void PinLockedShadowColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(PinSettings.LockedShadowColor, PinSettings.SetLockedShadowColor,
            PinLockedShadowColorSwatch, PinLockedShadowColorText);
}
