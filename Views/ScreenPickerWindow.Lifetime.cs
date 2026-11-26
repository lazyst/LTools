using System;
using System.Windows;
using System.Windows.Input;
using LTools.Native;

namespace LTools.Views;

/// <summary>ScreenPickerWindow 的「生命周期与键盘」部分。</summary>
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

        // 打开时先把十字线放到用户按下按钮前的鼠标处（静态画面里它就在那儿）。
        if (Win32.GetCursorPos(out var pt) && TryToDip(pt, out var dip)) UpdateCrosshair(Clamp(dip));
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && _hasPoint)
        {
            ConfirmPoint();
            e.Handled = true;
        }
    }

    private void Window_Cancel(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Cancel();
    }

    private void Cancel()
    {
        Confirmed = false;
        Close();
    }
}
