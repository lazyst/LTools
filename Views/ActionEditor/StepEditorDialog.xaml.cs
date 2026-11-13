using System;
using System.Collections.Generic;
using System.Windows;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 步骤参数表单的模态壳（没有宿主窗口时用）：标题栏 + <see cref="StepFormView"/>。
/// </summary>
/// <remarks>
/// <para>
/// 日常编辑走动作编辑器右栏的抽屉（<c>ActionEditorWindow.Drawer.cs</c>），不弹这个窗口；
/// 它保留下来服务两类调用：<see cref="ValueField"/> 在抽屉之外（独立窗口里）编辑内联取值，
/// 以及无头冒烟 / 未来别的宿主。
/// </para>
/// <para>
/// 表单本身不在这个文件里：<c>StepFormView.xaml(.cs)</c> 才是唯一实现，别在这里再写一份。
/// </para>
/// </remarks>
public partial class StepEditorDialog : Window
{
    private readonly StepFormView _form;

    public StepEditorDialog(Step step, bool inline = false,
        ActionProgram? program = null, Func<IEnumerable<string>>? variableNames = null,
        string? resourceRoot = null, Func<IReadOnlyList<VariableInfo>>? variableInfos = null)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);

        Title = inline ? "编辑取值" : "编辑步骤";

        _form = new StepFormView(step, inline, program, variableNames, resourceRoot, variableInfos);
        _form.Saved += () => DialogResult = true;
        _form.Cancelled += () => DialogResult = false;
        FormHost.Content = _form;
    }

    /// <summary>确定后的步骤（取消时为 null）。</summary>
    public Step? Result => _form.Result;

    /// <summary>-1 / +1 = 用户点了上一步 / 下一步（编辑器据此打开相邻步骤）；0 = 正常确定。</summary>
    public int NavigateDelta => _form.NavigateDelta;

    /// <summary>「保存并继续添加」：添加流程里再开一个同动作单元的空表单。</summary>
    public bool ContinueAdding => _form.ContinueAdding;

    /// <summary>编辑器在 ShowDialog 之前调用：显示上一步 / 下一步。</summary>
    internal void EnableNavigation(bool canPrevious, bool canNext) =>
        _form.EnableNavigation(canPrevious, canNext);

    /// <summary>添加新步骤时调用：显示「保存并继续添加」。</summary>
    internal void EnableContinueAdding() => _form.EnableContinueAdding();
}
