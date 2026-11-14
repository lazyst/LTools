using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;
using Microsoft.Win32;

namespace LTools.Views;

/// <summary>
/// 弹层打开期间自己管关闭（StaysOpen=True）：点外部 / 滚轮 / 移窗 / 窗口失焦。
/// </summary>
public partial class ValueField
{
    // ============ 自己管关闭（StaysOpen=True）============

    private void AttachMenuWindow()
    {
        Window? window = Window.GetWindow(this);
        if (ReferenceEquals(window, _menuWindow)) return;

        DetachMenuWindow();
        _menuWindow = window;
        if (window == null) return;

        window.PreviewMouseDown += MenuWindow_MouseDown;
        window.PreviewMouseWheel += MenuWindow_Wheel;
        window.Deactivated += MenuWindow_Deactivated;
        window.LocationChanged += MenuWindow_Moved;
        window.SizeChanged += MenuWindow_Moved;
    }

    private void DetachMenuWindow()
    {
        if (_menuWindow == null) return;

        _menuWindow.PreviewMouseDown -= MenuWindow_MouseDown;
        _menuWindow.PreviewMouseWheel -= MenuWindow_Wheel;
        _menuWindow.Deactivated -= MenuWindow_Deactivated;
        _menuWindow.LocationChanged -= MenuWindow_Moved;
        _menuWindow.SizeChanged -= MenuWindow_Moved;
        _menuWindow = null;
    }

    /// <summary>
    /// 点弹层外就关（弹层里的点击走不到窗口这一层）。▾ 与图片胶囊自己负责开关，
    /// 所以点它们时不让这里先关一次——否则会「关掉又立刻开」。
    /// </summary>
    private void MenuWindow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Menu.IsOpen || Menu.Child.IsMouseOver) return;
        if (SourceButton.IsMouseOver || ThumbPanel.IsMouseOver) return;
        Menu.IsOpen = false;
    }

    private void MenuWindow_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Menu.IsOpen && !Menu.Child.IsMouseOver) Menu.IsOpen = false;
    }

    private void MenuWindow_Deactivated(object? sender, EventArgs e) => Menu.IsOpen = false;

    private void MenuWindow_Moved(object? sender, EventArgs e) => Menu.IsOpen = false;

}
