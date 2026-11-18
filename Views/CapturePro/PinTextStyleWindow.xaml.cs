using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图文字样式的默认值对话框（照上游设置页 <c>pinTextStyleItems</c>）：最大文字宽 / 默认字体 /
/// 默认字号 / 文字颜色 / 背景颜色 + 一份真渲染的预览。
/// </summary>
/// <remarks>
/// <para>
/// 上游这五项是 `pin_to_screen/text_*`（<c>settingscatalog.cpp:804</c>）：最大宽 100–2000（默认 1024）、
/// 字号 8–96（默认 16）、字体空 = 系统字体、两种颜色 <c>system</c> = 跟随主题，
/// 「恢复默认」对应上游设置里的「重置文字样式」。
/// </para>
/// <para>
/// 值只写**新贴**的文字：<see cref="PinTextStore.Save"/> 落 <c>configs/settings.json</c> 的 <c>PinText</c> 段，
/// 已经在屏幕上的贴图用记录里的 <see cref="PinTextSnapshot"/>，所以改这里不会让旧贴图变样。
/// </para>
/// </remarks>
public partial class PinTextStyleWindow : Window
{
    /// <summary>预览用的样例文字（带中英数，能看出字号 / 字体 / 折行）。</summary>
    private const string SampleText = "贴图文字样式预览 Preview 123\n这行会按最大文字宽自动折行，超出宽度时换行而不是撑宽。";

