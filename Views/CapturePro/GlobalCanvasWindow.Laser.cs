using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>激光笔拖尾的画层（纯绘制，不吃鼠标；窗口坐标）。</summary>
internal sealed class GlobalCanvasLaserLayer : FrameworkElement
{
    private static readonly Brush Ink = Frozen(new SolidColorBrush(GlobalCanvasLaser.Ink));

    /// <summary>拖尾模型（空 = 不画）。</summary>
    public GlobalCanvasLaser? Trail { get; set; }

    /// <summary>这一帧的时间戳（毫秒）。</summary>
    public long Now { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var trail = Trail;
        if (trail == null) return;

        foreach (var segment in trail.Segments(Now))
        {
            var pen = new Pen(Ink, Math.Max(0.8, segment.Width))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            dc.DrawLine(pen, segment.From, segment.To);
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 全屏画布的激光笔（照上游 <c>snow-recording-effects</c> 的激光拖尾）：开着时每 16ms
/// 采一次指针位置，画一条 500ms 收细的红线；点击穿透时照样看得见（讲课指引就靠它）。
/// </summary>
public partial class GlobalCanvasWindow
{
    private const int LaserIntervalMs = 16;

    private readonly GlobalCanvasLaser _trail = new();
    private DispatcherTimer? _laserTimer;
    private bool _laserOn;
    private bool _laserWasActive;

    private void SetLaser(bool on)
    {
        _laserOn = on;
        Toolbar.SetLaserState(on);

        if (!on)
        {
            StopLaserTimer();
            _trail.Clear();
            _laserWasActive = false;
            LaserLayer.Trail = null;
            LaserLayer.InvalidateVisual();
            return;
        }

        LaserLayer.Trail = _trail;
        LaserLayer.Now = Environment.TickCount64;
        LaserLayer.InvalidateVisual();
        StartLaserTimer();
    }

    private void StartLaserTimer()
    {
        if (_laserTimer == null)
        {
            _laserTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(LaserIntervalMs),
            };
            _laserTimer.Tick += (_, _) => LaserTick();
        }

        _laserTimer.Start();
    }

    private void StopLaserTimer() => _laserTimer?.Stop();

    private void LaserTick()
    {
        if (_closed || !_laserOn) return;

        long now = Environment.TickCount64;
        bool added = false;

        // 指针位置取物理像素再换窗口 DIP：与截图 / 鼠标注入同一套坐标系
        if (Win32.GetCursorPos(out var pt))
        {
            var view = new Point((pt.X - _monitor.Left) / _sx, (pt.Y - _monitor.Top) / _sy);
            if (view.X >= -1 && view.Y >= -1 &&
                view.X <= Surface.ActualWidth + 1 && view.Y <= Surface.ActualHeight + 1)
                added = _trail.Observe(view, now);
        }

        bool active = _trail.HasActive(now);
        if (added || active || _laserWasActive)
        {
            LaserLayer.Trail = _trail;
            LaserLayer.Now = now;
            LaserLayer.InvalidateVisual();
        }

        _laserWasActive = active;
    }
}
