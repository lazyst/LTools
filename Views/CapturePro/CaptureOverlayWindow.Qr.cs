using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 自动识别二维码：选区一落定就在后台扫一遍，识别到的码上摆一枚 24×24 圆点标记，
/// 悬停 / 点击弹出气泡（只读文本 + 复制 / 打开链接）。
/// </summary>
/// <remarks>
/// <para>
/// 1:1 照 snow-shot 的 <c>screenshotqrcontroller.cpp</c>：标记只在「选择」工具下、
/// 没在拖动 / 预选 / 翻历史 / 打字时出现；气泡优先放标记右侧、放不下翻左侧，
/// 光标离开标记或气泡 250ms 后收起，指针停在气泡里（或焦点在文本框里）不收起；
/// 复制 / 打开成功后直接结束这次截图（上游 <c>cancelCapture</c>），Esc 只关气泡。
/// </para>
/// <para>
/// 识别本身走 <see cref="QrScan"/>（纯逻辑，自测覆盖）；本文件只管窗口层：
/// 什么时候扫、结果换成窗口 DIP、标记与气泡怎么摆、事件怎么让路。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    /// <summary>识别到的一枚码：载荷 + 码在窗口里的 DIP 外接矩形（标记摆在它的中心）。</summary>
    private sealed record QrHit(string Text, Rect Code)
    {
        public Point Center => new(Code.Left + Code.Width / 2, Code.Top + Code.Height / 2);
    }

    /// <summary>选区改完到真正开扫之间的防抖（拖边角时别每一帧都扫一遍）。</summary>
    private const int QrDebounceMs = 220;

    private readonly List<QrHit> _qrHits = new();
    private readonly List<Button> _qrMarkers = new();

    private bool _qrAuto = true;         // 设置里的「自动识别二维码」（读一次，整场截图不变）
    private bool _qrVisible = true;      // 工具条上那个勾选（运行期状态，不落盘）
    private bool _qrBusy;                // 正在后台扫
    private string _qrError = "";        // 失败原因（进工具条 ToolTip）
    private int _qrGeneration;           // 结果过期判定（选区变了 / 退出时 +1）
    private string _qrScanned = "";      // 上一次扫完的选区签名
    private string _qrPending = "";      // 已排队 / 正在扫的选区签名

    private DispatcherTimer? _qrDebounce;
    private DispatcherTimer? _qrHover;
    private DispatcherTimer? _qrDismiss;
    private Button? _qrHoverMarker;
    private Button? _qrBubbleMarker;
    private bool _qrBubbleOpen;

    /// <summary>构造函数末尾调一次：读设置、挂三个计时器与气泡的进出事件。</summary>
    private void InitQr()
    {
        _qrAuto = SelectionStore.QrAuto;

        _qrDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(QrDebounceMs) };
        _qrDebounce.Tick += (_, _) => QrDebounceTick();

        // 悬停 150ms 才弹气泡（照上游 m_hoverTimer）；离开 250ms 才收（m_dismissTimer）
        _qrHover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _qrHover.Tick += (_, _) => QrHoverTick();

        _qrDismiss = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _qrDismiss.Tick += (_, _) => QrDismissTick();

        QrBubbleCard.MouseEnter += (_, _) => _qrDismiss?.Stop();
        QrBubbleCard.MouseLeave += (_, _) =>
        {
            if (_qrBubbleOpen) ScheduleQrDismiss();
        };
        QrBubbleCard.LostKeyboardFocus += (_, _) =>
        {
            if (_qrBubbleOpen) ScheduleQrDismiss();
        };

        Toolbar.QrVisibilityRequested += SetQrMarkersVisible;
    }

    /// <summary>工具条上「显示二维码」被点了（照上游 <c>setMarkersVisible</c>）：只切显示，不重扫。</summary>
    private void SetQrMarkersVisible(bool visible)
    {
        _qrVisible = visible;
        UpdateQrChrome();
    }

    // ============ 什么时候扫 ============

    /// <summary>
    /// 每次界面刷新时调（<see cref="UpdateChrome"/> 末尾）：选区落定就排队扫一次，
    /// 再按当前状态摆标记 / 气泡、同步工具条按钮。
    /// </summary>
    private void UpdateQrChrome()
    {
        bool hasSelection = _sel is Rect sel && IsValid(sel);

        // 这些状态里二维码没有意义（或者选区已经不可用）：把结果扔掉，
        // 别让上一张图的标记留在新画面 / 历史图上。
        if (_scrollMode || EditOnly || BrowsingHistory || !hasSelection)
        {
            if (_qrHits.Count > 0 || _qrBusy || _qrScanned.Length > 0) ResetQr();
            Toolbar.SetQrState(false, _qrVisible, _qrError);
            return;
        }

        if (_qrAuto && _drag == DragMode.None && !SmartPreviewActive && !_textEditing) ScheduleQrScan();

        bool canShow = _qrVisible && !_qrBusy && _qrHits.Count > 0 &&
                       _tool == CaptureTool.Select && _drag == DragMode.None &&
                       !SmartPreviewActive && !_textEditing;

        Geometry? geometry = SelectionGeometry();
        for (int i = 0; i < _qrMarkers.Count && i < _qrHits.Count; i++)
        {
            Button marker = _qrMarkers[i];
            QrHit hit = _qrHits[i];
            bool show = canShow && geometry != null && QrInsideSelection(geometry, hit.Code);
            marker.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) continue;

            double side = QrScan.MarkerSize;
            Canvas.SetLeft(marker, hit.Center.X - side / 2);
            Canvas.SetTop(marker, hit.Center.Y - side / 2);
        }

        // 气泡跟着打开它的那枚标记走；标记被藏起来（换工具 / 拖动 / 选区变了）就收起
        if (_qrBubbleOpen && _qrBubbleMarker is { } anchor)
        {
            int index = _qrMarkers.IndexOf(anchor);
            if (index < 0 || index >= _qrHits.Count || anchor.Visibility != Visibility.Visible)
                HideQrBubble();
            else
                PlaceQrBubble(anchor, _qrHits[index]);
        }

        Toolbar.SetQrState(_qrAuto && !_qrBusy && _qrHits.Count > 0, _qrVisible, _qrError);
    }

    /// <summary>把识别结果和「扫过哪个选区」一起清掉，并让后台那次任务作废。</summary>
    private void ResetQr()
    {
        _qrGeneration++;
        _qrBusy = false;
        _qrError = "";
        _qrScanned = "";
        _qrPending = "";
        _qrDebounce?.Stop();
        BuildQrMarkers(null);
    }

    private void ScheduleQrScan()
    {
        if (_qrDebounce == null) return;      // 构造期（InitQr 之前）不扫

        string signature = QrSignature();
        if (signature.Length == 0 || signature == _qrScanned || signature == _qrPending) return;

        _qrPending = signature;
        _qrDebounce.Stop();
        _qrDebounce.Start();
    }

    private void QrDebounceTick()
    {
        _qrDebounce?.Stop();

        string signature = QrSignature();
        if (signature.Length == 0 || signature != _qrPending || signature == _qrScanned) return;

        StartQrScan(signature);
    }

    /// <summary>选区签名：底图 + 选区外接矩形（图像像素）。同一块区域拖着不动就不重复扫。</summary>
    private string QrSignature()
    {
        Int32Rect crop = QrCropRect();
        if (crop.Width < 8 || crop.Height < 8) return "";

        int stamp = RuntimeHelpers.GetHashCode(_doc.Base);
        return $"{stamp}:{crop.X},{crop.Y},{crop.Width},{crop.Height}";
    }

    /// <summary>
    /// 要识别的图像矩形（底图像素）：加区外接框，算法与 <c>SyncArea</c> 同一份
    /// （单块矩形 = 整块；多块 / 自由 = 加区并集）。**不读 <c>_doc.Area</c>**——
    /// 那个要等到导出 / 改样式时才更新，编辑过程中的选区可能比它新。
    /// </summary>
    private Int32Rect QrCropRect()
    {
        int width = _doc.Base.PixelWidth, height = _doc.Base.PixelHeight;
        Rect box = Rect.Empty;
        foreach (var region in _regions)
        {
            if (region.Mode != RegionMode.Add) continue;
            var image = region.ToImage(_doc.Origin, _doc.Scale).Clamp(width, height);
            box.Union(image.Bounds);
        }
        if (box.IsEmpty) return default;

        int bw = Math.Max(2, (int)Math.Round(box.Width));
        int bh = Math.Max(2, (int)Math.Round(box.Height));
        int bx = Math.Min((int)Math.Round(box.X), Math.Max(0, width - bw));
        int by = Math.Min((int)Math.Round(box.Y), Math.Max(0, height - bh));
        return new Int32Rect(bx, by, bw, bh);
    }

    // ============ 后台识别 ============

    private async void StartQrScan(string signature)
    {
        Int32Rect crop = QrCropRect();
        if (crop.Width < 8 || crop.Height < 8) return;

        BitmapSource frame;
        try
        {
            frame = new CroppedBitmap(_doc.Base, crop);
            frame.Freeze();                  // 后台线程要用，必须冻结
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Qr", "裁二维码识别图失败：" + ex.Message);
            return;
        }

        int generation = ++_qrGeneration;
        _qrBusy = true;
        _qrError = "";
        BuildQrMarkers(null);                // 照上游 recognize → invalidate：旧结果先清
        Toolbar.SetQrState(false, _qrVisible, _qrError);

        double originX = _doc.Origin.X, originY = _doc.Origin.Y;
        double documentScale = _doc.Scale > 0 ? _doc.Scale : 1;
        double prepareScale = QrScan.ScaleFor(crop.Width, crop.Height);

        List<QrHit> hits;
        try
        {
            hits = await Task.Run(() => DetectQr(frame, crop, originX, originY, documentScale, prepareScale));
        }
        catch (Exception ex)
        {
            if (generation != _qrGeneration) return;
            AppLog.Warn("CapturePro.Qr", "自动识别二维码失败：" + ex.Message);
            _qrBusy = false;
            _qrError = "二维码识别失败";
            _qrScanned = "";
            _qrPending = "";
            UpdateQrChrome();
            return;
        }

        if (generation != _qrGeneration) return;   // 期间选区变了 / 退出截图：这份结果作废

        _qrBusy = false;
        _qrError = "";
        _qrScanned = signature;
        _qrPending = signature;
        BuildQrMarkers(hits);
        UpdateQrChrome();
    }

    /// <summary>后台线程：裁好的图 → 降采样 → 逐枚解码 → 换算成窗口 DIP。</summary>
    private static List<QrHit> DetectQr(BitmapSource frame, Int32Rect crop,
        double originX, double originY, double documentScale, double prepareScale)
    {
        var hits = new List<QrHit>();
        using System.Drawing.Bitmap source = CaptureRecognize.ToBitmap(frame);
        using System.Drawing.Bitmap prepared = QrScan.Prepare(source, prepareScale);

        foreach (var mark in QrScan.Detect(prepared))
        {
            double x1 = originX + (crop.X + mark.Left / prepareScale) / documentScale;
            double y1 = originY + (crop.Y + mark.Top / prepareScale) / documentScale;
            double x2 = originX + (crop.X + mark.Right / prepareScale) / documentScale;
            double y2 = originY + (crop.Y + mark.Bottom / prepareScale) / documentScale;

            double w = x2 - x1, h = y2 - y1;
            if (w < 1 || h < 1) continue;
            hits.Add(new QrHit(mark.Text, new Rect(x1, y1, w, h)));
        }

        return hits;
    }

    // ============ 标记 ============

    /// <summary>重建标记按钮（null / 空表 = 全部摘掉）。顺序与 <see cref="_qrHits"/> 一一对应。</summary>
    private void BuildQrMarkers(List<QrHit>? hits)
    {
        HideQrBubble();

        foreach (var marker in _qrMarkers) QrMarkerHost.Children.Remove(marker);
        _qrMarkers.Clear();
        _qrHits.Clear();

        if (hits == null || hits.Count == 0) return;

        for (int i = 0; i < hits.Count; i++)
        {
            QrHit hit = hits[i];
            var button = new Button
            {
                Style = (Style)FindResource("QrMarkerBtn"),
                ToolTip = hit.Text.Length > 80 ? hit.Text[..80] + "…" : hit.Text,
                Tag = i,
                Visibility = Visibility.Collapsed,
            };
            button.Click += (_, _) => ShowQrBubble(button, focus: true);
            button.MouseEnter += (_, _) => QrMarkerEnter(button);
            button.MouseLeave += (_, _) => QrMarkerLeave(button);

            QrMarkerHost.Children.Add(button);
            _qrMarkers.Add(button);
            _qrHits.Add(hit);
        }
    }

    /// <summary>码的外接矩形四角都在选区里才算（上游：选区减去码为空）。</summary>
    private static bool QrInsideSelection(Geometry geometry, Rect code)
    {
        return geometry.FillContains(new Point(code.Left, code.Top)) &&
               geometry.FillContains(new Point(code.Right, code.Top)) &&
               geometry.FillContains(new Point(code.Left, code.Bottom)) &&
               geometry.FillContains(new Point(code.Right, code.Bottom)) &&
               geometry.FillContains(new Point(code.Left + code.Width / 2, code.Top + code.Height / 2));
    }

    private void QrMarkerEnter(Button marker)
    {
        _qrDismiss?.Stop();
        if (!ReferenceEquals(_qrHoverMarker, marker)) _qrHover?.Stop();
        _qrHoverMarker = marker;

        bool already = _qrBubbleOpen && ReferenceEquals(_qrBubbleMarker, marker);
        if (!already && _qrHover is { IsEnabled: false }) _qrHover.Start();
    }

    private void QrMarkerLeave(Button marker)
    {
        if (ReferenceEquals(_qrHoverMarker, marker)) ScheduleQrDismiss();
    }

    private void QrHoverTick()
    {
        _qrHover?.Stop();
        if (_qrHoverMarker is { } marker && marker.Visibility == Visibility.Visible)
            ShowQrBubble(marker, focus: false);
    }

    /// <summary>离开标记 / 气泡后等 250ms 再收（指针移进气泡就停表）。</summary>
    private void ScheduleQrDismiss()
    {
        _qrHover?.Stop();
        _qrHoverMarker = null;
        _qrDismiss?.Stop();
        _qrDismiss?.Start();
    }

    private void QrDismissTick()
    {
        _qrDismiss?.Stop();

        // 指针停在气泡上 / 焦点在气泡里的文本框：不收起（上游同一个判定）
        if (_qrBubbleOpen && (QrBubbleCard.IsMouseOver || QrBubbleCard.IsKeyboardFocusWithin)) return;
        if (_qrHoverMarker != null) return;

        HideQrBubble();
    }

    // ============ 气泡 ============

    private void ShowQrBubble(Button marker, bool focus)
    {
        int index = marker.Tag is int value ? value : -1;
        if (index < 0 || index >= _qrHits.Count || marker.Visibility != Visibility.Visible) return;

        QrHit hit = _qrHits[index];
        _qrHover?.Stop();
        _qrDismiss?.Stop();

        bool same = _qrBubbleOpen && ReferenceEquals(_qrBubbleMarker, marker);
        if (!same)
        {
            QrBubbleText.Text = hit.Text;
            QrBubbleStatus.Text = "";
            QrBubbleStatus.Visibility = Visibility.Collapsed;

            // 整串就是一个 http(s) 网址才给「打开链接」，按钮主次也跟着换（照上游 setDetection）
            string? url = QrScan.WebUrl(hit.Text);
            QrOpenBtn.Visibility = url == null ? Visibility.Collapsed : Visibility.Visible;
            QrCopyBtn.Style = (Style)FindResource(url == null ? "QrBtnPrimary" : "QrBtnOutline");
            _qrBubbleMarker = marker;
        }

        _qrBubbleOpen = true;
        QrBubbleHost.Visibility = Visibility.Visible;
        PlaceQrBubble(marker, hit);

        if (focus)
        {
            QrBubbleText.Focus();
            Keyboard.Focus(QrBubbleText);
        }
    }

    private void HideQrBubble()
    {
        bool wasOpen = _qrBubbleOpen;
        _qrBubbleOpen = false;
        _qrBubbleMarker = null;
        QrBubbleHost.Visibility = Visibility.Collapsed;

        // 焦点还在气泡里（Esc / 复制完）就还给覆盖层，别让键盘悬在一个看不见的控件上
        if (wasOpen && QrBubbleCard.IsKeyboardFocusWithin) Focus();
    }

    /// <summary>按标记位置摆气泡：先量文本高，再把「卡 + 箭头」摆到标记旁边（照上游 place）。</summary>
    private void PlaceQrBubble(Button marker, QrHit hit)
    {
        double availableWidth = Math.Max(1, ActualWidth);
        double availableHeight = Math.Max(1, ActualHeight);

        double width = QrScan.BubbleWidth(availableWidth);
        double content = QrScan.ContentWidth(width);

        QrBubbleCard.Width = width;
        QrBubbleText.Width = content;
        QrBubbleText.Height = double.NaN;
        QrBubbleText.Measure(new Size(content, double.PositiveInfinity));

        double textHeight = QrScan.TextHeight(QrBubbleText.DesiredSize.Height, availableHeight);
        QrBubbleText.Height = textHeight;

        QrBubbleCard.Height = double.NaN;
        QrBubbleCard.Measure(new Size(width, double.PositiveInfinity));
        double height = Math.Max(QrScan.Pad * 2 + 1, QrBubbleCard.DesiredSize.Height);
        QrBubbleCard.Height = height;

        double side = QrScan.MarkerSize;
        var anchor = new Rect(hit.Center.X - side / 2, hit.Center.Y - side / 2, side, side);
        var available = new Rect(0, 0, availableWidth, availableHeight);
        QrBubbleLayout layout = QrScan.PlaceBubble(anchor, available, width, height);

        Canvas.SetLeft(QrBubbleCard, layout.Left);
        Canvas.SetTop(QrBubbleCard, layout.Top);
        QrBubbleShape.Data = BubbleGeometry(width, height, layout.ArrowOnLeft, layout.ArrowY);
    }

    /// <summary>圆角卡 + 箭头合成一个几何：描边只在外轮廓上（内部那条接缝被并掉了）。</summary>
    private static Geometry BubbleGeometry(double width, double height, bool arrowOnLeft, double arrowY)
    {
        double pad = QrScan.Pad;
        var box = new Rect(pad, pad, Math.Max(1, width - pad * 2), Math.Max(1, height - pad * 2));
        var body = new RectangleGeometry(box, QrScan.Radius, QrScan.Radius);

        double y = Math.Clamp(arrowY, box.Top + 16, Math.Max(box.Top + 16, box.Bottom - 16));
        double x = arrowOnLeft ? box.Left : box.Right;
        double tip = arrowOnLeft ? x - 8 : x + 8;

        var arrow = new PathGeometry(new[]
        {
            new PathFigure(new Point(x, y - 8), new PathSegment[]
            {
                new LineSegment(new Point(tip, y), true),
                new LineSegment(new Point(x, y + 8), true),
            }, closed: true),
        });

        return new CombinedGeometry(GeometryCombineMode.Union, body, arrow);
    }

    // ============ 气泡里的两个按钮 ============

    /// <summary>复制：选中了就只复制选中那段，否则整串；成功后结束这次截图（照上游 cancelCapture）。</summary>
    private void QrCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!_qrBubbleOpen || _qrBubbleMarker is not { } marker) return;

        int index = _qrMarkers.IndexOf(marker);
        if (index < 0 || index >= _qrHits.Count) return;

        string text = QrBubbleText.SelectionLength > 0 ? QrBubbleText.SelectedText : _qrHits[index].Text;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Qr", "复制二维码文本失败：" + ex.Message);
            QrBubbleStatus.Text = "复制失败：剪贴板被别的程序占着";
            QrBubbleStatus.Visibility = Visibility.Visible;
            PlaceQrBubble(marker, _qrHits[index]);
            return;
        }

        HideQrBubble();
        Cancel();
    }

    /// <summary>打开链接：只认整串的 http(s) 网址；打不开就在气泡里写明原因（照上游 showOpenError）。</summary>
    private void QrOpen_Click(object sender, RoutedEventArgs e)
    {
        if (!_qrBubbleOpen || _qrBubbleMarker is not { } marker) return;

        int index = _qrMarkers.IndexOf(marker);
        if (index < 0 || index >= _qrHits.Count) return;

        string? url = QrScan.WebUrl(_qrHits[index].Text);
        if (url == null) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Qr", "打开二维码链接失败：" + ex.Message);
            QrBubbleStatus.Text = "无法打开识别到的链接";
            QrBubbleStatus.Visibility = Visibility.Visible;
            PlaceQrBubble(marker, _qrHits[index]);
            return;
        }

        HideQrBubble();
        Cancel();
    }

    // ============ 鼠标 / 键盘让路 ============

    private bool IsOnQr(DependencyObject? source) => AncestorIs(source, QrMarkerHost);

    private bool IsOnQrBubble(DependencyObject? source) => AncestorIs(source, QrBubbleCard);

    /// <summary>沿可视树往上找某个祖先（和 <c>IsOnToolbar</c> 那套同一个写法）。</summary>
    private static bool AncestorIs(DependencyObject? source, DependencyObject target)
    {
        for (var node = source; node != null;)
        {
            if (ReferenceEquals(node, target)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
