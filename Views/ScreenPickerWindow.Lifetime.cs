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

/// <summary>ScreenPickerWindow 的「生命周期与窗口状态」部分。</summary>
public partial class ScreenPickerWindow : Window
{
    // ============ 生命周期 ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PinToMonitor();
        Activate();
        Focus();

        UpdateLayout();
        PlaceHint();

        if (_pointMode)
        {
            // 打开时先把十字线放到用户按下按钮前的鼠标处（静态画面里它就在那儿）。
            if (Win32.GetCursorPos(out var pt) && TryToDip(pt, out var dip))
                UpdateCrosshair(Clamp(dip));
        }
        else
        {
            // 窗口树枚举很快（几百个句柄），打开时一次即可；UIA 的那一层在悬停时按需补。
            try
            {
                _windowTargets.AddRange(ScreenSnapTargets.FromWindows(_monitor));
                RebuildSnapLines();
            }
            catch { /* 探测失败不影响手动框选 */ }

            UpdateChrome();

            // 打开时鼠标本来就停在某个窗口上：先算一次悬停，省得用户要动一下才看见高亮。
            if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip))
                UpdateHover(Clamp(dip));
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 拖动中按 Esc 只放弃这一次拖动（和 Snipaste / Quicker 一致），再按一次才退出框选。
            if (_drag != DragMode.None) CancelDrag();
            else Cancel();
            e.Handled = true;
            return;
        }

        if (_pointMode) { if (e.Key == Key.Enter && _hasPoint) { ConfirmPoint(); e.Handled = true; } return; }

        // Ctrl+A：整屏（当前显示器）。很多人用惯了截图工具的「截全屏」。
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _drag = DragMode.None;
            _sel = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
            UpdateChrome();
            e.Handled = true;
            return;
        }

        // P：把当前选区（没选区就光标位置）复制成 x,y,w,h，方便填到别处。
        if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_sel is Rect enterSel && IsValid(enterSel))
            {
                ConfirmRegion(enterSel);
            }
            else if (_hoverTarget is SnapBox hover)
            {
                // 还没框选时按 Enter = 选中当前高亮的窗口 / 控件，再按一次才确定
                _sel = ToDipRect(hover.Bounds);
                UpdateChrome();
            }
            e.Handled = true;
            return;
        }

        if (_sel is not Rect sel || _drag != DragMode.None) return;

        // 方向键逐像素微调；按住 Shift 每次 10px。
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        Vector delta = e.Key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => default,
        };
        if (delta == default) return;

        _sel = MoveTo(sel, delta);
        UpdateChrome();
        e.Handled = true;
    }

    /// <summary>放弃当前拖动：正在拖新选区就清掉，拖把手 / 移动就还原。</summary>
    private void CancelDrag()
    {
        _drag = DragMode.None;
        _handle = Handle.None;
        _pressTarget = null;
        _sel = _sel is Rect s && IsValid(s) && _originRect != default ? _originRect : null;
        if (_sel is Rect back && (back.Width < MinSize || back.Height < MinSize)) _sel = null;
        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        Cursor = Cursors.Arrow;
        UpdateChrome();
    }

    /// <summary>复制选区（<c>x,y,w,h</c>）或光标位置（<c>x,y</c>）到剪贴板。</summary>
    private void CopySelection()
    {
        string text;
        if (_sel is Rect s && IsValid(s))
        {
            var (x1, y1) = ToImage(s.TopLeft);
            var (x2, y2) = ToImage(s.BottomRight);
            int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
            text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{_monitor.X + x},{_monitor.Y + y},{Math.Abs(x2 - x1) + 1},{Math.Abs(y2 - y1) + 1}");
        }
        else if (Win32.GetCursorPos(out var cursor))
        {
            text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{cursor.X},{cursor.Y}");
        }
        else return;

        try { Clipboard.SetText(text); }
        catch { return; }   // 剪贴板被别的程序占着：不打断框选

        ShowNotice($"已复制 {text}");
    }

    /// <summary>顶部提示临时换成一句话，2 秒后恢复。</summary>
    private void ShowNotice(string text)
    {
        _notice = text;
        UpdateChrome();

        _noticeTimer ??= new DispatcherTimer();
        _noticeTimer.Stop();
        _noticeTimer.Interval = TimeSpan.FromSeconds(2);
        _noticeTimer.Tick -= NoticeElapsed;
        _noticeTimer.Tick += NoticeElapsed;
        _noticeTimer.Start();
    }

    private void NoticeElapsed(object? sender, EventArgs e)
    {
        _noticeTimer?.Stop();
        _notice = "";
        UpdateChrome();
    }

    private void Window_Cancel(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Cancel();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Cancel()
    {
        Confirmed = false;
        Close();
    }

}
