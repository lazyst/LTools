using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LTools.Views.Controls;

/// <summary>
/// 拖动时跟随光标的快照幽灵。所有排序交互共用：进入拖动时才创建快照，
/// 之后每次移动只改 Canvas 坐标（不触发重排）。
/// </summary>
internal sealed class DragVisuals
{
    private readonly Window _window;
    private readonly Canvas _layer;
    private FrameworkElement? _ghost;
    private double _ghostWidth;
    private double _ghostHeight;

    public DragVisuals(Window window, Canvas layer)
    {
        _window = window;
        _layer = layer;
    }

    public bool HasGhost => _ghost != null;

    /// <summary>按源元素创建快照幽灵并摆到光标中心。源元素必须在可视树中。</summary>
    public void ShowGhost(FrameworkElement source, Point windowPoint, FrameworkElement? body = null)
    {
        HideGhost();
        _ghostWidth = Math.Max(1, source.ActualWidth);
        _ghostHeight = Math.Max(1, source.ActualHeight);
        _ghost = body != null
            ? DragFx.CreateCard(body, _ghostWidth, _ghostHeight)
            : DragFx.CreateGhost(source);
        _layer.Children.Add(_ghost);
        MoveGhost(windowPoint);
        Mouse.OverrideCursor = Cursors.SizeAll;
    }

    /// <summary>幽灵居中跟随光标。</summary>
    public void MoveGhost(Point windowPoint)
    {
        if (_ghost == null) return;
        var p = _window.TranslatePoint(windowPoint, _layer);
        Canvas.SetLeft(_ghost, p.X - _ghostWidth / 2);
        Canvas.SetTop(_ghost, p.Y - _ghostHeight / 2);
    }

    public void HideGhost()
    {
        if (_ghost == null) return;
        _layer.Children.Remove(_ghost);
        _ghost = null;
    }

    /// <summary>结束（释放光标形状、移除幽灵）。占位框由调用方自行管理。</summary>
    public void End()
    {
        HideGhost();
        Mouse.OverrideCursor = null;
    }
}
