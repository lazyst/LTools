using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Script;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 确定 / 取消 / 上一步 / 下一步 / 保存并继续添加，以及表单自己的键处理：
/// Enter = 确定、Ctrl+Enter = 保存这一步、Esc = 取消、Alt+←/→ = 上下一步。
/// </summary>
public partial class StepFormView
{
    // ============ 确定 / 取消 ============

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySave()) return;
        NavigateDelta = 0;
        Saved?.Invoke();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySave()) return;
        ContinueAdding = true;
        Saved?.Invoke();
    }

    /// <summary>上一步 / 下一步：先按「确定」的规则保存，再让编辑器打开相邻步骤。</summary>
    private void Navigate(int delta)
    {
        if (!TrySave()) return;
        NavigateDelta = delta;
        Saved?.Invoke();
    }

    /// <summary>
    /// Enter = 确定、Ctrl+Enter = 保存这一步、Esc = 取消、Alt+← / Alt+→ = 上一步 / 下一步。
    /// </summary>
    /// <remarks>
    /// 抽屉里没有「窗口级默认按钮」可以靠（<c>IsDefault</c> / <c>IsCancel</c> 会落到动作编辑器窗口自己的
    /// 确定 / 取消上，一按 Enter 就把整个动作存了），所以在表单自己这里收。
    /// 多行输入框里的 Enter 是换行，必须让给它（<see cref="TextBox.AcceptsReturn"/>）——
    /// 那种情况下用 <b>Ctrl+Enter</b> 保存这一步（多行框里 Ctrl+Enter 本来就没人用）。
    /// ▾ 来源菜单与录制按键在 Popup 里，键事件走它们自己的树，不会跑到这里来。
    /// </remarks>
    private void StepFormView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if ((modifiers & System.Windows.Input.ModifierKeys.Alt) != 0)
        {
            if (e.Key == System.Windows.Input.Key.Left && PrevButton.Visibility == Visibility.Visible)
            {
                Navigate(-1);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Right && NextButton.Visibility == Visibility.Visible)
            {
                Navigate(1);
                e.Handled = true;
            }

            return;
        }

        // Ctrl+Enter = 保存这一步：多行框里 Enter 让给换行，但它要有个「确定」的出口。
        if (e.Key == System.Windows.Input.Key.Enter
            && (modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            if (TrySave())
            {
                NavigateDelta = 0;
                ContinueAdding = false;
                Saved?.Invoke();
            }

            e.Handled = true;
            return;
        }

        // Ctrl / Shift + Enter 留给控件自己（多行框里用来换行之类的习惯用法）。
        if (modifiers != System.Windows.Input.ModifierKeys.None) return;

        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Cancelled?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter && !FocusAcceptsReturn())
        {
            if (TrySave())
            {
                NavigateDelta = 0;
                ContinueAdding = false;
                Saved?.Invoke();
            }

            e.Handled = true;
        }
    }

    /// <summary>焦点在多行输入框里时 Enter 是换行，别当成「确定」。</summary>
    private static bool FocusAcceptsReturn() =>
        System.Windows.Input.Keyboard.FocusedElement is TextBox { AcceptsReturn: true };

}
