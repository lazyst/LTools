using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views;

/// <summary>ScreenPickerWindow 的「鼠标输入」部分。</summary>
public partial class ScreenPickerWindow : Window
{
    // ============ 鼠标 ============
    //
    // 全部用窗口级 Preview 事件：鼠标按下后 WPF 会把捕获挂到窗口上（Mouse.Captured == 本窗口），
    // 之后的 MouseMove 只路由到窗口，挂在 Canvas 上的 MouseMove 收不到——框选会「按下就卡住」。

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 底部确认栏是普通按钮：点它不能当成开始框选。
        if (IsOnActionBar(e.OriginalSource as DependencyObject)) return;

        Point p = Clamp(e.GetPosition(Layer));

        if (_pointMode)
        {
            UpdateCrosshair(p);
            ConfirmPoint();
            e.Handled = true;
            return;
        }

        // 双击选区内部 = 确定（和大多数截图工具一致）。
        if (e.ClickCount == 2 && _sel is Rect dbl && dbl.Contains(p))
        {
            ConfirmRegion(dbl);
            e.Handled = true;
            return;
        }

        Handle hit = HitTest(p);
        _pressAt = p;
        _pressMoved = false;
        _pressTarget = null;

        if (hit != Handle.None)
        {
            _drag = hit == Handle.Move ? DragMode.Move : DragMode.Resize;
            _handle = hit;
            _originRect = _sel ?? default;
        }
        else
        {
            // 空白处按下：先按自由拖动开始；松手时若几乎没动、且光标下有窗口 / 控件，就选中它。
            _pressTarget = _hoverTarget;
            _drag = DragMode.New;
            _handle = Handle.None;
            _originRect = _sel ?? default;   // Esc 放弃这次拖动时还原成原来的选区
            _sel = new Rect(p, p);
            SetHover(null);
        }

        _origin = p;

        // 吸附线只取「按下时最上层那个窗口」的边：后台窗口的边不该把框吸走。
        // 拖动期间不再重算，保证手感稳定（Ctrl 可以临时关掉吸附）。
        RebuildSnapLines();

        Cursor = _drag == DragMode.New ? Cursors.Cross : CursorFor(_handle);
        CaptureMouse();
        UpdateChrome();
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        Point p = Clamp(e.GetPosition(Layer));

        if (_pointMode) { UpdateCrosshair(p); return; }

        if (_drag == DragMode.None)
        {
            Cursor = CursorFor(HitTest(p));
            UpdateHover(p);
            return;
        }

        if (!_pressMoved &&
            (Math.Abs(p.X - _pressAt.X) > PressSlop || Math.Abs(p.Y - _pressAt.Y) > PressSlop))
            _pressMoved = true;

        switch (_drag)
        {
            case DragMode.New:
            {
                // 拖出来的边角吸附到附近窗口 / 控件的边（按住 Ctrl 临时关闭）；按住 Shift 锁正方形。
                Point end = SnapPoint(p);
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) end = SquareOf(_origin, end);
                _sel = RectFrom(_origin, end);
                break;
            }
            case DragMode.Move:
                _sel = SnapMove(MoveTo(_originRect, p - _origin));
                break;
            case DragMode.Resize:
                _sel = SnapResize(ResizeFrom(_originRect, _handle, p - _origin), _handle);
                break;
        }

        UpdateChrome();
        e.Handled = true;
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _drag == DragMode.None) return;

        _drag = DragMode.None;
        _handle = Handle.None;
        ReleaseMouseCapture();

        // 几乎没移动 = 单击：选中光标下的窗口 / 控件（和 Snipaste / PixPin 的「点一下即选中」一致）。
        // 拖动了就是自由框选，不抢。
        if (!_pressMoved && _pressTarget is SnapBox target)
        {
            _sel = ToDipRect(target.Bounds);
        }
        else if (!IsValid(_sel))
        {
            // 误点 / 太小：清掉重来，不关闭窗口。
            _sel = null;
        }

        _pressTarget = null;
        Cursor = Cursors.Arrow;
        UpdateChrome();
        e.Handled = true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (IsValid(_sel)) ConfirmRegion(_sel!.Value);
    }

    /// <summary>事件源是否来自底部确认栏（按钮 / 它的父 Border）。</summary>
    private bool IsOnActionBar(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ActionBar)) return true;
            if (source is Button) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

}
