using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.Collage;

namespace LTools.Views.CapturePro;

/// <summary>
/// 拼接窗右侧布局表单（照上游 <c>ImageStitchingEditorWidget</c> 的 <c>refresh()</c> /
/// <c>settingsChanged()</c> / <c>syncLengthInputs()</c>）：布局方向 / 宫格行列 / 图像尺寸 /
/// 外边框 / 图像间距 / 背景颜色。
/// </summary>
/// <remarks>
/// <para>
/// 外边距与间距在磁盘和文档里**永远存输出像素**；界面上切到百分比只是换一种输入方式，
/// 参考长度照上游 <c>lengthReference()</c>：统一尺寸只看首图，纵向 / 宫格取宽、横向取高。
/// 切单位本身不动像素值，输入框按新单位把同一份像素重新显示一遍。
/// </para>
/// <para>
/// 提交时机是失焦 / Enter（对应上游 <c>setKeyboardTracking(false)</c>），提交后一律走
/// <see cref="ApplySettings"/>：值被规整回去（列数小于图片数需要的列）时把输入框拉回真值，
/// 换方向或改宫格行列时让画布重新适应。
/// </para>
/// </remarks>
public partial class ImageCollageWindow
{
    /// <summary>照上游 <c>kColorValues</c>，与 XAML 里 8 个色卡一一对应。</summary>
    private static readonly Color[] BackgroundPresets =
    {
        Color.FromRgb(0xFF, 0xFF, 0xFF),
        Color.FromRgb(0x00, 0x00, 0x00),
        Color.FromRgb(0x8C, 0x8C, 0x8C),
        Color.FromRgb(0xFF, 0xCC, 0xC7),
        Color.FromRgb(0xD9, 0xF7, 0xBE),
        Color.FromRgb(0xBA, 0xE0, 0xFF),
        Color.FromRgb(0xFF, 0xF1, 0xB8),
        Color.FromRgb(0xEF, 0xDB, 0xFF),
    };

    /// <summary>设置落盘的合并计时器（取色滑块会连着改很多次）。</summary>
    private DispatcherTimer? _settingsTimer;

    private bool _borderPercent;
    private bool _spacingPercent;

    // ============ 表单 → 控件 ============

