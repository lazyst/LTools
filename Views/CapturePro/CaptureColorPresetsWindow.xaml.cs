using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 颜色预设编辑框（设置「通用 → 截图 Pro」里的「颜色预设…」）。
/// </summary>
/// <remarks>
/// 照上游设置里的 <c>drawing.stroke-color-presets</c> / <c>fill-color-presets</c> /
/// <c>highlight-color-presets</c>：每族恒定 5 个格子，点一格改一个颜色。
/// 编辑的是本地草稿，点「保存」才写 <c>configs/settings.json</c>（取消就整份丢掉）。
/// 真正读写与默认值在 <see cref="ColorPresets"/>。
/// </remarks>
public partial class CaptureColorPresetsWindow : Window
{
    private readonly Dictionary<ColorPresetFamily, string[]> _drafts = new()
    {
        [ColorPresetFamily.Stroke] = ColorPresets.GetHex(ColorPresetFamily.Stroke),
        [ColorPresetFamily.Fill] = ColorPresets.GetHex(ColorPresetFamily.Fill),
        [ColorPresetFamily.Highlight] = ColorPresets.GetHex(ColorPresetFamily.Highlight),
    };

    private CaptureColorPresetsWindow()
    {
        InitializeComponent();
        Build(ColorPresetFamily.Stroke);
        Build(ColorPresetFamily.Fill);
        Build(ColorPresetFamily.Highlight);
    }

    internal static void Show(Window? owner)
    {
        var window = new CaptureColorPresetsWindow
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
            Topmost = owner?.Topmost ?? true,
        };

        window.ShowDialog();
    }

    private WrapPanel PanelOf(ColorPresetFamily family) => family switch
    {
        ColorPresetFamily.Fill => FillPalette,
        ColorPresetFamily.Highlight => HighlightPalette,
        _ => StrokePalette,
    };

    private void Build(ColorPresetFamily family)
    {
        var panel = PanelOf(family);
        panel.Children.Clear();

        string[] hex = _drafts[family];
        for (int i = 0; i < hex.Length; i++)
        {
            int index = i;
            ColorPresets.TryParse(hex[i], out var color);

            var swatch = new Border
            {
                Width = 34,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(4),
                Background = ColorPresets.Swatch(color),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                Cursor = Cursors.Hand,
                ToolTip = $"第 {i + 1} 个 · {ColorPresets.Format(color)}",
            };

            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                Pick(family, index);
            };

            panel.Children.Add(swatch);
        }
    }

    private void Pick(ColorPresetFamily family, int index)
    {
        ColorPresets.TryParse(_drafts[family][index], out var color);
        if (!CaptureColorDialog.Show(this, color, out var picked)) return;

        _drafts[family][index] = ColorPresets.Format(picked);
        Build(family);
    }

    private static bool TryFamily(object? tag, out ColorPresetFamily family)
    {
        family = ColorPresetFamily.Stroke;
        if (tag is not string text) return false;

        return Enum.TryParse(text, ignoreCase: true, out family);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !TryFamily(button.Tag, out var family)) return;

        var defaults = ColorPresets.DefaultHex(family);
        _drafts[family] = new string[defaults.Count];
        for (int i = 0; i < defaults.Count; i++) _drafts[family][i] = defaults[i];
        Build(family);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (family, hex) in _drafts) ColorPresets.Set(family, hex);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
