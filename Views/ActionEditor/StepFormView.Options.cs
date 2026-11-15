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
/// 步骤选项（失败策略 / 延迟 / 重试 / 跳过 / 备注）与打开表单时的初始焦点。
/// </summary>
public partial class StepFormView
{
    // ============ 选项 ============

    private void BuildOptions()
    {
        FailBox.SelectedIndex = _step.OnFail == FailMode.@continue ? 1 : 0;
        DelayBox.Text = _step.DelayMs > 0
            ? _step.DelayMs.ToString(CultureInfo.InvariantCulture) : "";
        RetryBox.Text = _step.Retry > 0
            ? _step.Retry.ToString(CultureInfo.InvariantCulture) : "";
        RetryDelayBox.Text = _step.RetryDelayMs == Step.DefaultRetryDelayMs
            ? "" : _step.RetryDelayMs.ToString(CultureInfo.InvariantCulture);

        // 「只重试」是可编辑下拉：常用组合一点就有，特殊的组合（precondition,unknown）可以直接写。
        RetryOnBox.Items.Clear();
        foreach (string preset in new[] { "notFound", "timeout", "notFound,timeout", "precondition" })
            RetryOnBox.Items.Add(preset);
        RetryOnBox.Text = ActionErrors.Normalize(_step.RetryOn);

        DisabledBox.IsChecked = _step.Disabled;
        NoteBox.Text = _step.Note ?? "";
        SyncRetryOnRow();
    }

    /// <summary>
    /// 「只重试」只在真的会重试时才显示：次数留空 / 0 时这一组没有意义，
    /// 也省得那一行把「步骤选项」撑成两行（窄抽屉里最容易变形的地方）。
    /// </summary>
    private void SyncRetryOnRow()
        => RetryOnRow.Visibility = RetryCount() > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>当前填的重试次数（读不懂 = 0，由保存时的校验去提示）。</summary>
    private int RetryCount()
        => int.TryParse(RetryBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? Math.Max(0, n) : 0;

    private void RetryBox_TextChanged(object sender, TextChangedEventArgs e) => SyncRetryOnRow();

    private void FocusFirstEditor()
    {
        var first = _rows.FirstOrDefault(r => r.Host.Visibility == Visibility.Visible);
        if (first == null) return;

        if (first.Editor.Field is { } value) value.FocusEditor();
        else if (first.Editor.Element is TextBox box) { box.Focus(); box.SelectAll(); }
        else if (first.Editor.Element is Panel panel && FindEditorBox(panel) is { } inner)
        {
            inner.Focus();
            if (first.Spec.Editor == InputEditor.code) inner.CaretIndex = 0;
        }
    }

    /// <summary>包装过的编辑器（文件 / 代码）里第一个可输入的文本框，用于打开对话框时聚焦。</summary>
    private static TextBox? FindEditorBox(DependencyObject root) =>
        FindChild<TextBox>(root, box => box.IsEnabled && box.IsVisible);

    private static T? FindChild<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed && match(typed)) return typed;
            if (FindChild(child, match) is { } found) return found;
        }
        return null;
    }

}
