using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Features;
using Microsoft.Win32;

namespace LTools.Views.Controls;

/// <summary>
/// 取值器编辑控件：条件与 set 右值共用。按「取值方式」切换字段面板。
/// </summary>
public partial class ProbeEditor : UserControl
{
    private bool _ready;

    /// <summary>取值方式改变（供条件编辑器刷新比较符）。</summary>
    public event Action? KindChanged;

    /// <summary>是否显示「目标色 / 容差」——set 的右值不需要目标色，条件需要。</summary>
    public bool ShowColorTarget
    {
        get => ColorTargetPanel.Visibility == Visibility.Visible;
        set => ColorTargetPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>当前取值方式。</summary>
    public ProbeKind SelectedKind => KindCombo.SelectedItem is KindItem k ? k.Kind : ProbeKind.text;

    public ProbeEditor()
    {
        InitializeComponent();

        KindCombo.ItemsSource = new List<KindItem>
        {
            new("文本 / 模板", ProbeKind.text),
            new("变量", ProbeKind.variable),
            new("选中文本", ProbeKind.selection),
            new("剪贴板", ProbeKind.clipboard),
            new("光标位置", ProbeKind.cursor),
            new("日期时间", ProbeKind.datetime),
            new("询问用户", ProbeKind.user),
            new("屏幕图像", ProbeKind.image),
            new("屏幕颜色", ProbeKind.color),
            new("窗口", ProbeKind.window),
            new("进程", ProbeKind.process),
        };
        KindCombo.SelectedIndex = 0;

        RegionCombo.ItemsSource = new List<RegionItem>
        {
            new("前台窗口", ImageRegion.foreground),
            new("整个屏幕", ImageRegion.screen),
            new("固定区域", ImageRegion.rect),
        };
        RegionCombo.SelectedIndex = 0;

        _ready = true;
        ShowPanel();
    }

    /// <summary>载入取值器。</summary>
    public void Load(Probe? probe)
    {
        probe ??= new Probe { Kind = ProbeKind.text };
        _ready = false;

        SelectKind(probe.Kind);
        TextValueBox.Text = probe.Text ?? "";
        VarNameBox.Text = probe.Name ?? "";
        SelectionTimeoutBox.Text = (probe.TimeoutMs > 0 ? probe.TimeoutMs : 800).ToString(CultureInfo.InvariantCulture);
        DateFormatBox.Text = probe.Format ?? "yyyy-MM-dd HH:mm:ss";
        UserPromptBox.Text = probe.Prompt ?? "";
        UserDefaultBox.Text = probe.Default ?? "";

        ImagePathBox.Text = probe.Image ?? "";
        SelectRegion(probe.Region);
        RectXBox.Text = probe.Rect is { Length: >= 4 } r ? r[0].ToString(CultureInfo.InvariantCulture) : "0";
        RectYBox.Text = probe.Rect is { Length: >= 4 } r2 ? r2[1].ToString(CultureInfo.InvariantCulture) : "0";
        RectWBox.Text = probe.Rect is { Length: >= 4 } r3 ? r3[2].ToString(CultureInfo.InvariantCulture) : "0";
        RectHBox.Text = probe.Rect is { Length: >= 4 } r4 ? r4[3].ToString(CultureInfo.InvariantCulture) : "0";
        ImageTimeoutBox.Text = probe.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        ImageIntervalBox.Text = (probe.IntervalMs > 0 ? probe.IntervalMs : 200).ToString(CultureInfo.InvariantCulture);
        ImageScoreBox.Text = probe.MinScore.ToString("0.00", CultureInfo.InvariantCulture);

        ColorXBox.Text = probe.X ?? "";
        ColorYBox.Text = probe.Y ?? "";
        ColorHexBox.Text = probe.Color ?? "#FFFFFF";
        ColorToleranceBox.Text = probe.Tolerance.ToString(CultureInfo.InvariantCulture);

        WindowTitleBox.Text = probe.WindowTitle ?? "";
        WindowClassBox.Text = probe.WindowClass ?? "";
        ProcessNameBox.Text = probe.ProcessName ?? "";

        _ready = true;
        ShowPanel();
        UpdateColorPreview();
    }

    /// <summary>读出取值器；失败返回 false 并给出可读原因。</summary>
    public bool TryRead(out Probe probe, out string error)
    {
        string? message = TryReadCore(out probe);
        error = message ?? "";
        return message == null;
    }

    /// <summary>返回 null 表示成功；否则为可读错误（避免在 out 参数上写本地函数）。</summary>
    private string? TryReadCore(out Probe probe)
    {
        probe = new Probe { Kind = SelectedKind };

        switch (probe.Kind)
        {
            case ProbeKind.text:
                probe.Text = TextValueBox.Text;
                break;

            case ProbeKind.variable:
                probe.Name = VarNameBox.Text.Trim();
                if (probe.Name.Length == 0) return "请填写变量名。";
                break;

            case ProbeKind.selection:
                if (!TryInt(SelectionTimeoutBox, 800, 100, 5000, "选中文本等待时间",
                            out int selMs, out string selError)) return selError;
                probe.TimeoutMs = selMs;
                break;

            case ProbeKind.datetime:
                probe.Format = DateFormatBox.Text.Trim();
                try { _ = DateTime.Now.ToString(probe.Format, CultureInfo.InvariantCulture); }
                catch (FormatException) { return "日期格式无效。"; }
                break;

            case ProbeKind.user:
                probe.Prompt = UserPromptBox.Text;
                probe.Default = UserDefaultBox.Text;
                break;

            case ProbeKind.image:
            {
                string path = ImagePathBox.Text.Trim();
                if (path.Length == 0) return "请选择模板图片。";
                if (!File.Exists(ImageLibrary.Resolve(path))) return "模板图片不存在，请重新选择。";
                probe.Image = path;
                probe.Region = (RegionCombo.SelectedItem as RegionItem)?.Region ?? ImageRegion.foreground;

                if (!TryInt(ImageTimeoutBox, 0, 0, 600_000, "图像等待时间",
                            out int timeout, out string timeoutError)) return timeoutError;
                if (!TryInt(ImageIntervalBox, 200, 50, 5000, "图像轮询间隔",
                            out int interval, out string intervalError)) return intervalError;
                if (!TryDouble(ImageScoreBox, 0.85, 0.30, 0.99, "最低相似度",
                               out double score, out string scoreError)) return scoreError;
                probe.TimeoutMs = timeout;
                probe.IntervalMs = interval;
                probe.MinScore = score;

                if (probe.Region == ImageRegion.rect)
                {
                    if (!TryInt(RectXBox, 0, -100_000, 100_000, "区域 X", out int x, out string xError)) return xError;
                    if (!TryInt(RectYBox, 0, -100_000, 100_000, "区域 Y", out int y, out string yError)) return yError;
                    if (!TryInt(RectWBox, 0, 1, 100_000, "区域宽", out int w, out string wError)) return wError;
                    if (!TryInt(RectHBox, 0, 1, 100_000, "区域高", out int h, out string hError)) return hError;
                    probe.Rect = new[] { x, y, w, h };
                }
                break;
            }

            case ProbeKind.color:
            {
                probe.X = ColorXBox.Text.Trim();
                probe.Y = ColorYBox.Text.Trim();
                if (probe.X.Length == 0 || probe.Y.Length == 0) return "请填写颜色坐标。";
                if (!TryInt(ColorToleranceBox, 12, 0, 255, "颜色容差",
                            out int tolerance, out string toleranceError)) return toleranceError;
                probe.Tolerance = tolerance;
                probe.Color = ColorHexBox.Text.Trim();
                if (ShowColorTarget && !TryParseHex(probe.Color ?? "", out _))
                    return "目标颜色请填写 #RRGGBB。";
                break;
            }

            case ProbeKind.window:
                probe.WindowTitle = WindowTitleBox.Text.Trim();
                probe.WindowClass = WindowClassBox.Text.Trim();
                if (probe.WindowTitle.Length == 0 && probe.WindowClass.Length == 0)
                    return "窗口标题和类名至少填一个。";
                break;

            case ProbeKind.process:
                probe.ProcessName = ProcessNameBox.Text.Trim();
                if (probe.ProcessName.Length == 0) return "请填写进程名。";
                break;
        }

        return null;
    }

    // —— 面板切换 ——

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ShowPanel();
        KindChanged?.Invoke();
    }

