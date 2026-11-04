using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features;
using LTools.Views;
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
            new("固定文本", ProbeKind.text),
            new("变量", ProbeKind.variable),
            new("选中文本", ProbeKind.selection),
            new("剪贴板", ProbeKind.clipboard),
            new("光标位置", ProbeKind.cursor),
            new("日期时间", ProbeKind.datetime),
            new("询问用户", ProbeKind.user),
            new("截图找图", ProbeKind.image),
            new("屏幕取色", ProbeKind.color),
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
        LoadThumbnail();
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
        LoadThumbnail();
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

        bool rect = kind == ProbeKind.image &&
                    (RegionCombo.SelectedItem as RegionItem)?.Region == ImageRegion.rect;
        RectPanel.Visibility = rect ? Visibility.Visible : Visibility.Collapsed;
        RegionHint.Visibility = rect ? Visibility.Visible : Visibility.Collapsed;

        // 「询问用户」必须真的跑流程才能测，其他取值方式都可以就地试。
        TestBar.Visibility = kind == ProbeKind.user ? Visibility.Collapsed : Visibility.Visible;
        TestHint.Text = kind switch
        {
            ProbeKind.selection => "会向前台应用发送 Ctrl+C 并还原剪贴板（窗口会短暂隐藏）",
            ProbeKind.clipboard => "读取当前剪贴板内容",
            ProbeKind.image => $"用当前屏幕找一次（最多等 {ProbeTest.MaxWaitMs / 1000} 秒，窗口会短暂隐藏）",
            ProbeKind.color => "读取该点当前颜色（窗口会短暂隐藏）",
            ProbeKind.window or ProbeKind.process => "检查当前系统状态",
            _ => "按现在的设置立即求值一次",
        };
        TestResult.Visibility = Visibility.Collapsed;
        TestResult.Text = "";
    }

    private void Region_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ShowPanel();
    }

    // —— 图像 ——

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

    /// <summary>框选截图：截下来的就是模板，同时把搜索区域收紧到框内。</summary>
    private void CaptureShot_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (!ScreenPicker.TryPickRegion(window, out var area, out var image)) return;

        try
        {
            string path = SaveShot(image);
            _ready = false;
            ImagePathBox.Text = ImageLibrary.ToStoredPath(path);
            SelectRegion(ImageRegion.rect);
            SetRect(area.X, area.Y, area.Width, area.Height);
            _ready = true;

            ShowPanel();
            LoadThumbnail();
            ShowTest($"已保存 {Path.GetFileName(path)}（{image.PixelWidth} × {image.PixelHeight}）· 搜索区域已设为该框", true);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(window, "框选截图", "保存模板图失败：" + ex.Message);
        }
    }

    /// <summary>预览是非模态的：重复点预览复用同一个窗口，不会叠出一堆。</summary>
    private ImagePreviewWindow? _preview;

    private void PreviewImage_Click(object sender, RoutedEventArgs e)
    {
        string path = ImageLibrary.Resolve(ImagePathBox.Text);
        if (!File.Exists(path))
        {
            ShowTest("没有可预览的图片。", false);
            return;
        }

        BitmapSource image;
        try
        {
            image = LoadBitmap(path);
        }
        catch (Exception ex)
        {
            ShowTest("预览失败：" + ex.Message, false);
            return;
        }

        string name = Path.GetFileName(path);

        // 复用已有预览：只换图，不新开窗口。
        if (_preview is { IsLoaded: true })
        {
            _preview.SetContent(image, name);
            _preview.Activate();
            return;
        }

        var owner = Window.GetWindow(this);

        // 关键：Show 而不是 ShowDialog，且继承 owner 的 Topmost。
        // 组合动作编辑器经常是 Topmost（从超级面板打开），模态子窗口会被它压住并让编辑器变灰，
        // 看起来就像「编辑窗口没了」。
        _preview = new ImagePreviewWindow(image, name)
        {
            Owner = owner,
            Topmost = owner?.Topmost ?? false,
            ShowInTaskbar = false,
        };
        _preview.Closed += (_, _) => _preview = null;
        _preview.Show();
    }

    private void Thumb_Click(object sender, MouseButtonEventArgs e) => PreviewImage_Click(sender, e);

    /// <summary>只框搜索区域，不动模板图。</summary>
    private void PickRegion_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (!ScreenPicker.TryPickRegion(window, out var area, out _)) return;

        _ready = false;
        SelectRegion(ImageRegion.rect);
        SetRect(area.X, area.Y, area.Width, area.Height);
        _ready = true;
        ShowPanel();
        ShowTest($"搜索区域：{area.X}, {area.Y} · {area.Width} × {area.Height}", true);
    }

    private void ImagePath_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        LoadThumbnail();
    }

    private void LoadThumbnail()
    {
        if (ImageThumb == null) return;

        string text = (ImagePathBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            SetThumb(null, "无图", "还没有模板图，点「框选截图」直接在屏幕上截取。");
            return;
        }

        string path = ImageLibrary.Resolve(text);
        if (!File.Exists(path))
        {
            SetThumb(null, "找不到", "文件不存在：" + text);
            return;
        }

        try
        {
            var bmp = LoadBitmap(path);
            SetThumb(bmp, "", $"{bmp.PixelWidth} × {bmp.PixelHeight} px · {Path.GetFileName(path)} · 点击缩略图可预览");
        }
        catch (Exception ex)
        {
            SetThumb(null, "无法读取", "图片读取失败：" + ex.Message);
        }
    }

    private void SetThumb(BitmapSource? image, string placeholder, string hint)
    {
        ImageThumb.Source = image;
        ImageThumbHint.Text = placeholder;
        ImageThumbHint.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
        ImageFileHint.Text = hint;
        PreviewImageBtn.IsEnabled = image != null;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(Path.GetFullPath(path));
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>模板图存到 resources/images，文件名带时间戳；重名自动加序号。</summary>
    private static string SaveShot(BitmapSource image)
    {
        ImageLibrary.EnsureDir();
        string stem = $"shot-{DateTime.Now:yyyyMMdd-HHmmss}";
        string path = Path.Combine(ImageLibrary.Dir, stem + ".png");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(ImageLibrary.Dir, $"{stem}-{n}.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    private void SetRect(int x, int y, int w, int h)
    {
        RectXBox.Text = x.ToString(CultureInfo.InvariantCulture);
        RectYBox.Text = y.ToString(CultureInfo.InvariantCulture);
        RectWBox.Text = w.ToString(CultureInfo.InvariantCulture);
        RectHBox.Text = h.ToString(CultureInfo.InvariantCulture);
    }

    // —— 颜色 ——

    private void PickColorPoint_Click(object sender, RoutedEventArgs e)
    {
        if (!ScreenPicker.TryPickPoint(Window.GetWindow(this), out int x, out int y, out string hex)) return;

        ColorXBox.Text = x.ToString(CultureInfo.InvariantCulture);
        ColorYBox.Text = y.ToString(CultureInfo.InvariantCulture);

        // 条件里取点几乎总是想「就是这个颜色」：顺手填目标色，要改再改。
        if (ShowColorTarget) ColorHexBox.Text = hex;

        ShowTest($"已取点 {x}, {y} · {hex}", true);
    }

    // —— 就地测试 ——

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        string? error = TryReadCore(out var probe);
        if (error != null)
        {
            ShowTest("请先修正：" + error, false);
            return;
        }
        if (probe.Kind == ProbeKind.user) return;

        // 看屏幕 / 取前台选区时，LTools 自己的窗口必须让开，否则搜到的是自己。
        bool hide = probe.Kind is ProbeKind.image or ProbeKind.color or ProbeKind.cursor
            or ProbeKind.window or ProbeKind.selection;

        TestBtn.IsEnabled = false;
        ShowTest("测试中…", null);

        Action? restore = null;
        try
        {
            if (hide)
            {
                restore = TransientHide.HideAll(Window.GetWindow(this));
                await Task.Delay(180);
            }
            string result = await Task.Run(() => ProbeTest.Run(probe));
            ShowTest(result, true);
        }
        catch (Exception ex)
        {
            ShowTest("测试失败：" + ex.Message, false);
        }
        finally
        {
            restore?.Invoke();
            TestBtn.IsEnabled = true;
        }
    }

    private void ShowTest(string text, bool? ok)
    {
        TestResult.Text = text;
        TestResult.Visibility = Visibility.Visible;
        TestResult.Foreground = ok switch
        {
            true => (Brush)FindResource("TextBrush"),
            false => (Brush)FindResource("DangerBrush"),
            null => (Brush)FindResource("SecondaryTextBrush"),
        };
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
