using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>CaptureOverlayWindow 的「鼠标移动分派」部分。</summary>
public partial class CaptureOverlayWindow
{
    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        // 长截图模式：光标只是滚轮的位置，别改光标 / 别更新悬停候选
        if (_scrollMode) return;

        // 二维码标记 / 气泡上：光标形状归它们（手型 / 文本光标），别改选区光标
        if (IsOnQrBubble(e.OriginalSource as DependencyObject) ||
            IsOnQr(e.OriginalSource as DependencyObject))
        {
            return;
        }

        Point p = Clamp(e.GetPosition(Layer));

        // 参考线 / 取色十字跟着光标走（不管在拖还是在悬停）
        UpdateGuides(p);

        if (_drag == DragMode.None)
        {
            // 光标停在工具栏 / 信息条 / 输入框上时，别去动悬停候选和光标形状（取色卡也收起）
            if (_textEditing || IsOverToolbar || IsOverSelectionBar)
            {
                HidePicker();
                return;
            }

            // 取色 / 坐标：选择工具与取色工具下都跟着光标（上游 moveToolActive 那一档）
            if (_tool is CaptureTool.Select or CaptureTool.Picker) ShowPickerAt(p);
            else HidePicker();

            // 折线选区正在逐点单击：只让最后一段虚线跟着光标
            if (_selPolyActive)
            {
                _selPolyCursor = p;
                Surface.PendingSmooth = _shape == SelectionMode.Curve;
                Surface.PendingPolygon = PendingShape();
                Surface.InvalidateVisual();
                return;
            }

            // 取色：光标旁跟一块色卡，不参与吸附 / 悬停探测
            if (_tool == CaptureTool.Picker)
            {
                Cursor = Cursors.Cross;
                return;
            }

            RefreshCursor(p);
            if (_tool == CaptureTool.AutoFilter)
            {
                UpdateAutoFilterHover(p);      // 光标下那一块点亮（上游 region_at）
                return;
            }
            if (!IsValid(_sel) || _smartPreviewActive) UpdateHover(p);
            return;
        }

        if (!_pressMoved &&
            (Math.Abs(p.X - _pressAt.X) > PressSlop || Math.Abs(p.Y - _pressAt.Y) > PressSlop))
            _pressMoved = true;

        switch (_drag)
        {
            case DragMode.New:
                UpdateNewDrag(p);
                break;
            case DragMode.Lasso:
                // 自由选区：补点 + 外接框跟着走（画面上画的是那条虚线，不是这个框）
                SelectionShape.Append(_lasso, p);
                RefreshSelection();
                break;
            case DragMode.Move:
            case DragMode.MoveRegion:
                // 按住 / 松开「移动整个选区」可以中途换口径（上游 rebaseMoveDrag）：
                // 换完按新口径继续拖，指针位置不跳
                RebaseMoveIfScopeChanged(p);
                if (_drag == DragMode.MoveRegion) MoveOneRegion(p - _origin);
                else MoveFrame(SnapMove(MoveTo(_originRect, p - _origin)));
                break;
            case DragMode.Resize:
            {
                double ratio = DragRatio(marquee: false);
                if (ratio > 0)
                {
                    ResizeFrame(AspectLock.Drag(_originRect, _handle, p - _origin,
                        new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)),
                        MinSize, ratio));
                }
                else
                {
                    ResizeFrame(SnapResize(ResizeFrom(_originRect, _handle, p - _origin), _handle));
                }
                break;
            }
            case DragMode.Draw:
                UpdateToolDrag(p);
                break;
            case DragMode.Erase:
                EraseAt(p);
                break;
            case DragMode.Mark:
                UpdateMarkDrag(p);
                break;
            case DragMode.MarkResize:
                UpdateMarkResize(p);
                break;
            case DragMode.MagnifierLens:
                UpdateMagnifierLensDrag(p);
                break;
            case DragMode.Marquee:
                UpdateMarkMarquee(p);
                break;
            case DragMode.SmartPress:
                // 预选态按住拖 = 手拖框选：从**按下点**起框（上游 beginSelectionDrag(pressPosition, Marquee)）
                if (_smart.ShouldStartManualDrag(p, PressSlop))
                {
                    BeginSmartMarquee(_smart.PressPosition);
                    UpdateNewDrag(p);
                }
                break;
        }

        UpdateChrome();
        e.Handled = true;
    }

    /// <summary>正在拖一块新矩形（新框 / 加区 / 减区 / 智能选区转手拖）：算这一格的几何。</summary>
}