    private void ShowPanel()
    {
        var kind = SelectedKind;
        var panels = new Dictionary<ProbeKind, UIElement>
        {
            [ProbeKind.text] = P_Text,
            [ProbeKind.variable] = P_Variable,
            [ProbeKind.selection] = P_Selection,
            [ProbeKind.clipboard] = P_Clipboard,
            [ProbeKind.cursor] = P_Cursor,
            [ProbeKind.datetime] = P_DateTime,
            [ProbeKind.user] = P_User,
            [ProbeKind.image] = P_Image,
            [ProbeKind.color] = P_Color,
            [ProbeKind.window] = P_Window,
            [ProbeKind.process] = P_Process,
        };

        foreach (var p in panels.Values) p.Visibility = Visibility.Collapsed;
        if (panels.TryGetValue(kind, out var selected)) selected.Visibility = Visibility.Visible;
        RectPanel.Visibility = kind == ProbeKind.image &&
                               (RegionCombo.SelectedItem as RegionItem)?.Region == ImageRegion.rect
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Region_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ShowPanel();
    }

    // —— 图像 / 颜色 ——

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择模板图片",
            Filter = "图像|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        ImageLibrary.EnsureDir();
        ImagePathBox.Text = ImageLibrary.ToStoredPath(dlg.FileName);
    }

    private void ColorHex_Changed(object sender, TextChangedEventArgs e) => UpdateColorPreview();

    private void UpdateColorPreview()
    {
        if (ColorPreview == null) return;
        ColorPreview.Background = TryParseHex(ColorHexBox.Text, out var c)
            ? new SolidColorBrush(c)
            : Brushes.Transparent;
    }

    private static bool TryParseHex(string text, out Color color)
    {
        color = Colors.White;
        text = (text ?? "").Trim().TrimStart('#');
        if (text.Length != 6) return false;
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        color = Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
        return true;
    }

    // —— 小工具 ——

    private static bool TryInt(TextBox box, int fallback, int min, int max, string what,
                               out int value, out string error)
    {
        error = "";
        string text = (box.Text ?? "").Trim();
        if (text.Length == 0) { value = fallback; return true; }
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"{what}必须是整数。";
            return false;
        }
        value = Math.Clamp(value, min, max);
        return true;
    }

    private static bool TryDouble(TextBox box, double fallback, double min, double max, string what,
                                  out double value, out string error)
    {
        error = "";
        string text = (box.Text ?? "").Trim();
        if (text.Length == 0) { value = fallback; return true; }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = $"{what}必须是数字。";
            return false;
        }
        value = Math.Clamp(value, min, max);
        return true;
    }

    private void SelectKind(ProbeKind kind)
    {
        for (int i = 0; i < KindCombo.Items.Count; i++)
        {
            if (KindCombo.Items[i] is KindItem k && k.Kind == kind)
            {
                KindCombo.SelectedIndex = i;
                return;
            }
        }
        KindCombo.SelectedIndex = 0;
    }

    private void SelectRegion(ImageRegion region)
    {
        for (int i = 0; i < RegionCombo.Items.Count; i++)
        {
            if (RegionCombo.Items[i] is RegionItem r && r.Region == region)
            {
                RegionCombo.SelectedIndex = i;
                return;
            }
        }
        RegionCombo.SelectedIndex = 0;
    }

    private sealed record KindItem(string Label, ProbeKind Kind)
    {
        public override string ToString() => Label;
    }

    private sealed record RegionItem(string Label, ImageRegion Region)
    {
        public override string ToString() => Label;
    }
}
