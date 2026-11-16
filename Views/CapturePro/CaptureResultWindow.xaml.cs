using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 识别结果窗：一次截图的文字 / 近似表格 / 二维码，每页都能一键复制。
/// </summary>
/// <remarks>
/// <para>
/// 识别要一两秒，所以窗口**先开再算**：<c>Loaded</c> 里把 <see cref="CaptureRecognize.Run"/> 丢到后台线程，
/// 算完回 UI 线程填内容（与编辑器试运行的轨迹上屏同一套思路）。调用方不必自己起线程。
/// </para>
/// <para>
/// 三页签用 RadioButton + 三个同层面板切显隐（主题里没有 TabControl 样式，
/// 用系统那套灰边框会跟整个界面格格不入）。
/// </para>
/// </remarks>
public partial class CaptureResultWindow : Window
{
    private readonly BitmapSource? _image;
    private readonly string? _engine;
    private readonly bool _runOnLoad;

    private RecognizeResult? _result;
    private string _tab = "text";
    private bool _started;

    /// <summary>正常打开：先显示「正在识别…」，Loaded 之后在后台跑识别。</summary>
    public CaptureResultWindow(BitmapSource image, string? engine)
    {
        InitializeComponent();
        _image = image;
        _engine = engine;
        _runOnLoad = true;
        Loaded += (_, _) => Start();
    }

    /// <summary>冒烟 / 自检用：直接灌一份已经算好的结果，不跑 OCR、不读屏幕。</summary>
    internal CaptureResultWindow(RecognizeResult result)
    {
        InitializeComponent();
        _runOnLoad = false;
        Apply(result);
    }

    /// <summary>打开结果窗（非模态：用户慢慢复制，流程该干嘛干嘛）。</summary>
    public static void Show(BitmapSource image, string? engine)
    {
        var window = new CaptureResultWindow(image, engine);
        window.Show();
        window.Activate();
    }

    /// <summary>识别结果（冒烟读它做断言；还没算完时为 null）。</summary>
    internal RecognizeResult? Result => _result;

    private async void Start()
    {
        if (!_runOnLoad || _started) return;
        _started = true;

        if (_image == null)
        {
            Fail("没有可识别的图片");
            return;
        }

        RecognizeResult result;
        try
        {
            var image = _image;
            string? engine = _engine;
            result = await Task.Run(() => CaptureRecognize.Run(image, engine));
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.Result", ex);
            Fail(ex.Message);
            return;
        }

        if (!IsVisible) return;   // 用户已经关掉窗口
        Apply(result);
    }

    private void Fail(string message)
    {
        StatusText.Text = "识别失败";
        DetailText.Text = message;
        CopyBtn.IsEnabled = false;
    }

    internal void Apply(RecognizeResult result)
    {
        _result = result;

        if (!result.Ok)
        {
            Fail(result.Error);
            FillText(result);
            return;
        }

        StatusText.Text = string.Format(CultureInfo.CurrentCulture,
            "识别完成 · 文字 {0} 行{1}{2}",
            result.Lines.Count,
            result.Table.HasTable ? string.Format(CultureInfo.CurrentCulture, " · 表格 {0} 行 × {1} 列", result.Table.RowCount, result.Table.Columns) : "",
            result.QrText.Length > 0 ? " · 找到二维码" : "");

        DetailText.Text = string.Format(CultureInfo.CurrentCulture,
            "{0} · 用时 {1:0.0} 秒", result.Engine, result.ElapsedMs / 1000.0);

        FillText(result);
        FillTable(result.Table);
        FillQr(result);
        CopyBtn.IsEnabled = true;
    }

    // ============ 三页 ============

    private void FillText(RecognizeResult result) => TextView.Text = result.Text;

    private void FillTable(TableGrid table)
    {
        TableView.Columns.Clear();
        TableView.ItemsSource = null;

        bool has = table.HasTable;
        TableView.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        TableEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        CopyTsvBtn.IsEnabled = has;
        CopyHtmlBtn.IsEnabled = has;

        TableInfo.Text = has
            ? string.Format(CultureInfo.CurrentCulture, "{0} 行 × {1} 列（按词框聚类，仅供参考）", table.RowCount, table.Columns)
            : "";

        if (!has) return;

        for (int i = 0; i < table.Columns; i++)
        {
            var binding = new Binding($"[{i}]");
            TableView.Columns.Add(new DataGridTextColumn
            {
                Header = "列 " + (i + 1).ToString(CultureInfo.CurrentCulture),
                Binding = binding,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 72,
                ElementStyle = CellStyle(binding),
            });
        }

        TableView.ItemsSource = table.Rows;
    }

    /// <summary>单元格样式：长文本省略 + 悬停看全文（表格列宽固定，不省略会撑出横向滚动）。</summary>
    private static Style CellStyle(Binding binding)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, binding));
        return style;
    }

    private void FillQr(RecognizeResult result)
    {
        QrView.Text = result.QrText;
        QrInfo.Text = result.QrText.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, "格式 {0} · {1} 个字符", result.QrType, result.QrText.Length)
            : "这张截图里没有找到二维码。";
    }

    // ============ 页签 / 复制 ============

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton button) return;
        _tab = button.Tag as string ?? "text";

        // XAML 解析期间 IsChecked="True" 会先触发一次，那时后面的兄弟控件还没建出来
        if (TextView == null) return;

        bool text = _tab == "text";
        TextView.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        TablePage.Visibility = _tab == "table" ? Visibility.Visible : Visibility.Collapsed;
        QrPage.Visibility = _tab == "qr" ? Visibility.Visible : Visibility.Collapsed;

        CopyBtn.Content = _tab switch
        {
            "table" => "复制 TSV",
            "qr" => "复制二维码内容",
            _ => "复制文字",
        };
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyCurrent();

    private void CopyTsv_Click(object sender, RoutedEventArgs e) => Copy(_result?.Table.ToTsv(), "表格 TSV");

    private void CopyHtml_Click(object sender, RoutedEventArgs e) => Copy(_result?.Table.ToHtml(), "表格 HTML");

    private void CopyCurrent()
    {
        switch (_tab)
        {
            case "table":
                Copy(_result?.Table.ToTsv(), "表格 TSV");
                break;
            case "qr":
                Copy(_result?.QrText, "二维码内容");
                break;
            default:
                Copy(_result?.Text, "识别文字");
                break;
        }
    }

    private static void Copy(string? text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            ToastService.Show("没有可复制的内容", "识别结果", 1600);
            return;
        }

        if (NativeClipboard.SetText(text)) ToastService.Show($"已复制{what}", "识别结果", 1600);
        else ToastService.Show("复制失败（剪贴板被占用）", "识别结果", 0, ToastKind.error);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
