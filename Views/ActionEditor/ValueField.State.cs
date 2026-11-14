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
/// 值的三态（字面量 / 变量引用 / 内联取值）与输入框事件：
/// 加载、面板刷新、清空、插入变量、内联取值的进出。
/// </summary>
public partial class ValueField
{
    // ============ 值的三态 ============

    private void Load(StepArg? value)
    {
        _loading = true;
        try
        {
            value ??= StepArg.FromText("");
            _inline = null;
            _boundVariable = null;
            _listLiteral = null;

            switch (value.Kind)
            {
                case StepArgKind.variable:
                    _boundVariable = value.VariablePath;
                    Box.Text = VariableNameMode
                        ? value.VariablePath
                        : "${" + value.VariablePath + "}";
                    break;

                case StepArgKind.inline:
                    _inline = value.InlineStep ?? Step.Of(FirstPureUnit());
                    break;

                default:
                    _listLiteral = value.Literal.Kind == ValueKind.list ? value.Literal : null;
                    Box.Text = _listLiteral != null
                        ? ListText.Display(_listLiteral)
                        : value.Literal.AsText();
                    break;
            }

            UpdatePanels();
        }
        finally { _loading = false; }
    }

    private void UpdatePanels()
    {
        bool inline = _inline != null;
        bool image = _spec?.Editor == InputEditor.image;

        // 输入框永远可见：字面量 / 变量 / 内联取值都在这一个框里表达，
        // 图片缩略图、区域框选、取值动作单元都不再把它换掉。
        Box.Visibility = Visibility.Visible;
        Box.IsReadOnly = inline;
        Box.Cursor = inline ? Cursors.Arrow : Cursors.IBeam;

        ThumbPanel.Visibility = image && !inline ? Visibility.Visible : Visibility.Collapsed;

        if (inline) UpdateInline();
        else if (image) UpdateImage();

        UpdateStatus();
    }

    /// <summary>内联取值：输入框只读显示「取哪个值」，改参数 / 切回文本都在 ▾ 里。</summary>
    private void UpdateInline()
    {
        var spec = StepRegistry.Default.TryGet(_inline!.Unit, out var unit) ? unit.Spec : null;
        string text = "取值：" + (spec?.Name ?? _inline.Unit);

        bool wasLoading = _loading;
        _loading = true;
        try { Box.Text = text; }
        finally { _loading = wasLoading; }
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        // 手动改过文本就不再是「整串一个变量」：${a}${b} 这种模板仍按文本存。
        if (_boundVariable != null && (Box.Text ?? "").Trim() != "${" + _boundVariable + "}")
            _boundVariable = null;

        if (_missing) MarkMissing(false);
        UpdatePanels();
        Changed?.Invoke();
    }

    /// <summary>
    /// 内联取值时输入框是只读描述：点一下就是「我要自己写字」，切回文本态
    /// （清掉取值动作单元，光标落进空框，接着打字即可）。
    /// </summary>
    private void Box_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_inline == null) return;
        LeaveInline();
    }

    /// <summary>
    /// 标出「必填但还空着」：状态行换成红字。用状态行而不是红框——输入框的边框
    /// 归主题样式（悬停 / 焦点各有自己的颜色），就地改会盖掉那套视觉。
    /// </summary>
    public void MarkMissing(bool on)
    {
        if (_missing == on) return;
        _missing = on;
        UpdateStatus();
    }

    // ============ 插入 / 选择 ============

    private void InsertVariable(string name)
    {
        // 插入变量 = 用户要的是一个值，不再是「现场取值的动作单元」。
        _inline = null;

        if (VariableNameMode)
        {
            Box.Text = name;
            Box.CaretIndex = name.Length;
            Box.Focus();
            Changed?.Invoke();
            return;
        }

        string token = "${" + name + "}";
        string text = Box.Text ?? "";
        int start;
        int length;

        if (Box.SelectionLength > 0)
        {
            start = Math.Clamp(Box.SelectionStart, 0, text.Length);
            length = Math.Clamp(Box.SelectionLength, 0, text.Length - start);
        }
        else if (string.Equals(text.Trim(), name, StringComparison.OrdinalIgnoreCase))
        {
            // 整段就是个变量名（常见的「写成文本」错误）：直接换掉。
            start = 0;
            length = text.Length;
        }
        else if (Box.IsKeyboardFocusWithin)
        {
            start = Math.Clamp(Box.CaretIndex, 0, text.Length);
            length = 0;
        }
        else
        {
            start = text.Length;
            length = 0;
        }

        Box.Text = text.Remove(start, length).Insert(start, token);
        Box.CaretIndex = start + token.Length;

        // 整串正好是一个变量 → 存成引用，保留 list / object / number 类型。
        _boundVariable = string.Equals(Box.Text.Trim(), token, StringComparison.Ordinal) ? name : null;
        Box.IsReadOnly = false;
        UpdatePanels();
        Box.Focus();
        Changed?.Invoke();
    }

    private void SetLiteral(string text)
    {
        LiteralText = text;
        Changed?.Invoke();
    }

    private void ClearValue()
    {
        _inline = null;
        _boundVariable = null;
        _listLiteral = null;
        Box.Text = "";
        UpdatePanels();
        Changed?.Invoke();
    }

    // ============ 内联取值 ============

    /// <summary>切回文本态：丢掉取值动作单元，输入框恢复可编辑并清空。</summary>
    private void LeaveInline()
    {
        if (_inline == null) return;

        _loading = true;
        try
        {
            _inline = null;
            _boundVariable = null;
            _listLiteral = null;
            Box.Text = "";
        }
        finally { _loading = false; }

        Box.IsReadOnly = false;
        UpdatePanels();
        Box.Focus();
        Changed?.Invoke();
    }

    private void EditInline()
    {
        if (_inline == null) return;

        // 宿主（抽屉）接管：推入一层新表单，面包屑多一段；回来时由宿主调 ApplyInlineEdit。
        if (InlineEditor != null)
        {
            InlineEditor(this, _inline, _spec?.Label ?? "参数");
            return;
        }

        var dlg = new StepEditorDialog(_inline, inline: true,
            program: Program, variableNames: VariableNameSource, resourceRoot: ResourceRoot,
            variableInfos: VariableInfoSource)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() == true && dlg.Result != null) ApplyInlineEdit(dlg.Result);
    }

    /// <summary>
    /// 宿主接管内联编辑的钩子：参数是「发起请求的字段 / 现有内联步骤 / 参数标签」。
    /// 抽屉里由编辑器推入一层，回来后调 <see cref="ApplyInlineEdit"/>。
    /// </summary>
    internal Action<ValueField, Step, string>? InlineEditor { get; set; }

    /// <summary>把编辑好的取值步骤写回来（宿主在下一层保存后调用）。</summary>
    internal void ApplyInlineEdit(Step step)
    {
        _inline = step;
        UpdatePanels();
        Changed?.Invoke();
    }

}
