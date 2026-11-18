using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features.CapturePro;
using LTools.Native;

using SelectionMode = LTools.Features.CapturePro.SelectionMode;

namespace LTools.Views.CapturePro;

/// <summary>
/// 覆盖层的窗口层元素：把手、尺寸条、悬停标签、顶部提示、完成动作那一截。
/// </summary>
/// <remarks>
/// 摆放一律按「选区在窗口里的 DIP 矩形」算，尺寸文字用物理像素（那才是最终存进配置的数字）。
/// 所有 <c>Measure</c> 都在 Canvas 里做：Canvas 用无限约束测量，<c>DesiredSize</c> 就是内容尺寸，
/// 才能把工具条「贴着选区、放不下翻上方」算出来。
/// </remarks>
public partial class CaptureOverlayWindow
{
    private const double GripSize = MarkGeometry.HandleSize;   // 把手方块尺寸（DIP，与标注手柄同一个值）
    private const double GripHit = 8;      // 把手命中半径（DIP）

    /// <summary>选区上的八个把手（框选模式下创建）。</summary>
    private void BuildGrips()
    {
        foreach (var h in MarkGeometry.Handles)
        {
            var grip = new Rectangle
            {
                Width = GripSize,
                Height = GripSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Tag = h,
            };
            _grips.Add(grip);
            Layer.Children.Add(grip);
        }
    }

    /// <summary>按当前选区重画画面、把手、尺寸条、悬停高亮、工具条与顶部提示。</summary>
    private void UpdateChrome()
    {
        // 长截图模式：界面完全是另一套（拼接画面 + 小面板），别再摆框选那一套
        if (_scrollMode)
        {
            ApplyScrollChrome();
            return;
        }

        // 折线选区还没围起来：这不是「已选区」，别出把手 / 工具条（_sel 只是外接框）
        bool pending = SelectionPending;

        // 多块 / 自由 / 折线 / 挖洞都按合起来的几何画（与导出同一份 SelectionSet 规则）；
        // 减区把选区挖空 = null：没有可导出的内容，别出把手 / 工具条。
        // 折线还在逐点单击时几何里本来就没有它（见 LiveRegions），已有选区照旧留在画面上。
        Geometry? geometry = SelectionGeometry();
        bool hasSelection = !pending && geometry != null && _sel is Rect current && IsValid(current);
        Rect sel = hasSelection ? _sel!.Value : Rect.Empty;

        // 悬停高亮只在「还没选区」时出现；有选区时用户在看边角，不需要再跳候选
        Rect hover = Rect.Empty;
        if (!hasSelection && !pending && _regions.Count == 0 && _hoverTarget is SnapBox hb)
        {
            var hr = ToDipRect(hb.Bounds);
            if (hr.Width >= 2 && hr.Height >= 2) hover = hr;
        }

        Surface.Selection = sel;
        Surface.SelectionGeometry = geometry;
        Surface.PendingPolygon = PendingShape();
        Surface.Hover = hover;
        Surface.ShowHandles = ShowsMarkHandles;
        Surface.InvalidateVisual();

        // 形状条 = 「下一块的形状」，**框过之后也常驻**：加区 / 减区也要先选形状。
        // 只在拖动 / 画标注、折线待收尾、贴图内标注、翻历史、打字时收起来。
        bool shapeBar = !pending && _drag == DragMode.None && !_textEditing && !EditOnly && !BrowsingHistory;
        ShapeBar.Visibility = shapeBar ? Visibility.Visible : Visibility.Collapsed;
        if (shapeBar)
        {
            SyncShapeButtons();
            PlaceShapeBar();
        }

        if (!hasSelection)
        {
            PositionGrips(sel, false);
            InfoBox.Visibility = Visibility.Collapsed;
            HideToolbar();
            ClearMarkSelection();
            if (hover != Rect.Empty) DrawDetect(hover, _hoverTarget!.Value.Label);
            else HideDetect();

            RefreshHint();
            PlaceHint();
            return;
        }

        HideDetect();
        PositionGrips(sel, true);

        // 拖动过程中先别跳出来，松手后再给按钮。
        // 「贴图内标注」没有完成动作：完成是 Enter / 双击，结果交回那张贴图，别摆一排按了会被忽略的按钮。
        bool showActions = _drag == DragMode.None && !EditOnly;
        Toolbar.SetActionsVisible(showActions);

        // 工具栏：改框期间先收起来，免得被拖到指头底下；画标注时留着（随时能换工具）
        if (_drag is DragMode.New or DragMode.Move or DragMode.Resize or DragMode.Lasso) Toolbar.Visibility = Visibility.Collapsed;
        else ShowToolbar(sel);
        Toolbar.SetUndo(_doc.CanUndo, _doc.CanRedo);

        // 尺寸提示：主流放在选区左上角外侧，工具条翻到选区上方时让开（别压住它）
        double toolbarHeight = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : Toolbar.DesiredSize.Height;
        bool toolbarAbove = Toolbar.Visibility == Visibility.Visible &&
                            Canvas.GetTop(Toolbar) + toolbarHeight <= sel.Top + 1;
        ShowSizeInfo(sel, toolbarAbove);

        RefreshHint();
        PlaceHint();
    }