    /// <summary>按文档设置刷新整张表单（不让控件事件反过来改文档）。</summary>
    private void SyncForm(bool force = false)
    {
        if (_closing) return;

        var settings = _doc.Settings;
        _syncing = true;
        try
        {
            DirVertical.IsChecked = settings.Direction == CollageDirection.Vertical;
            DirHorizontal.IsChecked = settings.Direction == CollageDirection.Horizontal;
            DirCustom.IsChecked = settings.Direction == CollageDirection.Custom;
            GridRow.Visibility = settings.Direction == CollageDirection.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (force || !RowsBox.IsKeyboardFocusWithin)
                RowsBox.Text = settings.Rows.ToString(CultureInfo.InvariantCulture);
            if (force || !ColumnsBox.IsKeyboardFocusWithin)
                ColumnsBox.Text = settings.Columns.ToString(CultureInfo.InvariantCulture);

            SizeUniform.IsChecked = settings.Sizing == CollageSizing.Uniform;
            SizeOriginal.IsChecked = settings.Sizing == CollageSizing.Original;

            BorderUnitPx.IsChecked = !_borderPercent;
            BorderUnitPct.IsChecked = _borderPercent;
            SpacingUnitPx.IsChecked = !_spacingPercent;
            SpacingUnitPct.IsChecked = _spacingPercent;

            // 参考长度会随图片 / 尺寸方式变：百分比模式下按当前参考重算显示，存的仍是像素。
            int reference = LengthReference();
            SyncLength(BorderBox, BorderSuffix, settings.Border, _borderPercent, reference, force);
            SyncLength(SpacingBox, SpacingSuffix, settings.Spacing, _spacingPercent, reference, force);

            string description = settings.Direction switch
            {
                CollageDirection.Custom => "网格单元宽度的百分比",
                CollageDirection.Horizontal => "定尺寸后最高图像的百分比",
                _ => "定尺寸后最宽图像的百分比",
            };
            BorderUnitGroup.ToolTip = description;
            SpacingUnitGroup.ToolTip = description;
            BorderBox.ToolTip = _borderPercent ? description : "外边框（输出像素）";
            SpacingBox.ToolTip = _spacingPercent ? description : "图像间距（输出像素）";

            SyncColor(settings.Background);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>一个长度输入框：按单位显示、百分比且没有参考长度时置灰清空（照上游）。</summary>
    private static void SyncLength(TextBox box, TextBlock suffix, int pixels, bool percent, int reference,
        bool force)
    {
        suffix.Text = percent ? "%" : "px";

        bool available = !percent || reference > 0;
        box.IsEnabled = available;
        if (!available)
        {
            if (force || !box.IsKeyboardFocusWithin) box.Text = "";
            return;
        }

        if (!force && box.IsKeyboardFocusWithin) return;

        box.Text = percent
            ? (100.0 * pixels / reference).ToString("0.##", CultureInfo.InvariantCulture)
            : pixels.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 百分比 / 参考长度用哪条边（照上游 <c>lengthReference</c>）：统一尺寸只有首图参与，
    /// 纵向与宫格取宽、横向取高。
    /// </summary>
    private int LengthReference()
    {
        var settings = _doc.Settings;
        int reference = 0;
        foreach (var item in _doc.Items)
        {
            var (width, height) = item.TransformedSize;
            reference = Math.Max(reference, settings.Direction != CollageDirection.Horizontal ? width : height);
            if (settings.Sizing == CollageSizing.Uniform) break;
        }

        return reference;
    }

    /// <summary>输入框里的值换成输出像素；解析不了返回 -1（调用方回滚显示）。</summary>
    private int LengthPixels(TextBox box, bool percent)
    {
        string text = box.Text.Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return -1;

        if (percent) value = value * LengthReference() / 100.0;
        if (!double.IsFinite(value)) return -1;

        value = Math.Clamp(value, 0, int.MaxValue);
        return (int)Math.Round(value);
    }

    // ============ 控件 → 表单 ============

    private void Direction_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _closing) return;

        CommitPendingFields();
        var direction = DirHorizontal.IsChecked == true
            ? CollageDirection.Horizontal
            : DirCustom.IsChecked == true
                ? CollageDirection.Custom
                : CollageDirection.Vertical;
        ApplySettings(settings => settings with { Direction = direction }, "direction");
    }

    private void Sizing_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _closing) return;

