using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Core;
using LTools.Features;
using LTools.Views;

namespace LTools.Views.Controls;

/// <summary>
/// 取值器编辑控件：条件与 set 右值共用。按「取值方式」切换字段面板。
/// </summary>
public partial class ProbeEditor : UserControl
{
    private bool _ready;

    /// <summary>正在下载 PP-OCRv6 高精度模型时的取消源；null 表示没在下载。</summary>
    private CancellationTokenSource? _downloadCts;

    /// <summary>取值方式改变（供条件编辑器刷新比较符）。</summary>
    public event Action? KindChanged;

    /// <summary>文本内容变化（供条件编辑器在「文本 ⇄ 变量」之间切换比较符）。</summary>
    public event Action? ValueChanged;

    /// <summary>是否显示「目标色 / 容差」——set 的右值不需要目标色，条件需要。</summary>
    public bool ShowColorTarget
    {
        get => ColorTargetPanel.Visibility == Visibility.Visible;
        set => ColorTargetPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>当前取值方式。</summary>
    public ProbeKind SelectedKind => KindCombo.SelectedItem is KindItem k ? k.Kind : ProbeKind.text;

    /// <summary>
    /// 实际语义：文本整段只写 <c>${变量}</c> 时按 <see cref="ProbeKind.variable"/> 处理，
    /// 这样条件里仍可用「为真 / 为空 / 大于」等变量判定，运行时也保留 found / number 等属性。
    /// </summary>
    public ProbeKind EffectiveKind =>
        SelectedKind == ProbeKind.text && TryParseSingleVariable(TextValueBox.Text, out _)
            ? ProbeKind.variable
            : SelectedKind;

    public ProbeEditor()
    {
        InitializeComponent();

        KindCombo.ItemsSource = new List<KindItem>
        {
            new("文本 / 变量", ProbeKind.text),
            new("选中文本", ProbeKind.selection),
            new("剪贴板", ProbeKind.clipboard),
            new("光标位置", ProbeKind.cursor),
            new("日期时间", ProbeKind.datetime),
            new("输入文本", ProbeKind.user),
            new("截图", ProbeKind.screenshot),
            new("截图找图", ProbeKind.image),
            new("文字识别", ProbeKind.ocr),
            new("找文字（区域 + 坐标）", ProbeKind.textSearch),
            new("读速记（分类 + 关键字）", ProbeKind.note),
            new("屏幕取色", ProbeKind.color),
            new("窗口", ProbeKind.window),
            new("进程", ProbeKind.process),
            new("上一步的错误", ProbeKind.lastError),
        };
        KindCombo.SelectedIndex = 0;

        RegionCombo.ItemsSource = new List<RegionItem>
        {
            new("前台窗口", ImageRegion.foreground),
            new("整个屏幕", ImageRegion.screen),
            new("固定区域", ImageRegion.rect),
        };
        RegionCombo.SelectedIndex = 0;

        // 读速记：分类下拉列出磁盘上已有的分类（可编辑，直接输入新分类）
        try
        {
            NoteCategoryCombo.Items.Add(NoteRepository.AllCategories);
            foreach (string category in new NoteRepository(AppPaths.NotesDir).Categories())
                NoteCategoryCombo.Items.Add(category);
        }
        catch (Exception ex)
        {
            AppLog.Warn("速记", "读取速记分类失败：" + ex.Message);
        }

        OcrEngineCombo.ItemsSource = new List<OcrEngineItem>
        {
            new("PP-OCRv6 快速（内置，推荐）", "v6tiny"),
            new("系统 OCR（最快，最省内存）", null),
            new("PP-OCRv6 高精度（需下载 29.8 MB）", "v6small"),
        };
        OcrEngineCombo.SelectedIndex = 0;
        ScreenshotModeCombo.SelectedIndex = 0;

        _ready = true;
        ShowPanel();
        UpdateOcrEngineState();
    }

    /// <summary>载入取值器。</summary>
    public void Load(Probe? probe)
    {
        probe ??= new Probe { Kind = ProbeKind.text };
        _ready = false;

        SelectKind(probe.Kind == ProbeKind.variable ? ProbeKind.text : probe.Kind);
        // 变量与文本在 UI 上合并：整段 ${name} 就是变量引用
        TextValueBox.Text = probe.Kind == ProbeKind.variable
            ? "${" + (probe.Name ?? "") + "}"
            : probe.Text ?? "";
        SelectionTimeoutBox.Text = (probe.TimeoutMs > 0 ? probe.TimeoutMs : 800).ToString(CultureInfo.InvariantCulture);
        DateFormatBox.Text = probe.Format ?? "yyyy-MM-dd HH:mm:ss";
        UserPromptBox.Text = probe.Prompt ?? "";
        UserDefaultBox.Text = probe.Default ?? "";
        UserMultilineBox.IsChecked = probe.Multiline;

        ImagePicker.Path = probe.Image ?? "";
        SelectRegion(probe.Region);
        ScreenshotModeCombo.SelectedIndex = probe.ScreenshotMode switch
        {
            ScreenshotMode.foreground => 1,
            ScreenshotMode.screen => 2,
            ScreenshotMode.rect => 3,
            _ => 0,
        };
        ScreenshotRectBox.Text = probe.ScreenshotRect ?? "";
        OcrImageBox.Text = probe.OcrImage ?? "";
        TextSearchBox.Text = probe.Text ?? "";
        TextSearchTimeoutBox.Text = Math.Clamp(probe.TimeoutMs, 0, 600_000).ToString(CultureInfo.InvariantCulture);
        TextSearchIntervalBox.Text = (probe.IntervalMs > 0 ? probe.IntervalMs : TextLocator.DefaultIntervalMs)
            .ToString(CultureInfo.InvariantCulture);
        NoteCategoryCombo.Text = probe.NoteCategory ?? "";
        NoteKeywordBox.Text = probe.Text ?? "";
        // 已有 OCR 步骤保留自己的引擎；新建步骤沿用上次的选择（默认 PP-OCRv6 快速）
        SelectOcrEngine(probe.Kind is ProbeKind.ocr or ProbeKind.textSearch ? probe.OcrEngine : OcrEnginePreference.DefaultForNewProbe);
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
        SetWindowAdvanced(!string.IsNullOrWhiteSpace(probe.WindowClass));
        ProcessNameBox.Text = probe.ProcessName ?? "";

        _ready = true;
        ShowPanel();
        UpdateOcrEngineState();
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
            {
                string text = TextValueBox.Text ?? "";
                // 整段只写一个 ${变量} → 按变量取值（保留 found / number 等结构化属性）
                if (TryParseSingleVariable(text, out string name))
                {
                    probe.Kind = ProbeKind.variable;
                    probe.Name = name;
                }
                else
                {
                    probe.Text = text;
                }
                break;
            }

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
                probe.Multiline = UserMultilineBox.IsChecked == true;
                break;

            case ProbeKind.screenshot:
                probe.ScreenshotMode = ScreenshotModeCombo.SelectedIndex switch
                {
                    1 => ScreenshotMode.foreground,
                    2 => ScreenshotMode.screen,
                    3 => ScreenshotMode.rect,
                    _ => ScreenshotMode.interactive,
                };
                if (probe.ScreenshotMode == ScreenshotMode.rect)
                {
                    string spec = ScreenshotRectBox.Text.Trim();
                    if (spec.Length == 0) return "请填写截图区域（x,y,w,h），或点「框选区域…」。";
                    // 含模板时只能等运行时才知道实际数值；字面值在这里先校验一次，免得跑起来才报错。
                    if (!spec.Contains('$') && !ScreenshotProbe.TryParseRect(spec, out _, out _, out _, out _))
                        return "截图区域要写成 x,y,w,h（四个整数，宽高大于 0）。";
                    probe.ScreenshotRect = spec;
                }
                break;

            case ProbeKind.image:
            {
                string path = ImagePicker.Path;
                if (path.Length == 0) return "请选择模板图片。";
                if (!File.Exists(ImageLibrary.Resolve(path))) return "模板图片不存在，请重新选择。";
                probe.Image = path;
                if (ReadRegion(probe) is string regionError) return regionError;

                if (!TryInt(ImageTimeoutBox, 0, 0, 600_000, "图像等待时间",
                            out int timeout, out string timeoutError)) return timeoutError;
                if (!TryInt(ImageIntervalBox, 200, 50, 5000, "图像轮询间隔",
                            out int interval, out string intervalError)) return intervalError;
                if (!TryDouble(ImageScoreBox, 0.85, 0.30, 0.99, "最低相似度",
                               out double score, out string scoreError)) return scoreError;
                probe.TimeoutMs = timeout;
                probe.IntervalMs = interval;
                probe.MinScore = score;
                break;
            }

            case ProbeKind.ocr:
                probe.OcrEngine = (OcrEngineCombo.SelectedItem as OcrEngineItem)?.Value;
                probe.OcrImage = OcrImageBox.Text.Trim();
                if (probe.OcrImage.Length == 0)
                    return "请填写要识别的图片（先用「截图」拿一张图，再填它的变量，例如 ${截图}）。";
                break;

            case ProbeKind.textSearch:
            {
                string needle = TextSearchBox.Text.Trim();
                if (needle.Length == 0) return "请填写要在区域里查找的文字。";
                probe.Text = needle;
                probe.OcrEngine = (OcrEngineCombo.SelectedItem as OcrEngineItem)?.Value;
                if (ReadRegion(probe) is string textRegionError) return textRegionError;

                if (!TryInt(TextSearchTimeoutBox, 0, 0, 600_000, "找字等待时间",
                            out int textTimeout, out string textTimeoutError)) return textTimeoutError;
                if (!TryInt(TextSearchIntervalBox, TextLocator.DefaultIntervalMs, 50, 5000, "找字轮询间隔",
                            out int textInterval, out string textIntervalError)) return textIntervalError;
                probe.TimeoutMs = textTimeout;
                probe.IntervalMs = textInterval;
                break;
            }

            case ProbeKind.note:
                probe.NoteCategory = NoteCategoryCombo.Text.Trim();
                probe.Text = NoteKeywordBox.Text.Trim();
                break;

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

    /// <summary>读出共用的屏幕区域（图像搜索 / OCR 截图）；区域非法时返回可读错误。</summary>
    private string? ReadRegion(Probe probe)
    {
        probe.Region = (RegionCombo.SelectedItem as RegionItem)?.Region ?? ImageRegion.foreground;
        if (probe.Region != ImageRegion.rect) return null;

        if (!TryInt(RectXBox, 0, -100_000, 100_000, "区域 X", out int x, out string xError)) return xError;
        if (!TryInt(RectYBox, 0, -100_000, 100_000, "区域 Y", out int y, out string yError)) return yError;
        if (!TryInt(RectWBox, 0, 1, 100_000, "区域宽", out int w, out string wError)) return wError;
        if (!TryInt(RectHBox, 0, 1, 100_000, "区域高", out int h, out string hError)) return hError;
        probe.Rect = new[] { x, y, w, h };
        return null;
    }

    // —— 面板切换 ——

    private void TextValue_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        ValueChanged?.Invoke();
    }

    /// <summary>整段只是一个 <c>${变量}</c> 时取出变量名（忽略首尾空白）。</summary>
    public static bool TryParseSingleVariable(string? text, out string name)
        => LTools.Features.Template.TrySingleVariable(text, out name);

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
            [ProbeKind.selection] = P_Selection,
            [ProbeKind.clipboard] = P_Clipboard,
            [ProbeKind.cursor] = P_Cursor,
            [ProbeKind.datetime] = P_DateTime,
            [ProbeKind.user] = P_User,
            [ProbeKind.screenshot] = P_Screenshot,
            [ProbeKind.image] = P_Image,
            [ProbeKind.ocr] = P_Ocr,
            [ProbeKind.textSearch] = P_TextSearch,
            [ProbeKind.note] = P_Note,
            [ProbeKind.color] = P_Color,
            [ProbeKind.window] = P_Window,
            [ProbeKind.process] = P_Process,
            [ProbeKind.lastError] = P_LastError,
        };

