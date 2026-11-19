using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 一个颜色的取色框（颜色预设编辑用）：十六进制 + RGBA 数字 + 常用色快选 + 预览。
/// </summary>
/// <remarks>
/// 上游这一层是 Qt 的 <c>QColorDialog</c>；本地没有等价控件，所以只做「能改准」的部分：
/// 输入 <c>#RRGGBBAA</c> 或分别填四个分量，改哪边另一边跟着走，带透明度时预览铺棋盘格。
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

    private CaptureColorDialog(Color color)
    {
        InitializeComponent();
        BuildQuickPicks();
        SetColor(color, syncHex: true, syncRgba: true);
    }

    /// <summary>当前选的颜色（确定之后才有意义）。</summary>
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
                ToolTip = ColorPresets.Format(color),
            };

            var captured = color;
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                SetColor(captured, syncHex: true, syncRgba: true);
            };

            QuickPicks.Children.Add(swatch);
        }
    }

    private void SetColor(Color color, bool syncHex, bool syncRgba)
    {
        _color = color;
        Preview.Background = ColorPresets.Swatch(color);

        _syncing = true;
        if (syncHex) HexBox.Text = ColorPresets.Format(color);
        if (syncRgba)
        {
            RBox.Text = color.R.ToString(CultureInfo.InvariantCulture);
            GBox.Text = color.G.ToString(CultureInfo.InvariantCulture);
            BBox.Text = color.B.ToString(CultureInfo.InvariantCulture);
            ABox.Text = color.A.ToString(CultureInfo.InvariantCulture);
        }
        _syncing = false;
        ErrorText.Text = "";
    }

    private void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;

        // 输到一半不报错（比如刚删掉两位），能解析时才同步其它控件。
        if (ColorPresets.TryParse(HexBox.Text, out var color))
            SetColor(color, syncHex: false, syncRgba: true);
        else
            ErrorText.Text = "";
    }

    private void Rgba_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;

        if (!TryByte(RBox.Text, out byte r) || !TryByte(GBox.Text, out byte g) ||
            !TryByte(BBox.Text, out byte b) || !TryByte(ABox.Text, out byte a))
            return;

        SetColor(Color.FromArgb(a, r, g, b), syncHex: true, syncRgba: false);
    }

    private static bool TryByte(string text, out byte value) =>
        byte.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!ColorPresets.TryParse(HexBox.Text, out var color))
        {
            ErrorText.Text = "认不出这个颜色，按 #RRGGBBAA 或 #RRGGBB 写（十六进制）。";
            return;
        }

        _color = color;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
