using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 选区参数对话框（照上游 <c>ScreenshotSelectionResizeModalContent</c>）：
/// 快速集 + X / Y / 宽 / 高 + 比例锁 + 圆角 + 阴影 | 边框 + 效果宽 / 色。
/// </summary>
/// <remarks>
/// <para>
/// 全部是屏幕物理像素（与信息条、快速集、历史同一套）。比例锁只联动宽 / 高，
/// 左上角（X / Y）不动——与上游 <c>syncHeightFromWidth</c> / <c>syncWidthFromHeight</c> 一致。
/// </para>
/// <para>
/// 「添加 / 删除快速集」立刻落盘（<see cref="SelectionStore"/>），确定 / 取消只管这一次的几何与外观；
/// 与上游「预设在弹窗里就能增删」的行为一致。
/// </para>
/// </remarks>
public partial class SelectionParamsWindow : Window
{
    private static readonly Color[] PaletteColors =
    {
        Colors.White, Color.FromRgb(0x00, 0x00, 0x00), Color.FromRgb(0x33, 0x33, 0x33),
        Color.FromRgb(0x71, 0x71, 0x7A), Color.FromRgb(0xEF, 0x44, 0x44),
        Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xEA, 0xB3, 0x08),
        Color.FromRgb(0x22, 0xC5, 0x5E), Color.FromRgb(0x06, 0xB6, 0xD4),
        Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x8B, 0x5C, 0xF5),
        Color.FromRgb(0xEC, 0x48, 0x99),
    };

    private readonly SelectionParams _current;
    private readonly SelectionParams? _previous;
    private readonly List<SelectionPreset> _presets;
    private readonly Int32Rect _bounds;
    private readonly double _scale;
    private readonly bool _radiusApplicable;
    private readonly List<Border> _swatches = new();

    private SelectionParams _draft;
    private Color _color;
    private bool _lockAspect;
    private double _ratio;        // 高 / 宽；锁着才有意义
    private bool _syncing;

    /// <summary>
    /// 控件里那两份值（宽 + 色）现在属于哪一种效果。
    /// </summary>
    /// <remarks>
    /// 切「阴影 / 边框」时单选已经变了、字段还是旧类型那两份，所以要记着「字段是按哪种类型写的」，
    /// 读值一律先写回 <c>_editedType</c> 那一份，再把结果里的类型换成单选选中的那个。
    /// 不这么做的话切到边框会把阴影色 / 阴影宽盖到边框上（两份额外值全丢）。
    /// </remarks>
    private SelectionBorderType _editedType = SelectionBorderType.Shadow;

    /// <summary>按「确定」时的结果；取消 = null。</summary>
    internal SelectionParams? Result { get; private set; }

    private SelectionParamsWindow(SelectionParams current, SelectionParams? previous,
        IReadOnlyList<SelectionPreset> presets, Int32Rect bounds, double scale, bool radiusApplicable)
    {
        InitializeComponent();

        _current = current;
        _previous = previous;
        _presets = new List<SelectionPreset>(presets);
        _bounds = bounds;
        _scale = scale > 0 ? scale : 1;
        _radiusApplicable = radiusApplicable;
        _draft = current;
        _color = current.EffectColor;

        BuildPalette();
        BuildQuickSets();
        ApplyParamsToFields(current);
    }

    internal static SelectionParams? Show(Window owner, SelectionParams current,
        SelectionParams? previous, IReadOnlyList<SelectionPreset> presets,
        Int32Rect bounds, double scale, bool radiusApplicable)
    {
        var dlg = new SelectionParamsWindow(current, previous, presets, bounds, scale, radiusApplicable)
        {
            Owner = owner,
        };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    // ============ 快速集 ============

    private void BuildQuickSets()
    {
        _syncing = true;
        QuickSetBox.Items.Clear();
        QuickSetBox.Items.Add(new ComboBoxItem { Content = "快速集…", Tag = null });
        QuickSetBox.Items.Add(new ComboBoxItem { Content = "当前选区", Tag = "current" });
        QuickSetBox.Items.Add(new ComboBoxItem
        {
            Content = "上次选区",
            Tag = "previous",
            IsEnabled = _previous != null,
        });

        for (int i = 0; i < _presets.Count; i++)
            QuickSetBox.Items.Add(new ComboBoxItem { Content = _presets[i].Name, Tag = i });

        QuickSetBox.SelectedIndex = 0;
        _syncing = false;
    }

    private void QuickSet_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (QuickSetBox.SelectedItem is not ComboBoxItem item) return;

        switch (item.Tag)
        {
            case "current":
                ApplyParamsToFields(_current);
                break;
            case "previous" when _previous != null:
                ApplyParamsToFields(_previous);
                break;
            case int index when index >= 0 && index < _presets.Count:
            {
                var preset = _presets[index];
                ApplyParamsToFields(SelectionParams.From(
                    preset.Area, preset.LockAspect, preset.Style, _scale));
                break;
            }
        }
    }

    private void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        var p = ReadParams();
        if (p == null) return;

        string name = $"{p.Width} x {p.Height}";
        var (ok, value) = InputDialog.Show(this, "添加快速集", "快速集名称：", name);
        if (!ok || string.IsNullOrWhiteSpace(value)) return;

        name = value.Trim();
        var style = p.ApplyTo(SelectionStyle.None, _scale);
        SelectionStore.AddPreset(name, p.Area, style, p.LockAspect);

        _presets.RemoveAll(x => string.Equals(x.Name, name, StringComparison.Ordinal));
        _presets.Add(new SelectionPreset(name, p.Area, style, p.LockAspect));
        BuildQuickSets();

        QuickSetBox.SelectedIndex = QuickSetBox.Items.Count - 1;
        UpdatePresetButtons();
    }

    private void RemovePreset_Click(object sender, RoutedEventArgs e)
    {
        if (QuickSetBox.SelectedItem is not ComboBoxItem { Tag: int index } ||
            index < 0 || index >= _presets.Count)
            return;

        var preset = _presets[index];
        if (!ConfirmDialog.Confirm(this, "删除快速集", $"删除快速集「{preset.Name}」？", danger: true, okText: "删除"))
            return;

        SelectionStore.RemovePreset(preset.Name);
        _presets.RemoveAt(index);
        BuildQuickSets();
        UpdatePresetButtons();
    }

    private void UpdatePresetButtons()
    {
        RemovePresetBtn.IsEnabled = QuickSetBox.SelectedItem is ComboBoxItem { Tag: int };
    }

    // ============ 字段 ============

    /// <summary>把一份参数灌进控件（切快速集 / 切类型都走它）。</summary>
    private void ApplyParamsToFields(SelectionParams p)
    {
        _syncing = true;

        _draft = p;
        _lockAspect = p.LockAspect;
        _ratio = p.LockAspect ? (double)p.Height / Math.Max(1, p.Width) : 0;
        _color = p.EffectColor;
        _editedType = p.BorderType;

        XBox.Text = p.X.ToString();
        YBox.Text = p.Y.ToString();
        WidthBox.Text = p.Width.ToString();
        HeightBox.Text = p.Height.ToString();
        RadiusBox.Text = p.RadiusPixels.ToString();
        EffectWidthBox.Text = p.EffectWidthPixels.ToString();

        ShadowTypeBtn.IsChecked = p.BorderType == SelectionBorderType.Shadow;
        BorderTypeBtn.IsChecked = p.BorderType == SelectionBorderType.Border;
        UpdateEffectLabels();
        SetColor(p.EffectColor, syncHex: true);
        UpdateLockVisual();
        UpdateRadiusAvailability();

        _syncing = false;
        UpdatePresetButtons();
    }

    private void UpdateRadiusAvailability()
    {
        RadiusBox.IsEnabled = _radiusApplicable;
        RadiusBox.ToolTip = _radiusApplicable ? null : "自定义区域 / 多块选区没有圆角";
    }

    private void UpdateEffectLabels()
    {
        bool border = BorderTypeBtn.IsChecked == true;
        EffectWidthLabel.Text = border ? "边框宽度" : "阴影宽度";
        EffectColorLabel.Text = border ? "边框颜色" : "阴影颜色";
    }

    private void UpdateLockVisual()
    {
        if (_lockAspect)
        {
            LockBtn.Background = (Brush)FindResource("PrimaryBrush");
            LockBtn.Tag = FindResource("PrimaryHoverBrush");
            LockGlyph.Fill = (Brush)FindResource("OnPrimaryTextBrush");
        }
        else
        {
            LockBtn.Background = Brushes.Transparent;
            LockBtn.Tag = FindResource("HoverBgBrush");
            LockGlyph.Fill = (Brush)FindResource("TextBrush");
        }
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _lockAspect = !_lockAspect;
        if (_lockAspect && TryInt(WidthBox, out int w) && TryInt(HeightBox, out int h) && w > 0)
            _ratio = (double)Math.Max(1, h) / w;
        UpdateLockVisual();
    }

    private void BorderType_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        // 先把当前类型的宽 / 色收进草稿，再切类型（另一份宽度 / 颜色各自保留）
        var next = ReadParams();
        if (next == null) return;

        var type = BorderTypeBtn.IsChecked == true ? SelectionBorderType.Border : SelectionBorderType.Shadow;
        ApplyParamsToFields(next with { BorderType = type });
    }

    private void Width_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !_lockAspect || _ratio <= 0) return;
        if (!TryInt(WidthBox, out int width) || width <= 0) return;

        int height = Math.Clamp((int)Math.Round(width * _ratio), 1, Math.Max(1, _bounds.Height));
        if (TryInt(HeightBox, out int current) && current == height) return;

        _syncing = true;
        HeightBox.Text = height.ToString();
        _syncing = false;
    }

    private void Height_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !_lockAspect || _ratio <= 0) return;
        if (!TryInt(HeightBox, out int height) || height <= 0) return;

        int width = Math.Clamp((int)Math.Round(height / _ratio), 1, Math.Max(1, _bounds.Width));
        if (TryInt(WidthBox, out int current) && current == width) return;

        _syncing = true;
        WidthBox.Text = width.ToString();
        _syncing = false;
    }

    private void Number_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        // 改 X / Y 时收窄宽高的可选上限（上游 updateGeometryRanges 同义，这里由 ReadParams 夹）
    }

    // ============ 颜色 ============

    private void BuildPalette()
    {
        foreach (var color in PaletteColors)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                Cursor = Cursors.Hand,
            };
            var captured = color;
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                SetColor(captured, syncHex: true);
            };
            _swatches.Add(swatch);
            ColorPalette.Children.Add(swatch);
        }
    }

    private void SetColor(Color color, bool syncHex)
    {
        _color = color;
        for (int i = 0; i < _swatches.Count; i++)
        {
            bool selected = i < PaletteColors.Length && PaletteColors[i] == color;
            _swatches[i].BorderThickness = new Thickness(selected ? 2 : 1);
            _swatches[i].BorderBrush = selected
                ? (Brush)FindResource("PrimaryBrush")
                : (Brush)FindResource("BorderBrush");
        }

        if (!syncHex) return;
        _syncing = true;
        ColorHexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _syncing = false;
    }

    private void ColorHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (!TryParseHex(ColorHexBox.Text, out var color)) return;
        SetColor(color, syncHex: false);
    }

    private static bool TryParseHex(string text, out Color color)
    {
        color = default;
        string value = text.Trim().TrimStart('#');
        if (value.Length != 6) return false;
        try
        {
            color = Color.FromRgb(
                Convert.ToByte(value.Substring(0, 2), 16),
                Convert.ToByte(value.Substring(2, 2), 16),
                Convert.ToByte(value.Substring(4, 2), 16));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ============ 读值 ============

    /// <summary>把控件读成一份参数；数字填错返回 null（确定按钮会提示）。</summary>
    private SelectionParams? ReadParams()
    {
        if (!TryInt(XBox, out int x) || !TryInt(YBox, out int y) ||
            !TryInt(WidthBox, out int width) || !TryInt(HeightBox, out int height) ||
            !TryInt(RadiusBox, out int radius) || !TryInt(EffectWidthBox, out int effectWidth))
        {
            MarkInvalid();
            return null;
        }

        int w = Math.Clamp(width, 1, Math.Max(1, _bounds.Width));
        int h = Math.Clamp(height, 1, Math.Max(1, _bounds.Height));

        var p = _draft with
        {
            Area = new Int32Rect(x, y, w, h),
            LockAspect = _lockAspect,
            RadiusPixels = Math.Clamp(radius, 0, SelectionStyle.CornerRadiusMax),
            // 字段里那两份值属于 _editedType（切换类型那一拍单选已经变了、字段还是旧的）
            BorderType = _editedType,
        };

        p = p.WithEffectWidth(Math.Clamp(effectWidth, 0, SelectionStyle.EffectWidthMax));
        p = p.WithEffectColor(_color);
        p = p with
        {
            BorderType = BorderTypeBtn.IsChecked == true
                ? SelectionBorderType.Border
                : SelectionBorderType.Shadow,
        };
        return p.ClampTo(_bounds);
    }

    private void MarkInvalid()
    {
        foreach (var box in new[] { XBox, YBox, WidthBox, HeightBox, RadiusBox, EffectWidthBox })
        {
            bool bad = !TryInt(box, out _);
            box.BorderBrush = bad ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("BorderBrush");
        }
    }

    private static bool TryInt(TextBox box, out int value) =>
        int.TryParse(box.Text.Trim(), out value);

    // ============ 出口 ============

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var p = ReadParams();
        if (p == null)
        {
            ConfirmDialog.Info(this, "选区参数", "有数字填错了：请填整数（宽 / 高至少 1）。");
            return;
        }

        Result = p;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

