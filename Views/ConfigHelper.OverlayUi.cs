using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「通用 → 截图 Pro · 界面与参考线」：工具条尺寸、过渡动画、信息条单位、
/// 取色器的显示时机 / 格式 / 坐标口径，以及选区框、压暗、三条参考线的颜色与提示条不透明度。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshot_ui/*</c> 那 18 项，读写与归一化全在 <see cref="OverlayUiSettings"/>
/// （落 <c>configs/settings.json</c> 的 <c>Screenshot</c> 段）。这里的每个控件只做「回显 + 落盘」，
/// 不自己存一份，也不在打开覆盖层时再读磁盘（覆盖层用同一份进程内缓存）。
/// </para>
/// <para>
/// 颜色按钮复用 <see cref="CaptureColorDialog"/>（与颜色预设同一个取色框），
/// 六个色值都按 <c>#RRGGBBAA</c> 存，认 6 位简写。
/// </para>
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>把这一卡片刷成当前设置（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulateOverlayUi()
    {
        CaptureToolbarSizeCombo.SelectedIndex =
            OverlayUiSettings.ToolbarSize == OverlayUiSettings.ToolbarSmall ? 1 : 0;

        CaptureRegionTypeCombo.SelectedIndex = SelectionStore.RegionType switch
        {
            LTools.Features.CapturePro.SelectionMode.Polyline => 1,
            LTools.Features.CapturePro.SelectionMode.Curve => 2,
            LTools.Features.CapturePro.SelectionMode.Lasso => 3,
            _ => 0,
        };

        CaptureTransitionCombo.SelectedIndex = OverlayUiSettings.SelectionTransition switch
        {
            SelectionTransition.Fast => 1,
            SelectionTransition.None => 2,
            _ => 0,
        };

        CaptureUnitCombo.SelectedIndex = OverlayUiSettings.LogicalUnits ? 1 : 0;

        CapturePickerModeCombo.SelectedIndex = OverlayUiSettings.ColorPickerDisplayMode switch
        {
            OverlayUiSettings.PickerAlwaysShow => 1,
            OverlayUiSettings.PickerAlwaysHide => 2,
            _ => 0,
        };

        CapturePickerFormatCombo.SelectedIndex = OverlayUiSettings.ColorPickerFormat switch
        {
            OverlayUiSettings.FormatHexWithoutHash => 1,
            OverlayUiSettings.FormatRgb => 2,
            OverlayUiSettings.FormatHsl => 3,
            _ => 0,
        };

        CapturePickerCoordCombo.SelectedIndex = OverlayUiSettings.RelativePickerCoordinates ? 1 : 0;

        CaptureHintOpacitySlider.Value = OverlayUiSettings.ShortcutHintOpacity;
        CaptureHintOpacityText.Text = OverlayUiSettings.ShortcutHintOpacity + "%";

        CaptureHideSelectionBarBox.IsChecked = OverlayUiSettings.SelectionToolbarHidden;
        CaptureShowEditBarBox.IsChecked = OverlayUiSettings.ShowEditSelectionToolbar;
        CaptureAreaHintBox.IsChecked = OverlayUiSettings.AreaTypeHintEnabled;
        CaptureGuidesDefaultBox.IsChecked = OverlayUiSettings.ShowGuidesByDefault;
        CaptureGuidesAdaptiveBox.IsChecked = OverlayUiSettings.AdaptiveGuideColors;

        ShowColor(CaptureBorderColorSwatch, CaptureBorderColorText, OverlayUiSettings.SelectionBorderColor);
        ShowColor(CaptureMaskColorSwatch, CaptureMaskColorText, OverlayUiSettings.SelectionMaskColor);
        ShowColor(CaptureCursorGuideColorSwatch, CaptureCursorGuideColorText, OverlayUiSettings.CursorGuideColor);
        ShowColor(CaptureSelectionGuideColorSwatch, CaptureSelectionGuideColorText,
            OverlayUiSettings.SelectionCenterGuideColor);
        ShowColor(CaptureMonitorGuideColorSwatch, CaptureMonitorGuideColorText,
            OverlayUiSettings.MonitorCenterGuideColor);
        ShowColor(CapturePickerGuideColorSwatch, CapturePickerGuideColorText,
            OverlayUiSettings.PickerCenterGuideColor);

        PopulateActionLayoutSummary();
    }

    /// <summary>「完成动作按钮」那一行的摘要（分组用竖线连、收起来的在末尾报数）。</summary>
    private void PopulateActionLayoutSummary()
    {
        var layout = CaptureActionLayoutStore.Current;
        string text = layout.Describe();
        if (layout.Hidden.Count > 0) text += $"（收起 {layout.Hidden.Count} 个）";
        CaptureActionLayoutSummary.Text = text;
    }

    /// <summary>打开完成动作布局编辑器；保存了就刷新摘要。</summary>
    private void CaptureActionLayout_Click(object sender, RoutedEventArgs e)
    {
        if (!CaptureActionLayoutWindow.Show(this)) return;
        PopulateActionLayoutSummary();
    }

    /// <summary>六个色值按钮的共用外观：色块 + <c>#RRGGBBAA</c> 文本（透明写「透明」）。</summary>
    private static void ShowColor(Border swatch, TextBlock text, Color color)
    {
        swatch.Background = new SolidColorBrush(color);
        text.Text = color.A == 0 ? "透明（不画）" : ColorPresets.Format(color);
    }

    // ============ 下拉 / 勾选 ============

    /// <summary>选区形状（落 <c>ui.json</c> 的 <c>CaptureSelection.RegionType</c>，覆盖层下次打开就用它）。</summary>
    private void CaptureRegionType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        SelectionStore.SetRegionType(CaptureRegionTypeCombo.SelectedIndex switch
        {
            1 => LTools.Features.CapturePro.SelectionMode.Polyline,
            2 => LTools.Features.CapturePro.SelectionMode.Curve,
            3 => LTools.Features.CapturePro.SelectionMode.Lasso,
            _ => LTools.Features.CapturePro.SelectionMode.Rect,
        });
    }

    private void CaptureOverlayUi_Changed(object sender, RoutedEventArgs e)
    {        if (_initializing) return;

        OverlayUiSettings.SetToolbarSize(CaptureToolbarSizeCombo.SelectedIndex == 1
            ? OverlayUiSettings.ToolbarSmall
            : OverlayUiSettings.ToolbarNormal);

        OverlayUiSettings.SetSelectionTransition(CaptureTransitionCombo.SelectedIndex switch
        {
            1 => SelectionTransition.Fast,
            2 => SelectionTransition.None,
            _ => SelectionTransition.Soft,
        });

        OverlayUiSettings.SetSelectionDisplayUnit(CaptureUnitCombo.SelectedIndex == 1
            ? OverlayUiSettings.UnitLogical
            : OverlayUiSettings.UnitPhysical);

        OverlayUiSettings.SetColorPickerDisplayMode(CapturePickerModeCombo.SelectedIndex switch
        {
            1 => OverlayUiSettings.PickerAlwaysShow,
            2 => OverlayUiSettings.PickerAlwaysHide,
            _ => OverlayUiSettings.PickerHideOutsideSelection,
        });

        OverlayUiSettings.SetColorPickerFormat(CapturePickerFormatCombo.SelectedIndex switch
        {
            1 => OverlayUiSettings.FormatHexWithoutHash,
            2 => OverlayUiSettings.FormatRgb,
            3 => OverlayUiSettings.FormatHsl,
            _ => OverlayUiSettings.FormatHex,
        });

        OverlayUiSettings.SetColorPickerCoordinateMode(CapturePickerCoordCombo.SelectedIndex == 1
            ? OverlayUiSettings.CoordRelative
            : OverlayUiSettings.CoordGlobal);

        OverlayUiSettings.SetSelectionToolbarHidden(CaptureHideSelectionBarBox.IsChecked == true);
        OverlayUiSettings.SetShowEditSelectionToolbar(CaptureShowEditBarBox.IsChecked == true);
        OverlayUiSettings.SetAreaTypeHintEnabled(CaptureAreaHintBox.IsChecked == true);
        OverlayUiSettings.SetShowGuidesByDefault(CaptureGuidesDefaultBox.IsChecked == true);
        OverlayUiSettings.SetAdaptiveGuideColors(CaptureGuidesAdaptiveBox.IsChecked == true);
    }

    // ============ 提示条不透明度 ============

    private void CaptureHintOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int percent = (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero);
        CaptureHintOpacityText.Text = percent + "%";

        if (_initializing) return;
        OverlayUiSettings.SetShortcutHintOpacity(percent);
    }

    // ============ 六个颜色按钮 ============

    private void CaptureBorderColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(OverlayUiSettings.SelectionBorderColor,
            color => OverlayUiSettings.SetSelectionBorderColor(color),
            CaptureBorderColorSwatch, CaptureBorderColorText);

    private void CaptureMaskColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(OverlayUiSettings.SelectionMaskColor,
            color => OverlayUiSettings.SetSelectionMaskColor(color),
            CaptureMaskColorSwatch, CaptureMaskColorText);

    private void CaptureCursorGuideColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(OverlayUiSettings.CursorGuideColor,
            color => OverlayUiSettings.SetCursorGuideColor(color),
            CaptureCursorGuideColorSwatch, CaptureCursorGuideColorText);

    private void CaptureSelectionGuideColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(OverlayUiSettings.SelectionCenterGuideColor,
            color => OverlayUiSettings.SetSelectionCenterGuideColor(color),
            CaptureSelectionGuideColorSwatch, CaptureSelectionGuideColorText);

    private void CaptureMonitorGuideColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(OverlayUiSettings.MonitorCenterGuideColor,
            color => OverlayUiSettings.SetMonitorCenterGuideColor(color),
            CaptureMonitorGuideColorSwatch, CaptureMonitorGuideColorText);

    private void CapturePickerGuideColor_Click(object sender, RoutedEventArgs e) =>
        PickColor(OverlayUiSettings.PickerCenterGuideColor,
            color => OverlayUiSettings.SetPickerCenterGuideColor(color),
            CapturePickerGuideColorSwatch, CapturePickerGuideColorText);

    /// <summary>弹一次取色框，确认了就落盘并把色块 / 文本刷成新值（取消什么都不改）。</summary>
    private void PickColor(Color current, Action<Color> save, Border swatch, TextBlock text)
    {
        if (!CaptureColorDialog.Show(this, current, out var picked)) return;

        save(picked);
        ShowColor(swatch, text, picked);
    }
}