    private static readonly Color[] PaletteColors =
    {
        Color.FromRgb(0x18, 0x18, 0x1B), Color.FromRgb(0x71, 0x71, 0x7A), Colors.White,
        Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xEA, 0xB3, 0x08),
        Color.FromRgb(0x22, 0xC5, 0x5E), Color.FromRgb(0x06, 0xB6, 0xD4), Color.FromRgb(0x3B, 0x82, 0xF6),
        Color.FromRgb(0x8B, 0x5C, 0xF5), Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x00, 0x00, 0x00),
    };

    private readonly List<Border> _textSwatches = new();
    private readonly List<Border> _backgroundSwatches = new();

    private Color _textColor = Color.FromRgb(0x18, 0x18, 0x1B);
    private Color _backgroundColor = Colors.White;
    private bool _syncing;

    private PinTextStyleWindow(PinTextLimits limits)
    {
        InitializeComponent();

        BuildPalette(TextPalette, _textSwatches, color => SetTextColor(color, syncHex: true));
        BuildPalette(BackgroundPalette, _backgroundSwatches,
            color => SetBackgroundColor(color, syncHex: true));
        Fill(limits);
    }

    /// <summary>打开对话框；按「确定」保存并返回 <c>true</c>，取消返回 <c>false</c>。</summary>
    /// <param name="owner">宿主窗口（无宿主时置顶，例如从贴图右键进来）。</param>
    public static bool Show(Window? owner)
    {
        var dlg = new PinTextStyleWindow(PinTextStore.Limits())
        {
            Owner = owner,
        };
        if (owner == null)
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.Topmost = true;
        }
        return dlg.ShowDialog() == true;
    }

    // ============ 填 / 读 ============

    private void Fill(PinTextLimits limits)
    {
        _syncing = true;

        WidthBox.Text = limits.MaximumWidth.ToString(CultureInfo.InvariantCulture);
        SizeBox.Text = ((int)Math.Round(limits.FontSize)).ToString(CultureInfo.InvariantCulture);

        BuildFonts(limits.FontFamily);

        var theme = PinTextStyle.Current();
        _textColor = limits.TextColor ?? theme.TextColor;
        _backgroundColor = limits.BackgroundColor ?? theme.BackgroundColor;

        TextThemeBox.IsChecked = limits.TextColor == null;
        BackgroundThemeBox.IsChecked = limits.BackgroundColor == null;

        TextHexBox.Text = FormatHex(_textColor);
        BackgroundHexBox.Text = FormatHex(_backgroundColor);

        _syncing = false;
        SyncColorUi();
        UpdatePreview();
    }

    /// <summary>字体下拉：「系统默认」+ 所有系统字体；当前值不在系统列表里也补一项，别悄悄换成第一项。</summary>
    private void BuildFonts(string current)
    {
        FontBox.Items.Clear();
        FontBox.Items.Add(new ComboBoxItem { Content = "系统默认", Tag = "" });

        var families = new List<string>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            string name = family.Source;
            if (!string.IsNullOrWhiteSpace(name)) families.Add(name);
        }

        families.Sort(StringComparer.CurrentCultureIgnoreCase);
        if (current.Length > 0 && !families.Contains(current, StringComparer.OrdinalIgnoreCase))
            families.Insert(0, current);

        int selected = 0;
        foreach (string name in families)
        {
            FontBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase) &&
                current.Length > 0)
                selected = FontBox.Items.Count - 1;
        }

        FontBox.SelectedIndex = selected;
    }

    private string SelectedFont()
    {
        return FontBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "";
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (sender == TextThemeBox || sender == BackgroundThemeBox) SyncColorUi();
        UpdatePreview();
    }

    private void TextHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (!TryParseHex(TextHexBox.Text, out var color)) return;
        SetTextColor(color, syncHex: false);
        UpdatePreview();
    }

    private void BackgroundHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (!TryParseHex(BackgroundHexBox.Text, out var color)) return;
        SetBackgroundColor(color, syncHex: false);
        UpdatePreview();
    }

    // ============ 颜色 ============

    private void BuildPalette(Panel host, List<Border> swatches, Action<Color> pick)
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
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var captured = color;
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                pick(captured);
            };
            swatches.Add(swatch);
            host.Children.Add(swatch);
        }
    }

    private void SetTextColor(Color color, bool syncHex)
    {
        _textColor = color;
        _syncing = true;
        TextThemeBox.IsChecked = false;         // 点色板 = 明确要这个颜色，不再跟随主题
        if (syncHex) TextHexBox.Text = FormatHex(color);
        _syncing = false;
        SyncColorUi();
        UpdatePreview();
    }

    private void SetBackgroundColor(Color color, bool syncHex)
    {
        _backgroundColor = color;
        _syncing = true;
        BackgroundThemeBox.IsChecked = false;
        if (syncHex) BackgroundHexBox.Text = FormatHex(color);
        _syncing = false;
        SyncColorUi();
        UpdatePreview();
    }

    /// <summary>「跟随主题」勾上就禁用色板 / hex，并显示主题里当前那个颜色。</summary>
    private void SyncColorUi()
    {
        bool textTheme = TextThemeBox.IsChecked == true;
        bool backgroundTheme = BackgroundThemeBox.IsChecked == true;

        if (textTheme)
        {
            var theme = PinTextStyle.Current();
            SyncHex(TextHexBox, theme.TextColor);
            MarkSelected(_textSwatches, theme.TextColor);
        }
        else
        {
            MarkSelected(_textSwatches, _textColor);
        }

        if (backgroundTheme)
        {
            var theme = PinTextStyle.Current();
            SyncHex(BackgroundHexBox, theme.BackgroundColor);
            MarkSelected(_backgroundSwatches, theme.BackgroundColor);
        }
        else
        {
            MarkSelected(_backgroundSwatches, _backgroundColor);
        }

        TextPalette.IsEnabled = !textTheme;
        TextPalette.Opacity = textTheme ? 0.45 : 1;
        TextHexBox.IsEnabled = !textTheme;
        BackgroundPalette.IsEnabled = !backgroundTheme;
        BackgroundPalette.Opacity = backgroundTheme ? 0.45 : 1;
        BackgroundHexBox.IsEnabled = !backgroundTheme;
    }

    private void SyncHex(TextBox box, Color color)
    {
        string text = FormatHex(color);
        if (box.Text == text) return;
        _syncing = true;
        box.Text = text;
        _syncing = false;
    }

    private static void MarkSelected(List<Border> swatches, Color color)
    {
        for (int i = 0; i < swatches.Count; i++)
        {
            bool selected = i < PaletteColors.Length && PaletteColors[i] == color;
            swatches[i].BorderThickness = new Thickness(selected ? 2 : 1);
            swatches[i].BorderBrush = selected
                ? (Brush)Application.Current.FindResource("PrimaryBrush")
                : (Brush)Application.Current.FindResource("BorderBrush");
        }
    }

    private static string FormatHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>认 <c>#RRGGBB</c> / <c>RRGGBB</c>（alpha 不在这里编辑，透明色请从贴图里来）。</summary>
    private static bool TryParseHex(string text, out Color color)
    {
        var parsed = PinTextRules.ParseColor(text);
        if (parsed is { } value)
        {
            color = value;
            return true;
        }

        color = default;
        return false;
    }

    // ============ 预览 ============

    /// <summary>按控件里的值拼一份样式（数字填错就按默认值预览，不在这里拦人）。</summary>
    private PinTextStyle BuildStyle()
    {
        var style = PinTextStyle.Current();
        style.MaximumWidth = int.TryParse(WidthBox.Text.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int width)
            ? PinTextRules.NormalizeMaximumWidth(width)
            : PinTextRules.DefaultMaximumWidth;
        style.FontSize = int.TryParse(SizeBox.Text.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int size)
            ? PinTextRules.NormalizeFontSize(size)
            : PinTextRules.DefaultFontSize;

        string font = SelectedFont();
        style.FontFamily = font.Length == 0 ? style.FontFamily : font;
        style.TextColor = TextThemeBox.IsChecked == true ? style.TextColor : _textColor;
        style.BackgroundColor = BackgroundThemeBox.IsChecked == true
            ? style.BackgroundColor
            : _backgroundColor;
        return style;
    }

    private void UpdatePreview()
    {
        try
        {
            PreviewImage.Source = PinCardRenderer.RenderText(SampleText, BuildStyle(), 1).Image;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PinText", "预览渲染失败：" + ex.Message);
            PreviewImage.Source = null;
        }
    }

    // ============ 按钮 ============

    private void Reset_Click(object sender, RoutedEventArgs e) => ResetToDefaults();

    /// <summary>把控件改回上游默认值（设置页「重置文字样式」）。</summary>
    internal void ResetToDefaults() => Fill(PinTextLimits.Default);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (TrySave()) DialogResult = true;
    }

    /// <summary>校验并落盘；数字填错就标红并返回 <c>false</c>（不关窗）。</summary>
    internal bool TrySave()
    {
        bool widthOk = int.TryParse(WidthBox.Text.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int width);
        bool sizeOk = int.TryParse(SizeBox.Text.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int size);

        WidthBox.BorderBrush = !widthOk ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("BorderBrush");
        SizeBox.BorderBrush = !sizeOk ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("BorderBrush");
        if (!widthOk || !sizeOk) return false;

        var limits = new PinTextLimits(
            PinTextRules.NormalizeMaximumWidth(width),
            PinTextRules.NormalizeFontSize(size),
            PinTextRules.NormalizeFontFamily(SelectedFont()),
            TextThemeBox.IsChecked == true ? null : _textColor,
            BackgroundThemeBox.IsChecked == true ? null : _backgroundColor);

        PinTextStore.Save(limits);
        return true;
    }
}
