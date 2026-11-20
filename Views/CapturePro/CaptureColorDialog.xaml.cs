using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 取色 / 选色弹窗（照上游 <c>AdColorPicker</c> 的截图工具条那一版）：
/// 色板 + 色相条 + 透明度条 + 格式下拉（HEX / RGB / HSB）+ 一个能写多种写法的输入框 + 快捷色。
/// </summary>
/// <remarks>
/// <para>
/// 输入框认 <c>#RGB</c> / <c>#RGBA</c> / <c>#RRGGBB</c> / <c>#RRGGBBAA</c>（井号可省 6 / 8 位）、
/// <c>rgb()</c> / <c>rgba()</c> / <c>hsl()</c> / <c>hsla()</c> / <c>hsb()</c> / <c>hsv()</c>
/// ——解析只有 <see cref="ColorText"/> 一份，取色卡也用同一套。
/// </para>
/// <para>
/// 色板改 S / V、色相条改 H、透明度条改 alpha；三者都就地重算颜色并回写输入框（按当前格式）。
/// 灰色（饱和 = 0）时色相条停在原来的角度，不会一跳就归零。
/// </para>
/// </remarks>
public partial class CaptureColorDialog : Window
{
    private static readonly Color[] QuickPickColors =
    {
        Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), // 透明
        Colors.White,
        Color.FromRgb(0x00, 0x00, 0x00),
        Color.FromRgb(0x8C, 0x8C, 0x8C),
        Color.FromRgb(0xF5, 0x22, 0x2D),
        Color.FromRgb(0xFA, 0x8C, 0x16),
        Color.FromRgb(0xFA, 0xDB, 0x14),
        Color.FromRgb(0x52, 0xC4, 0x1A),
        Color.FromRgb(0x13, 0xC2, 0xC2),
        Color.FromRgb(0x16, 0x77, 0xFF),
        Color.FromRgb(0x72, 0x2E, 0xD1),
        Color.FromRgb(0xEB, 0x2F, 0x96),
    };

    private bool _syncing;
    private Color _color;
    private ColorFormatKind _format = ColorFormatKind.Hex;
    private double _hue;

    private CaptureColorDialog(Color color)
    {
        InitializeComponent();
        BuildQuickPicks();
        FormatBox.SelectedIndex = 0;
        ApplyColor(color, syncText: true);
    }

    /// <summary>当前选中的颜色（确定之后才有意义）。</summary>
    internal Color Result => _color;

    internal static bool Show(Window? owner, Color color, out Color result)
    {
        var window = new CaptureColorDialog(color)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
            Topmost = owner?.Topmost ?? true,
        };

        bool ok = window.ShowDialog() == true;
        result = window._color;
        return ok;
    }

    private void BuildQuickPicks()
    {
        foreach (var color in QuickPickColors)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(4),
                Background = ColorPresets.Swatch(color),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                Cursor = Cursors.Hand,
                ToolTip = ColorText.Format(color, ColorFormatKind.Hex),
            };

            var captured = color;
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                ApplyColor(captured, syncText: true);
            };

            QuickPicks.Children.Add(swatch);
        }
    }

    /// <summary>把颜色灌进全部控件（色板 / 色相条 / 透明度条 / 预览 / 输入框）。</summary>
    private void ApplyColor(Color color, bool syncText)
    {
        _color = color;

        var (hue, saturation, brightness) = ColorText.ToHsb(color);
        if (saturation > 0.01) _hue = hue;      // 灰色时保留原来的色相，别一跳就归零

        _syncing = true;
        Preview.Background = ColorPresets.Swatch(color);

        Board.Hue = _hue;
        Board.Saturation = saturation / 100.0;
        Board.Brightness = brightness / 100.0;
        Board.InvalidateVisual();

        Hue.Hue = _hue;
        Hue.InvalidateVisual();

        Alpha.Color = color;
        Alpha.Alpha = color.A;
        Alpha.InvalidateVisual();
        AlphaText.Text = ((int)Math.Round(color.A / 255.0 * 100)).ToString(CultureInfo.InvariantCulture) + "%";

        if (syncText) ValueBox.Text = ColorText.Format(color, _format);
        _syncing = false;
        ErrorText.Text = "";
    }

    // ============ 色板 / 色相 / 透明度 ============

    private void Board_Picked(object? sender, EventArgs e) =>
        ApplyColor(ColorText.FromHsb(_hue, Board.Saturation * 100, Board.Brightness * 100, _color.A),
            syncText: true);

    private void Hue_Picked(object? sender, EventArgs e)
    {
        _hue = Hue.Hue;
        ApplyColor(ColorText.FromHsb(_hue, Board.Saturation * 100, Board.Brightness * 100, _color.A),
            syncText: true);
    }

    private void Alpha_Picked(object? sender, EventArgs e) =>
        ApplyColor(Color.FromArgb(Alpha.Alpha, _color.R, _color.G, _color.B), syncText: true);

    // ============ 格式与输入框 ============

    private void Format_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ValueBox == null) return;

        _format = ColorText.KindOf((FormatBox.SelectedItem as ComboBoxItem)?.Tag as string);

        _syncing = true;
        ValueBox.Text = ColorText.Format(_color, _format);
        _syncing = false;
        ErrorText.Text = "";
    }

    private void Value_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;

        // 输到一半（少一位、括号没写完）不报错，能认出来才同步色板
        if (ColorText.TryParse(ValueBox.Text, out var color)) ApplyColor(color, syncText: false);
        else ErrorText.Text = "";
    }

    private void Value_LostFocus(object sender, RoutedEventArgs e)
    {
        // 离开输入框时还是认不出来：把最后能用的颜色写回去，别留一段半截文本
        if (!ColorText.TryParse(ValueBox.Text, out _))
            ApplyColor(_color, syncText: true);
    }

    private void Value_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        Commit();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit()) return;
        DialogResult = true;
    }

    /// <summary>把输入框里那段写进当前颜色；认不出来就地提示、不关窗。</summary>
    private bool Commit()
    {
        if (!ColorText.TryParse(ValueBox.Text, out var color))
        {
            ErrorText.Text = "认不出这个颜色：可以写 #RRGGBB / #RRGGBBAA、rgb()、hsl()、hsb()。";
            return false;
        }

        ApplyColor(color, syncText: true);
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
