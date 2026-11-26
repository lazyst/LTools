using System.Windows;
using System.Windows.Input;

namespace LTools.Views;

/// <summary>ScreenPickerWindow 的「鼠标」部分：移动画十字准线，单击即确认。</summary>
public partial class ScreenPickerWindow : Window
{
    // 全部用窗口级 Preview 事件：窗口自己处理，不依赖 Canvas 的命中（取点没有可选中的子元素）。

    private void Window_MouseMove(object sender, MouseEventArgs e)
        => UpdateCrosshair(Clamp(e.GetPosition(Layer)));

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        UpdateCrosshair(Clamp(e.GetPosition(Layer)));
        ConfirmPoint();
        e.Handled = true;
    }
}