    /// <summary>
    /// 工具条贴着选区走（照 snow-shot：在框选区域下方居中，放不下翻到上方）。
    /// 用户自己拖过就停在他放的地方，只做「别拖出窗口」的兜底。
    /// </summary>
    private void ShowToolbar(Rect sel)
    {
        Toolbar.Visibility = Visibility.Visible;

        if (!_toolbarPlaced || !Toolbar.UserMoved)
        {
            _toolbarPlaced = true;
            Toolbar.PlaceNear(sel, ActualWidth, ActualHeight);
        }

        // 兜底：拖到边上 / 高度变化时别让参数条被画布裁掉
        Toolbar.ClampInside(ActualWidth, ActualHeight);
    }

    private void HideToolbar()
    {
        Toolbar.Visibility = Visibility.Collapsed;
        _toolbarPlaced = false;
    }

    private void HideDetect()
    {
        DetectRect.Visibility = Visibility.Collapsed;
        DetectLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>悬停高亮：琥珀色框 + 左上角标签（控件类型 · 名称 · 尺寸）。</summary>
    private void DrawDetect(Rect r, string label)
    {
        DetectRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(DetectRect, r.X);
        Canvas.SetTop(DetectRect, r.Y);
        DetectRect.Width = r.Width;
        DetectRect.Height = r.Height;

        var (x1, y1) = ToImage(r.TopLeft);
        var (x2, y2) = ToImage(r.BottomRight);
        DetectText.Text = $"{label}   {Math.Abs(x2 - x1) + 1} × {Math.Abs(y2 - y1) + 1}";

        DetectLabel.Visibility = Visibility.Visible;
        DetectLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lw = DetectLabel.DesiredSize.Width;
        double lh = DetectLabel.DesiredSize.Height;

        double left = Math.Clamp(r.X, 8, Math.Max(8, ActualWidth - lw - 8));
        double top = r.Y - lh - 6;
        if (top < 8) top = r.Y + 6;                    // 上方贴边就放进框内侧
        if (top + lh > ActualHeight - 8) top = r.Bottom - lh - 6;
        Canvas.SetLeft(DetectLabel, left);
        Canvas.SetTop(DetectLabel, Math.Clamp(top, 8, Math.Max(8, ActualHeight - lh - 8)));
    }

    /// <summary>
    /// 选中标注的八个缩放手柄要不要露出来：只有「选择」工具下、选中了、且这一条能缩放。
    /// 序号徽标是固定尺寸的（数字要看得清），不给它手柄。
    /// </summary>
    private bool ShowsMarkHandles =>
        _tool == CaptureTool.Select && _selectedIndex >= 0 && _selectedIndex < _doc.Marks.Count &&
        _doc.Marks[_selectedIndex].CanScale;

    private void PositionGrips(Rect s, bool visible)
    {
        foreach (var grip in _grips)
        {
            if (!visible || grip.Tag is not FrameHandle h)
            {
                grip.Visibility = Visibility.Collapsed;
                continue;
            }

            Point at = MarkGeometry.GripPoint(s, h);
            Canvas.SetLeft(grip, at.X - GripSize / 2);
            Canvas.SetTop(grip, at.Y - GripSize / 2);
            grip.Visibility = Visibility.Visible;
        }
    }

    /// <summary>尺寸提示（物理像素）；主流放在选区左上角外侧，工具条翻上去时改放选区内侧。</summary>
    private void ShowSizeInfo(Rect s, bool toolbarAbove)
    {
        var (x1, y1) = ToImage(s.TopLeft);
        var (x2, y2) = ToImage(s.BottomRight);
        int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
        int w = Math.Abs(x2 - x1) + 1, h = Math.Abs(y2 - y1) + 1;

        // 尺寸仍报外接框（存进配置的就是它），形状 / 块数另说一句
        InfoText.Text = $"{SelectionLabel()}{w} × {h} px   {_doc.Monitor.X + x}, {_doc.Monitor.Y + y}";
        InfoBox.Visibility = Visibility.Visible;
        InfoBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double iw = InfoBox.DesiredSize.Width;
        double ih = InfoBox.DesiredSize.Height;

        double left = Math.Clamp(s.X, 8, Math.Max(8, ActualWidth - iw - 8));
        double top = s.Y - ih - 6;                     // 选区上方
        if (top < 8 || toolbarAbove) top = s.Y + 6;    // 上方没空间 / 工具条翻上去了 → 放进选区左上角
        Canvas.SetLeft(InfoBox, left);
        Canvas.SetTop(InfoBox, Math.Clamp(top, 8, Math.Max(8, ActualHeight - ih - 8)));
    }

    /// <summary>顶部提示居中。</summary>
    private void PlaceHint()
    {
        // 提示是单行不换行的长句：窗口窄（贴图内标注的覆盖层、小选区）时先按窗口收窄，
        // 否则它会从右边伸出去（用户看到半句话）。
        HintText.MaxWidth = Math.Max(160, ActualWidth - 24);

        // 文本刚换过就量的话，Measure 会拿到上一次（甚至是空文本）的量，居中会算偏到右边——
        // 先显式作废再量（实测：先 PlaceHint 再换文本的那一次，DesiredSize 停在 28）。
        HintText.InvalidateMeasure();
        HintBox.InvalidateMeasure();

        HintBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = HintBox.DesiredSize.Width;
        Canvas.SetLeft(HintBox, Math.Max(12, (ActualWidth - w) / 2));
        Canvas.SetTop(HintBox, 26);
    }

    /// <summary>形状条居中摆在提示下面（提示会按窗口宽度换行，所以每次都现量）。</summary>
    private void PlaceShapeBar()
    {
        ShapeBar.InvalidateMeasure();
        ShapeBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = ShapeBar.DesiredSize.Width;
        Canvas.SetLeft(ShapeBar, Math.Max(12, (ActualWidth - w) / 2));
        Canvas.SetTop(ShapeBar, 64);
    }

    /// <summary>顶部提示：把「现在能做什么」和「识别到了没有」放在一句话里。</summary>
    private void RefreshHint()
    {
        if (EditOnly)
        {
            HintText.Text = "标注这张贴图 · Enter 换回贴图 · Esc 取消 · 右键退回";
            return;
        }

        if (BrowsingHistory)
        {
            HintText.Text = HistoryHint();
            return;
        }

        if (_tool == CaptureTool.Picker)
        {
            HintText.Text = "取色 · 单击复制 #RRGGBB · 色卡跟着光标 · Esc 退出取色";
            return;
        }

        // 折线选区正在逐点单击：说清怎么收尾（不然只会一直加点）
        if (_selPolyActive)
        {
            HintText.Text = $"折线选区 · 已点 {_selPoly.Count} 个点 · 双击 / Enter / 点回起点结束 · Esc 取消";
            return;
        }

        // 减区把选区挖空了：说清「松手不并这一块」，别让人以为选区无缘无故没了
        if (_regions.Count > 0 && SelectionGeometry() == null)
        {
            HintText.Text = "减区把选区挖空了 · 松手这一块不并进去 · 再拖一块加区 / 右键取消选区";
            return;
        }

        if (_sel is Rect s && IsValid(s))
        {
            if (_tool != CaptureTool.Select)
            {
                var info = ToolCatalog.Of(_tool);
                HintText.Text = $"{info.Name} · {info.Hint} · Esc 逐级退出";
                return;
            }

            HintText.Text = SelectionLabel() +
                "拖动边角微调 · Shift 拖 = 加区 · Alt 拖 = 减区 · 方向键 1px（Shift 10px）· 点标注拖动 / Delete 删除 · 右键取消选区 · Ctrl+Z 撤销 · Enter 复制 · Ctrl+T 贴图 · Ctrl+R 识别 · Ctrl+L 长截图 · , / . 翻历史";
            return;
        }

        if (_hoverTarget is SnapBox hover)
        {
            string extra = _shape switch
            {
                SelectionMode.Lasso => " · 或按住自由圈选",
                SelectionMode.Polyline => " · 或逐点折线围选",
                _ => " · 也可直接拖动框选",
            };
            HintText.Text = $"单击选中「{hover.Label}」{extra} · 滚轮换层级 · 右键 / Esc 取消";
            return;
        }

        string text = _shape switch
        {
            SelectionMode.Lasso => "按住拖动圈出区域 · 松开自动闭合",
            SelectionMode.Polyline => "逐点单击围出区域 · 双击 / Enter 结束",
            _ => "拖动框选区域",
        };

        if (_windowTargets.Count > 0) text += " · 单击选窗口 / 控件 · 滚轮换层级";
        text += " · , / . 翻历史 · 右键 / Esc 取消";

        if (_uiaPending != IntPtr.Zero) text += " · 识别控件中…";
        else if (_uiaTargets.Count == 0 && _windowTargets.Count > 0) text += " · 未检测到控件，可直接拖拽框选";
        HintText.Text = text;
    }

    /// <summary>尺寸条 / 提示里的选区说明：多块报「N 块」，自由 / 折线报「自由选区」。</summary>
    private string SelectionLabel()
    {
        var live = LiveRegions();
        if (live.Count > 1) return $"{live.Count} 块 · ";
        return live.Count == 1 && live[0].IsPolygon ? "自由选区 · " : "";
    }

    /// <summary>标注变了（撤销 / 重做 / 预览）：重画画面与界面。</summary>
    private void RefreshMarks()
    {
        Surface.Preview = null;
        Surface.SkipIndex = -1;
        if (_selectedIndex >= _doc.Marks.Count) SelectMark(-1);
        if (NeedsWatermarkPreview) Surface.Preview = WatermarkMark;
        Surface.InvalidateVisual();
        UpdateChrome();
    }

    /// <summary>
    /// 按物理像素把窗口钉死在覆盖区域上（多屏时是整个虚拟桌面）。
    /// 混合 DPI 下 WPF 的 DIP→像素换算可能差几个像素，用 Win32 再确认一次，
    /// 保证冻结画面与屏幕 1:1 对齐。
    /// </summary>
    private void PinToMonitor()
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost,
            _area.X, _area.Y, _area.Width, _area.Height, Win32.SwpNoactivate);
    }

    /// <summary>
    /// 用窗口真正显示时的比例校准文档的「物理像素 / DIP」。
    /// 跨屏时窗口落在哪块显示器、系统给什么 DPI，只有窗口出来之后才知道；
    /// 校不准会让底图按错的比例铺开（右边 / 下边露出一截窗口底色）。
    /// </summary>
    /// <remarks>
    /// 方向是**物理像素 ÷ DIP**（= 125% 屏上得到 1.25）：<see cref="CaptureDocument.Canvas"/> 拿它
    /// 把底图从像素换算成 DIP，写反了（DIP ÷ 像素 = 0.8）底图会被铺成 1.56 倍大、只看得见左上角，
    /// 而导出用的是同一份比例的倒数关系（<see cref="CaptureDocument.Render"/> 里
    /// <c>Canvas = 像素 / Scale</c> 再乘回 <c>Scale</c>），所以剪贴板里的图看起来还是对的。
    /// </remarks>
    private void SyncDocumentScale()
    {
        if (ActualWidth <= 0 || _shot.PixelWidth <= 0) return;
        double scale = _shot.PixelWidth / ActualWidth;
        if (Math.Abs(scale - _doc.Scale) < 1e-6) return;

        _doc.SetScale(scale);
        Surface.InvalidateVisual();
        UpdateChrome();
    }
}
