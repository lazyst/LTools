using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 全屏画布的相机（缩放 / 平移）、工具条摆位与轮询。
/// </summary>
/// <remarks>
/// 视图矩阵由 <see cref="GlobalCanvasLayout"/> 给出：<c>scale(zoom)</c> 之后把画布中心平移到
/// 视口中心。标注坐标永远是画布坐标，所以缩放 / 平移只重算矩阵，不动文档。
/// </remarks>
public partial class GlobalCanvasWindow
{
    private DispatcherTimer? _opacityTimer;

    /// <summary>按当前中心 / 缩放重算视图矩阵并重画。</summary>
    private void UpdateView()
    {
        double width = Math.Max(1, Surface.ActualWidth);
        double height = Math.Max(1, Surface.ActualHeight);

        _view = GlobalCanvasLayout.ViewMatrix(_center.X, _center.Y, _zoom, width, height);
        Canvas.View = _view;
        Canvas.CanvasOverride = GlobalCanvasLayout.ViewRect(_view, width, height);
        Canvas.InvalidateVisual();

        // 就地输入框跟着镜头走（缩放 / 平移时用户可能正在打字）
        if (_textEditing) PlaceTextEntry();
    }

    /// <summary>画布坐标 → 窗口 DIP。</summary>
    private Point ToView(Point canvas) => _view.Transform(canvas);

    /// <summary>工具条初始落点：工作区右下各留 15（照上游 <c>placeToolbar(true)</c>）。</summary>
    private void PlaceToolbar(bool initial)
    {
        double width = Math.Max(1, Surface.ActualWidth);
        double height = Math.Max(1, Surface.ActualHeight);
        if (width < 2 || height < 2) return;

        Toolbar.FitWidth(width);

        if (!initial && Toolbar.UserMoved) return;

        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // 刚建出来还没量过 / 被折叠过时 DesiredSize 可能是 (0,0)：退回实际尺寸，
        // 别照 0 宽摆到右边外面去。
        double toolbarWidth = Toolbar.DesiredSize.Width > 0.5 ? Toolbar.DesiredSize.Width : Toolbar.ActualWidth;
        double toolbarHeight = Toolbar.DesiredSize.Height > 0.5 ? Toolbar.DesiredSize.Height : Toolbar.ActualHeight;

        var origin = GlobalCanvasLayout.ToolbarOrigin(width, height, toolbarWidth, toolbarHeight);

        System.Windows.Controls.Canvas.SetLeft(Toolbar, origin.X);
        System.Windows.Controls.Canvas.SetTop(Toolbar, origin.Y);
    }

    private void StartTimers()
    {
        if (_opacityTimer == null)
        {
            _opacityTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(GlobalCanvasLayout.OpacityPollMs),
            };
            _opacityTimer.Tick += (_, _) => UpdateToolbarOpacity();
        }

        _opacityTimer.Start();
    }

    private void StopTimers()
    {
        _opacityTimer?.Stop();
        StopLaserTimer();
    }
}