        foreach (var p in panels.Values) p.Visibility = Visibility.Collapsed;
        if (panels.TryGetValue(kind, out var selected)) selected.Visibility = Visibility.Visible;

        // 截图只在自己框选时才算「必须跑流程」；前台窗口 / 整个屏幕 / 固定区域可以就地试。
        bool interactiveShot = kind == ProbeKind.screenshot &&
                               ScreenshotModeCombo.SelectedIndex == 0;
        bool screenshotRect = kind == ProbeKind.screenshot &&
                              ScreenshotModeCombo.SelectedIndex == 3;
        ScreenshotRectPanel.Visibility = screenshotRect ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotModeHint.Text = kind == ProbeKind.screenshot
            ? ScreenshotModeHintText(ScreenshotModeCombo.SelectedIndex)
            : "";

        // 截图找图 / 找文字的搜索范围与截图无关，只有这两种取值方式才有区域设置。
        bool hasRegion = kind is ProbeKind.image or ProbeKind.textSearch;
        RegionSection.Visibility = hasRegion ? Visibility.Visible : Visibility.Collapsed;
        RegionLabel.Text = kind == ProbeKind.textSearch ? "查找区域" : "搜索区域";
        PickRegionBtn.ToolTip = "在屏幕上框出一块，自动填入 x / y / w / h";

