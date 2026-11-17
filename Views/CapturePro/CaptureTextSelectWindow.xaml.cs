using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 屏幕选字：冻结桌面 → 拖一个框 → 识别框里的文字 → 候选条里点一条复制。
/// </summary>
/// <remarks>
/// <para>
/// 与截图覆盖层同一套取景（<see cref="ScreenShot.Bounds"/>，物理像素）、同一套 DIP 校准
/// （先在 <c>Loaded</c> 里按物理像素钉死，再用 <c>ActualWidth / 像素宽</c> 校准比例）。
/// 候选条画在同一个窗口里，所以不用第二个窗口、<c>Esc</c> 始终有效。
/// </para>
/// <para>
/// OCR 走 <see cref="OcrLayout"/>（与「文字识别」同一个引擎偏好），在后台线程跑；
/// 识别失败只写在状态行上，不炸窗口。复制一律走 <see cref="NativeClipboard.SetText"/>。
/// </para>
/// <para>
/// 压暗用四块矩形拼（选区那块保持原亮度）；候选条落点复用 <see cref="ScrollLayout.PlaceIn"/>。
/// </para>
/// </remarks>
public partial class CaptureTextSelectWindow : Window
{
    /// <summary>小于这个 DIP 尺寸就不算框选（防手抖）。</summary>
    private const double MinSize = 6;

    private const double BarWidth = 400;

    private readonly BitmapSource _shot;
    private readonly System.Drawing.Rectangle _area;

    /// <summary>物理像素 / DIP（按窗口实际宽度校准，见 <see cref="SyncScale"/>）。</summary>
    private double _scale;

    private IntPtr _hwnd;
    private Point _origin;
    private bool _dragging;
    private Rect _selection;          // 窗口 DIP
    private bool _busy;               // 识别进行中：别再开新框
    private string _fullText = "";
    private IReadOnlyList<string> _candidates = Array.Empty<string>();

    /// <summary>用户这次复制了文字（覆盖层那边只关心「完成还是取消」）。</summary>
    internal bool Copied { get; private set; }

    internal CaptureTextSelectWindow(BitmapSource shot, System.Drawing.Rectangle area, double sx, double sy)
    {
        InitializeComponent();

        _shot = shot;
        _area = area;
        _scale = sx > 0 ? sx : 1;

        Shot.Source = shot;

        // 按目标显示器的 DPI 把物理矩形换算成 DIP（Loaded 里再按物理像素钉一次）
        Left = area.Left / _scale;
        Top = area.Top / (sy > 0 ? sy : 1);
        Width = Math.Max(1, area.Width / _scale);
        Height = Math.Max(1, area.Height / (sy > 0 ? sy : 1));

        DpiChanged += (_, _) => { PinToMonitor(); SyncScale(); };

        // 右键 / Esc 关窗：把「那一下」的抬起也吞掉，别漏给下面的程序（见 ExitInput）
        Closed += (_, _) => ExitInput.Swallow();
    }

    // ============ 生命周期 ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        PinToMonitor();
        SyncScale();

        Activate();
        Focus();

