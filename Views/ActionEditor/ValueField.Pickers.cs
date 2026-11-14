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
/// 「▾ 来源」里的选择入口：窗口 / 进程 / 文件 / 文件夹 / 屏幕区域 / 取点 / 取色 / 录制按键。
/// </summary>
public partial class ValueField
{
    private void PickWindowTitle()
    {
        var info = SystemPickerDialog.PickWindow(Window.GetWindow(this));
        if (info != null) SetLiteral(info.Title);
    }

    private void PickWindowClass()
    {
        var info = SystemPickerDialog.PickWindow(Window.GetWindow(this));
        if (info != null && info.ClassName.Length > 0) SetLiteral(info.ClassName);
    }

    private void PickProcessName()
    {
        string? name = SystemPickerDialog.PickProcess(Window.GetWindow(this));
        if (!string.IsNullOrWhiteSpace(name)) SetLiteral(name!);
    }

    private void PickFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择文件",
            Filter = "所有文件|*.*",
            InitialDirectory = Directory.Exists(AppPaths.DefaultWorkdir())
                ? AppPaths.DefaultWorkdir() : "",
        };
        if (dlg.ShowDialog() == true) SetLiteral(dlg.FileName);
    }

    private void PickFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择文件夹",
            InitialDirectory = Directory.Exists(AppPaths.DefaultWorkdir())
                ? AppPaths.DefaultWorkdir() : "",
        };
        if (dlg.ShowDialog() == true) SetLiteral(dlg.FolderName);
    }

    private void PickRegion()
    {
        if (!ScreenPicker.TryPickRegion(Window.GetWindow(this), out var area, out _)) return;
        SetLiteral(string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2},{3}", area.X, area.Y, area.Width, area.Height));
        Captured?.Invoke(area);
    }

    private void PickPoint()
    {
        if (!ScreenPicker.TryPickPoint(Window.GetWindow(this), out int x, out int y)) return;

        if (PointPicked != null) PointPicked(x, y);
        else SetLiteral(string.Equals(_spec?.Key, "y", StringComparison.OrdinalIgnoreCase)
            ? y.ToString(CultureInfo.InvariantCulture)
            : x.ToString(CultureInfo.InvariantCulture));
    }

    private void PickColor()
    {
        if (!ScreenPicker.TryPickColor(Window.GetWindow(this), out string hex)) return;
        if (hex.Length > 0) SetLiteral(hex);
    }

    private void RecordKey()
    {
        _inline = null;
        HotkeyCapture.SetHotkey(KeyBox, "");
        KeyPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => KeyBox.Focus()), DispatcherPriority.Input);
    }

    private void OnKeyRecorded(string chord)
    {
        KeyPopup.IsOpen = false;
        if (chord.Length == 0) return;

        string text = (Box.Text ?? "").TrimEnd();
        Box.Text = text.Length == 0 ? chord : text + Environment.NewLine + chord;
        Box.CaretIndex = Box.Text.Length;
        Box.IsReadOnly = false;
        UpdatePanels();
        Box.Focus();
        Changed?.Invoke();
    }

}