        bool rect = hasRegion &&
                    (RegionCombo.SelectedItem as RegionItem)?.Region == ImageRegion.rect;
        RectPanel.Visibility = rect ? Visibility.Visible : Visibility.Collapsed;
        RegionHint.Visibility = rect ? Visibility.Visible : Visibility.Collapsed;
        RegionHint.Text = kind == ProbeKind.textSearch
            ? "只在框内找字（更快更准）；目标位置会变就改成「前台窗口」。"
            : "只在框内查找（更快更准）；目标位置会变就改成「前台窗口」。";

        OcrEngineSection.Visibility = kind is ProbeKind.ocr or ProbeKind.textSearch
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 「输入文本 / 截图 · 交互框选」必须真的跑流程才能测，其他取值方式都可以就地试。
        bool interactive = kind == ProbeKind.user || interactiveShot;
        TestBar.Visibility = interactive ? Visibility.Collapsed : Visibility.Visible;
        TestHint.Text = kind switch
        {
            ProbeKind.selection => "通过 UI Automation 读取当前选区（不模拟按键，窗口会短暂最小化）",
            ProbeKind.clipboard => "读取当前剪贴板内容",
            ProbeKind.image => $"用当前屏幕找一次（最多等 {ProbeTest.MaxWaitMs / 1000} 秒，窗口会短暂最小化）",
            ProbeKind.ocr => "识别图片变量里的图片（编辑时变量通常还没有值，跑流程才有）",
            ProbeKind.textSearch => $"在当前屏幕上找一次（最多等 {ProbeTest.MaxWaitMs / 1000} 秒，窗口会短暂最小化）",
            ProbeKind.note => "读取速记目录里最近修改的一条（编辑时就能测）",
            ProbeKind.screenshot => "按当前方式截一张图（窗口会短暂最小化）",
            ProbeKind.color => "读取该点当前颜色（窗口会短暂最小化）",
            ProbeKind.window or ProbeKind.process => "检查当前系统状态",
            ProbeKind.lastError => "显示上一步被「继续下一步」吞掉的错误（编辑时通常为空）",
            _ => "按现在的设置立即求值一次",
        };
        TestResult.Visibility = Visibility.Collapsed;
        TestResult.Text = "";
    }

    /// <summary>截图方式的说明：把「什么时候需要人」「截到哪儿」讲清楚。</summary>
    private static string ScreenshotModeHintText(int index) => index switch
    {
        1 => "截当前前台窗口的实际可见范围（不含窗口阴影边距）；如果前台正好是 LTools 自己，就会截到 LTools。",
        2 => "截整个虚拟桌面（多显示器时为所有屏幕的合集）。",
        3 => "按填好的区域截；区域可以引用变量，例如先用「截图」框一次，再用 ${截图} 同一块区域再截一次。",
        _ => "流程跑到这一步时，LTools 会让开并冻结屏幕，由你在屏幕上框一块（可吸附窗口 / 控件，和截图工具一样）。框完自动继续，不用再按确定。",
    };

    private void Region_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ShowPanel();
    }

    private void ScreenshotMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ShowPanel();
        ValueChanged?.Invoke();
    }

    /// <summary>框一块当作截图区域，填成 x,y,w,h。</summary>
    private void ScreenshotPickRect_Click(object sender, RoutedEventArgs e)
    {
        if (!ScreenPicker.TryPickRegion(Window.GetWindow(this), out var area, out _)) return;

        _ready = false;
        ScreenshotModeCombo.SelectedIndex = 3;
        ScreenshotRectBox.Text = $"{area.X},{area.Y},{area.Width},{area.Height}";
        _ready = true;
        ShowPanel();
    }

    // —— OCR 引擎 ——

    private void OcrEngine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateOcrEngineState();
        OcrEnginePreference.Save((OcrEngineCombo.SelectedItem as OcrEngineItem)?.Value);
    }

    /// <summary>按所选引擎给出说明 / 下载入口：模型缺了先说清楚，而不是等运行时才报错。</summary>
    private void UpdateOcrEngineState()
    {
        if (OcrEngineHint == null || _downloadCts != null) return;

        PaddleOcr.Tier? tier = PaddleOcr.ParseTier((OcrEngineCombo.SelectedItem as OcrEngineItem)?.Value);
        OcrDownloadPanel.Visibility = Visibility.Collapsed;

        if (tier == null)
        {
            OcrEngineHint.Text = "系统自带（Windows.Media.Ocr），零下载、十几毫秒出结果，内存占用最小；12–14px 中文与数字、标点容易认错。会记住这次的选择。";
            return;
        }

        if (PaddleOcr.IsReady(tier.Value))
        {
            OcrEngineHint.Text = tier == PaddleOcr.Tier.Tiny
                ? "内置模型（随安装包），首次识别加载约 0.2 秒，单张约 0.4 秒；小字、数字明显更准。新建 OCR 步骤默认用它。"
                : "模型已就位，单张约 1.2 秒；同图基准里 15 个场景全部识别正确。";
            return;
        }

        OcrEngineHint.Text = "模型还没下载，现在识别会报错。下载一次即可——模型存在数据目录，升级覆盖程序不会丢。";
        OcrDownloadPanel.Visibility = Visibility.Visible;
    }

    private async void OcrDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCts != null) return;

        _downloadCts = new CancellationTokenSource();
        OcrDownloadButton.IsEnabled = false;
        OcrEngineCombo.IsEnabled = false;
        OcrDownloadHint.Text = "准备下载…";

        long total = OcrModelStore.SmallTotalBytes;
        var progress = new Progress<double>(f => OcrDownloadHint.Text =
            $"下载中 {f * 100:0}% · {f * total / 1048576.0:0.0} / {total / 1048576.0:0.0} MB");

        try
        {
            await OcrModelStore.DownloadSmallAsync(progress, _downloadCts.Token);
            OcrDownloadHint.Text = "下载完成，可以直接测试或保存。";
        }
        catch (OperationCanceledException)
        {
            OcrDownloadHint.Text = "已取消下载。";
        }
        catch (Exception ex)
        {
            OcrDownloadHint.Text = "下载失败：" + ex.Message;
            AppLog.Warn("OCR", "PP-OCRv6 高精度模型下载失败：" + ex);
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            OcrDownloadButton.IsEnabled = true;
            OcrEngineCombo.IsEnabled = true;
            UpdateOcrEngineState();
        }
    }

    // —— 图像 ——

    /// <summary>框选截图完成：把搜索区域收紧到刚框的那块（找图 / 找字都更快更准）。</summary>
    private void ImagePicker_Captured(Int32Rect area)
    {
        _ready = false;
        SelectRegion(ImageRegion.rect);
        SetRect(area.X, area.Y, area.Width, area.Height);
        _ready = true;

        ShowPanel();
        ShowTest($"模板图已保存 · 搜索区域已设为该框（{area.Width} × {area.Height}）", true);
    }

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

    // —— 窗口 / 进程：从当前系统状态里点选 ——

    /// <summary>展开 / 收起「窗口类名」高级项。</summary>
    private void WindowAdvanced_Click(object sender, RoutedEventArgs e)
        => SetWindowAdvanced(WindowAdvancedPanel.Visibility != Visibility.Visible);

    private void SetWindowAdvanced(bool expanded)
    {
        WindowAdvancedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        WindowAdvancedToggle.Content = expanded
            ? "高级：按窗口类名精确匹配 ▾"
            : "高级：按窗口类名精确匹配 ▸";
    }

    private void PickWindow_Click(object sender, RoutedEventArgs e)
    {
        var info = SystemPickerDialog.PickWindow(Window.GetWindow(this));
        if (info == null) return;

        // 只回填标题；类名保持为空（高级项里的旧类名会和新标题冲突，一并清掉）
        WindowTitleBox.Text = info.Title;
        WindowClassBox.Text = "";
        ShowTest($"已选择窗口：{info.Title}", true);
    }

    private void PickProcess_Click(object sender, RoutedEventArgs e)
    {
        string? name = SystemPickerDialog.PickProcess(Window.GetWindow(this));
        if (name == null) return;

        ProcessNameBox.Text = name;
        ShowTest("已选择进程：" + name, true);
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
        if (probe.Kind == ProbeKind.screenshot && probe.ScreenshotMode == ScreenshotMode.interactive) return;

        // 看屏幕 / 取前台选区时，LTools 自己的窗口必须让开，否则搜到的是自己。
        bool hide = probe.Kind is ProbeKind.image or ProbeKind.textSearch or ProbeKind.screenshot
            or ProbeKind.color or ProbeKind.cursor or ProbeKind.window or ProbeKind.selection;

        TestBtn.IsEnabled = false;
        ShowTest("测试中…", null);

        Action? restore = null;
        try
        {
            if (hide)
            {
                restore = TransientMinimize.MinimizeAll(Window.GetWindow(this));
                await Task.Delay(TransientMinimize.SettleMs);
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

    private void SelectOcrEngine(string? value)
    {
        string? normalized = PaddleOcr.ParseTier(value) switch
        {
            PaddleOcr.Tier.Tiny => "v6tiny",
            PaddleOcr.Tier.Small => "v6small",
            _ => null,
        };

        for (int i = 0; i < OcrEngineCombo.Items.Count; i++)
        {
            if (OcrEngineCombo.Items[i] is OcrEngineItem item && item.Value == normalized)
            {
                OcrEngineCombo.SelectedIndex = i;
                return;
            }
        }
        OcrEngineCombo.SelectedIndex = 0;
    }

    private sealed record OcrEngineItem(string Label, string? Value)
    {
        public override string ToString() => Label;
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