        UpdateLayout();
        PlaceHint();
        UpdateChrome();
    }

    /// <summary>按物理像素钉死窗口（与截图覆盖层同一套，混合 DPI 下 WPF 的 DIP 换算可能差几个像素）。</summary>
    private void PinToMonitor()
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost,
            _area.X, _area.Y, _area.Width, _area.Height, Win32.SwpNoactivate);
    }

    /// <summary>窗口真正显示之后校准比例：跨屏时系统按它落在哪块屏给 DPI，构造时只是初值。</summary>
    private void SyncScale()
    {
        if (ViewWidth <= 0 || _shot.PixelWidth <= 0) return;
        _scale = _shot.PixelWidth / ViewWidth;
    }

    // ============ 框选 ============

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnBar(e.OriginalSource as DependencyObject)) return;   // 点候选条不算框选
        if (_busy) return;

        _dragging = true;
        _origin = Clamp(e.GetPosition(Root));
        _selection = new Rect(_origin, new Size(0, 0));
        HideBar();
        SetHint("拖动框选要识别的文字 · Esc 取消");
        CatchMouse(true);
        UpdateChrome();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var to = Clamp(e.GetPosition(Root));
        _selection = new Rect(
            Math.Min(_origin.X, to.X), Math.Min(_origin.Y, to.Y),
            Math.Abs(to.X - _origin.X), Math.Abs(to.Y - _origin.Y));
        UpdateChrome();
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        CatchMouse(false);

        if (_selection.Width < MinSize || _selection.Height < MinSize)
        {
            _selection = Rect.Empty;
            UpdateChrome();
            return;
        }

        StartRecognize();
    }

    private void Window_Cancel(object sender, MouseButtonEventArgs e) => CloseWith(false);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseWith(false);
    }

    private void CatchMouse(bool on)
    {
        if (on) Mouse.Capture(this, CaptureMode.SubTree);
        else if (Mouse.Captured == this) ReleaseMouseCapture();
    }

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, 0, Math.Max(0, ViewWidth)),
        Math.Clamp(p.Y, 0, Math.Max(0, ViewHeight)));

    /// <summary>
    /// 窗口内容的 DIP 尺寸。窗口还没 <c>Show</c>（冒烟直接排版）时 <see cref="FrameworkElement.ActualWidth"/>
    /// 是 0，以内容自己的排版结果为准。
    /// </summary>
    private double ViewWidth => Root.ActualWidth > 0 ? Root.ActualWidth : ActualWidth;

    private double ViewHeight => Root.ActualHeight > 0 ? Root.ActualHeight : ActualHeight;

    /// <summary>选区是不是落在候选条上（在条上点按钮 / 划选文字都不该开始新框选）。</summary>
    private bool IsOnBar(DependencyObject? source)
    {
        for (var node = source; node != null; node = ParentOf(node))
            if (ReferenceEquals(node, ResultBar)) return true;
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node)
            : null;

    // ============ 识别 ============

    private void StartRecognize()
    {
        var crop = CropSelection();
        if (crop == null) return;

        _busy = true;
        CopyAllBtn.IsEnabled = false;
        _fullText = "";
        _candidates = Array.Empty<string>();
        CandidateList.ItemsSource = null;
        CandidateScroll.Visibility = Visibility.Collapsed;
        FullTextBox.Visibility = Visibility.Collapsed;
        ResultBar.Visibility = Visibility.Visible;
        HintBox.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在识别…";
        EngineText.Text = "";
        LayoutBar();

        string? engine = OcrEnginePreference.DefaultForNewProbe;

        Task.Run(() =>
        {
            string text = "";
            IReadOnlyList<string> candidates = Array.Empty<string>();
            string error = "";

            try
            {
                using var bitmap = CaptureRecognize.ToBitmap(crop);
                var lines = OcrLayout.Read(bitmap, engine);
                text = string.Join("\n", lines.Select(l => l.Text).Where(t => t.Trim().Length > 0));
                candidates = ScreenTextCandidates.Build(lines.Select(l => l.Text));
            }
            catch (Exception ex)
            {
                AppLog.Warn("CapturePro.TextSelect", "识别失败：" + ex.Message);
                error = ex.Message;
            }

            string engineLabel = CaptureRecognize.EngineLabel(engine);

            try
            {
                Dispatcher.BeginInvoke(new Action(() => ApplyResult(text, candidates, engineLabel, error)));
            }
            catch (Exception ex)
            {
                AppLog.Warn("CapturePro.TextSelect", "回填识别结果失败：" + ex.Message);
            }
        });
    }

    /// <summary>把选区从冻结底图上裁下来（物理像素，已冻结；交给后台线程用）。</summary>
    private BitmapSource? CropSelection()
    {
        if (_scale <= 0) return null;

        int x = (int)Math.Round(_selection.X * _scale);
        int y = (int)Math.Round(_selection.Y * _scale);
        int w = (int)Math.Round(_selection.Width * _scale);
        int h = (int)Math.Round(_selection.Height * _scale);

        if (w < 2 || h < 2) return null;

        try
        {
            var crop = new CroppedBitmap(_shot,
                new Int32Rect(x, y, Math.Min(w, _shot.PixelWidth - x), Math.Min(h, _shot.PixelHeight - y)));
            crop.Freeze();
            return crop;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.TextSelect", "裁剪选区失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>识别结果回到 UI 线程：填候选 / 整段文字，或者只把失败原因写在状态行上。</summary>
    private void ApplyResult(string text, IReadOnlyList<string> candidates, string engineLabel, string error)
    {
        _busy = false;
        ResultBar.Visibility = Visibility.Visible;
        HintBox.Visibility = Visibility.Collapsed;
        EngineText.Text = engineLabel;
        LayoutBar();

        if (error.Length > 0)
        {
            StatusText.Text = "识别失败：" + error;
            return;
        }

        if (text.Length == 0)
        {
            StatusText.Text = "没有识别到文字，换一块区域再试";
            return;
        }

        _fullText = text;
        _candidates = candidates;

        StatusText.Text = candidates.Count > 0
            ? $"识别到 {candidates.Count} 条候选 · 点一条复制，或拖选下方文字后 Ctrl+C"
            : "识别到文字 · 可在下方拖选后 Ctrl+C";
        SetHint(candidates.Count > 0 ? "点候选复制 · 也可在下方文本框里拖选" : "拖选下方文字后 Ctrl+C");

        CopyAllBtn.IsEnabled = true;
        FullTextBox.Text = text;
        FullTextBox.Visibility = Visibility.Visible;

        if (candidates.Count > 0)
        {
            CandidateList.ItemsSource = candidates;
            CandidateScroll.Visibility = Visibility.Visible;
        }

        LayoutBar();
    }

    // ============ 复制 / 收尾 ============

    private void Candidate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string text } && text.Length > 0) Copy(text);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_fullText.Length > 0) Copy(_fullText);
    }

    private void Reselect_Click(object sender, RoutedEventArgs e) => ResetSelection();

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(false);

    /// <summary>复制一段文字 → toast → 关窗。失败只提示，不关（用户还能改用文本框手动复制）。</summary>
    private void Copy(string text)
    {
        bool ok;
        try
        {
            ok = NativeClipboard.SetText(text);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.TextSelect", "复制失败：" + ex.Message);
            ok = false;
        }

        if (!ok)
        {
            StatusText.Text = "复制到剪贴板失败，可选中下方文字按 Ctrl+C";
            return;
        }

        string preview = text.Length > 24 ? text[..24] + "…" : text;
        ToastService.Show($"已复制：{preview}", "屏幕选字", 1800, ToastKind.success);
        CloseWith(true);
    }

    private void ResetSelection()
    {
        _selection = Rect.Empty;
        _candidates = Array.Empty<string>();
        _fullText = "";
        _busy = false;
        CandidateList.ItemsSource = null;
        CandidateScroll.Visibility = Visibility.Collapsed;
        FullTextBox.Text = "";
        FullTextBox.Visibility = Visibility.Collapsed;
        CopyAllBtn.IsEnabled = false;
        HideBar();
        SetHint("拖动框选要识别的文字 · Esc 取消");
        UpdateChrome();
    }

    private void HideBar()
    {
        ResultBar.Visibility = Visibility.Collapsed;
        HintBox.Visibility = Visibility.Visible;
    }

    private void CloseWith(bool copied)
    {
        Copied = copied;
        try { DialogResult = copied; }
        catch (InvalidOperationException)
        {
            // 非模态 / 没显示过（冒烟）时 DialogResult 不可用，直接关窗
            try { Close(); }
            catch (Exception ex) { AppLog.Warn("CapturePro.TextSelect", "关窗失败：" + ex.Message); }
        }
    }

    // ============ 界面 ============

    /// <summary>压暗四块 + 选区描边。没有选区时整块压暗。</summary>
    private void UpdateChrome()
    {
        double w = ViewWidth, h = ViewHeight;
        var s = _selection;

        if (s.Width <= 0 || s.Height <= 0)
        {
            SetRect(VeilTop, new Rect(0, 0, w, h));
            SetRect(VeilBottom, Rect.Empty);
            SetRect(VeilLeft, Rect.Empty);
            SetRect(VeilRight, Rect.Empty);
            SelRect.Visibility = Visibility.Collapsed;
            return;
        }

        SetRect(VeilTop, new Rect(0, 0, w, s.Top));
        SetRect(VeilBottom, new Rect(0, s.Bottom, w, Math.Max(0, h - s.Bottom)));
        SetRect(VeilLeft, new Rect(0, s.Top, s.Left, s.Height));
        SetRect(VeilRight, new Rect(s.Right, s.Top, Math.Max(0, w - s.Right), s.Height));

        Canvas.SetLeft(SelRect, s.Left);
        Canvas.SetTop(SelRect, s.Top);
        SelRect.Width = s.Width;
        SelRect.Height = s.Height;
        SelRect.Visibility = Visibility.Visible;
    }

    private static void SetRect(FrameworkElement element, Rect rect)
    {
        Canvas.SetLeft(element, rect.X);
        Canvas.SetTop(element, rect.Y);
        element.Width = rect.Width;
        element.Height = rect.Height;
        element.Visibility = rect.Width > 0 && rect.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlaceHint()
    {
        HintBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = HintBox.DesiredSize.Width;
        Canvas.SetLeft(HintBox, Math.Max(0, (ViewWidth - width) / 2));
        Canvas.SetTop(HintBox, 56);
        SetHint("拖动框选要识别的文字 · Esc 取消");
    }

    private void SetHint(string text) => HintText.Text = text;

    /// <summary>候选条贴到选区外面（窗口自己就是整块桌面，不必再换物理坐标）。</summary>
    private void LayoutBar()
    {
        if (ResultBar.Visibility != Visibility.Visible) return;

        ResultBar.Measure(new Size(BarWidth, double.PositiveInfinity));
        double height = Math.Min(ResultBar.DesiredSize.Height, Math.Max(120, ViewHeight - 24));

        var place = ScrollLayout.PlaceIn(_selection, new Size(BarWidth, height),
            new Size(ViewWidth, ViewHeight));

        Canvas.SetLeft(ResultBar, place.Left);
        Canvas.SetTop(ResultBar, place.Top);
    }
}
