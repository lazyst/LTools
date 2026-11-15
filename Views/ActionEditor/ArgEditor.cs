using System;
using System.Collections.Generic;
using System.Windows;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 一个参数编辑器（P4）：按 <see cref="InputSpec.Editor"/> 分形态，每种形态一个实现，
/// 各管自己怎么建控件、怎么把值读回来。
/// </summary>
/// <remarks>
/// <para>
/// 以前 <c>BuildEditor</c> 里一个大 <c>switch</c> 混着五种控件的建法与读法（230 行）。
/// 现在每种形态一个文件、一个类，<c>BuildEditor</c> 只做分发——新增形态就加一个实现。
/// </para>
/// <para>
/// 行为没变：控件树、事件接线、冻结处理都原样搬过来；<see cref="Field"/> 只对值字段非空，
/// 因为只有它能表达三态、也只有它带状态行。
/// </para>
/// </remarks>
internal interface IArgEditor
{
    /// <summary>摆在参数行里的控件（值字段自带状态行，别的形态是裸控件）。</summary>
    FrameworkElement Element { get; }

    /// <summary>把控件当前的值读回来（冻结时返回原引用）。</summary>
    StepArg? Read();

    /// <summary>值字段（三态控件）；开关 / 选项 / 多选 / 代码没有。</summary>
    ValueField? Field { get; }

    /// <summary>标出「必填但还空着」：只有值字段有状态行可写，别的形态不做事。</summary>
    void MarkMissing(bool on)
    {
    }
}

/// <summary>
/// 建一个参数编辑器要用的料：声明、当前值、宿主表单（取主题资源 / 通知刷新），
/// 以及反填路径（框选切枚举、取点填 X / Y）。
/// </summary>
internal sealed class ArgEditorContext
{
    public required InputSpec Spec { get; init; }

    /// <summary>当前值（步骤里存的，取不到就是声明的默认值）。</summary>
    public required StepArg? Current { get; init; }

    /// <summary>当前步骤所属的动作单元键（代码编辑器按它分流「检查语法」）。</summary>
    public string? UnitKey { get; init; }

    /// <summary>宿主表单：<c>FindResource</c> 走它的资源树。</summary>
    public required FrameworkElement Owner { get; init; }

    /// <summary>控件值变了（同步参数、刷新可见性与摘要）。</summary>
    public required Action Edited { get; init; }

    /// <summary>注册「按英文键改这一行的选中项」（框选后把配套枚举切到 <c>rect</c>）。</summary>
    public required Action<Action<string>> RegisterEnum { get; init; }

    /// <summary>注册「按文本写入这一行」（屏幕取点一次填好 X / Y）。</summary>
    public required Action<Action<string>> RegisterField { get; init; }

    /// <summary>注册「按文本写入固定区域参数」（框选结果落到 <c>x,y,w,h</c>）。</summary>
    public required Action<Action<string>> RegisterRect { get; init; }

    /// <summary>「从列表选…」的候选（按键 / 宏 / 子动作）；null = 这个参数不是候选型。</summary>
    public Func<IEnumerable<StepChoice>>? Choices { get; init; }

    public ActionProgram? Program { get; init; }

    public Func<IEnumerable<string>>? VariableNames { get; init; }

    public Func<IReadOnlyList<VariableInfo>>? VariableInfos { get; init; }

    /// <summary>草稿资源根（模板图缩略图认 <c>files/x.png</c>）。</summary>
    public string? ResourceRoot { get; init; }

    /// <summary>框选 / 截图完成后的通知（找图 / 找字顺手收紧搜索区域）。</summary>
    public Action<Int32Rect>? Captured { get; init; }

    /// <summary>屏幕上取到一次点（参数是屏幕坐标时填好同组的 X / Y）。</summary>
    public Action<int, int>? PointPicked { get; init; }
}