        CommitPendingFields();
        var sizing = SizeOriginal.IsChecked == true ? CollageSizing.Original : CollageSizing.Uniform;
        ApplySettings(settings => settings with { Sizing = sizing }, "sizing_mode");
    }

    private void BorderUnit_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _closing) return;

        // 先按旧单位把没提交的输入落下去（上游切单位前也会先 clearFocus）。
        if (BorderBox.IsKeyboardFocusWithin) CommitField(BorderBox);
        _borderPercent = BorderUnitPct.IsChecked == true;
        SyncForm(force: true);
    }

    private void SpacingUnit_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _closing) return;

        if (SpacingBox.IsKeyboardFocusWithin) CommitField(SpacingBox);
        _spacingPercent = SpacingUnitPct.IsChecked == true;
        SyncForm(force: true);
    }

    private void Field_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncing || _closing || sender is not TextBox box) return;
        CommitField(box);
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        CommitField(box);
        e.Handled = true;
    }

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _closing) return;
        if (sender is not RadioButton { Tag: string tag } ||
            !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            return;
        if (index < 0 || index >= BackgroundPresets.Length) return;

        CommitPendingFields();
        ApplySettings(settings => settings with { Background = BackgroundPresets[index] }, "background_color");
    }

    /// <summary>焦点还在输入框里、但用户已点了别的控件（分段控件不抢焦点）时先落值。</summary>
    private void CommitPendingFields()
    {
        foreach (var box in new[]
                 {
                     BorderBox, SpacingBox, RowsBox, ColumnsBox, PdfWidthBox, PdfHeightBox,
                     PdfMarginBox, PdfImageSpacingBox,
                 })
        {
            if (box.IsKeyboardFocusWithin) CommitField(box);
        }
    }

    private void CommitField(TextBox box)
    {
        if (_syncing || _closing) return;
        if (_loading || _exporting || _pdfExporting)
        {
            SyncForm(force: true);
            SyncPdfForm(force: true);
            return;
        }

        if (IsPdfField(box))
        {
            CommitPdfField(box);
            return;
        }

        if (ReferenceEquals(box, RowsBox) || ReferenceEquals(box, ColumnsBox))
        {
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int value) || value < 1)
            {
                SyncForm(force: true);
                return;
            }

            value = Math.Min(value, 9999);
            bool rows = ReferenceEquals(box, RowsBox);
            ApplySettings(settings => rows ? settings with { Rows = value } : settings with { Columns = value },
                rows ? "rows" : "columns");
            return;
        }

        bool percent = ReferenceEquals(box, BorderBox) ? _borderPercent : _spacingPercent;
        int pixels = LengthPixels(box, percent);
        if (pixels < 0)
        {
            SyncForm(force: true);
            return;
        }

        if (ReferenceEquals(box, BorderBox))
            ApplySettings(settings => settings with { Border = pixels }, "border_px");
        else if (ReferenceEquals(box, SpacingBox))
            ApplySettings(settings => settings with { Spacing = pixels }, "spacing_px");
    }

    /// <summary>
    /// 提交一次设置（照上游 <c>settingsChanged</c>）：值被规整回去时把表单拉回真值，
    /// 换方向或改宫格行列时重新适应画布。
    /// </summary>
    private void ApplySettings(Func<CollageSettings, CollageSettings> edit, string field)
    {
        if (_loading || _exporting || _pdfExporting || _closing)
        {
            SyncForm(force: true);
            return;
        }

        var previous = _doc.Settings;
        var next = edit(previous);
        if (!next.IsValid)
        {
            SyncForm(force: true);
            return;
        }

        _doc.SetSettings(next);
        var current = _doc.Settings;

        // 列数小于图片数需要的时候会被规整回原值、设置一点没变：照样要把输入框拉回真值。
        if (current == previous)
        {
            SyncForm(force: true);
            return;
        }

        if (current.Direction != previous.Direction ||
            (current.Direction == CollageDirection.Custom && field is "rows" or "columns"))
            Preview.FitToCanvas();
    }

    // ============ 背景颜色 ============

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;

        _syncing = true;
        try
        {
            SyncColor(_doc.Settings.Background);
        }
        finally
        {
            _syncing = false;
        }

        ColorPopup.IsOpen = true;
    }

    private void Channel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _closing) return;

        var color = Color.FromArgb(Channel(AlphaSlider), Channel(RedSlider), Channel(GreenSlider),
            Channel(BlueSlider));
        ApplySettings(settings => settings with { Background = color }, "background_color");
    }

    private static byte Channel(Slider slider) =>
        (byte)Math.Clamp((int)Math.Round(slider.Value), 0, 255);

    private void ColorHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitColorHex();
        e.Handled = true;
    }

    private void ColorHex_LostFocus(object sender, RoutedEventArgs e) => CommitColorHex();

    private void CommitColorHex()
    {
        if (_syncing || _closing) return;

        if (!ColorText.TryParse(ColorHex.Text, out var color))
        {
            SyncForm(force: true);
            return;
        }

        ApplySettings(settings => settings with { Background = color }, "background_color");
    }

    /// <summary>取色器 / 色卡 / 滑块与当前背景色对齐（选中态照上游只认不透明预设色）。</summary>
    private void SyncColor(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        ColorPreview.Background = brush;
        ColorButton.Background = brush;
        ColorButton.ToolTip = $"背景颜色 {CollageColor.Format(color)}（可调透明度）";

        if (!ColorHex.IsKeyboardFocusWithin) ColorHex.Text = CollageColor.Format(color);

        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        AlphaSlider.Value = color.A;

        RedText.Text = color.R.ToString(CultureInfo.InvariantCulture);
        GreenText.Text = color.G.ToString(CultureInfo.InvariantCulture);
        BlueText.Text = color.B.ToString(CultureInfo.InvariantCulture);
        AlphaText.Text = color.A.ToString(CultureInfo.InvariantCulture);

        int preset = IndexOfPreset(color);
        var buttons = new[] { Preset0, Preset1, Preset2, Preset3, Preset4, Preset5, Preset6, Preset7 };
        for (int i = 0; i < buttons.Length; i++) buttons[i].IsChecked = i == preset;
    }

    private static int IndexOfPreset(Color color)
    {
        if (color.A != 0xFF) return -1;

        for (int i = 0; i < BackgroundPresets.Length; i++)
        {
            if (BackgroundPresets[i].R == color.R && BackgroundPresets[i].G == color.G &&
                BackgroundPresets[i].B == color.B)
                return i;
        }

        return -1;
    }
}
