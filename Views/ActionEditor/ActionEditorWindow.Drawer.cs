using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 右栏的步骤参数抽屉（P1-1）：编辑 / 新增步骤、内联取值都在这里，**步骤列表始终看得见**。
/// </summary>
/// <remarks>
/// <para>
/// 抽屉是一叠 <see cref="StepFormView"/>：根层是「编辑 / 新增这一步」，内联取值在它上面再推一层。
/// 面包屑按这叠表单生成，点哪一段就回到那一层（上面几层未保存的内容一起丢掉——点面包屑就是这个意思）。
/// </para>
/// <para>
/// 表单**只在抽屉里交互**：保存 / 取消由 <see cref="StepFormView.Saved"/> / <see cref="StepFormView.Cancelled"/>
/// 回到这里，再按层的类型落位——根层写回动作（<c>Owner[Index]</c> / 插入），内联层写回父表单的那个字段。
/// 没有任何 <c>ShowDialog</c>：以前的模态循环会把步骤列表完全遮住，用户不知道自己改的是哪一步。
/// </para>
/// <para>
/// 右栏宽度是「跟着抽屉走」的：打开时 440（够放下参数行），收起回到 286。
/// 窗口的 <c>MinWidth</c> 也因此按 242 + 360 + 440 提上去了，别再调回 900。
/// </para>
/// </remarks>
public partial class ActionEditorWindow
{
    private enum DrawerKind
    {
        /// <summary>编辑已有步骤（可上一步 / 下一步）。</summary>
        edit,

        /// <summary>新增步骤（可「保存并继续添加」）。</summary>
        add,

        /// <summary>内联取值：保存后写回父表单的字段。</summary>
        inline,
    }

    /// <summary>抽屉里的一层：一份表单 + 它保存后往哪写。</summary>
    private sealed class DrawerFrame
    {
        public required StepFormView Form { get; init; }

        /// <summary>面包屑上显示的那一段（动作单元名 / 参数名）。</summary>
        public required string Label { get; init; }

        public required DrawerKind Kind { get; init; }

        /// <summary>根层：步骤落在 <c>Owner[Index]</c>（新增时是插入位置）。</summary>
        public List<Step>? Owner { get; init; }

        public int Index { get; init; }

        /// <summary>内联层：保存后写回这个字段。</summary>
        public ValueField? Field { get; init; }

        /// <summary>
        /// 根层「编辑」打开时那一步的对象引用。保存前用它确认「这一格还是刚才那一步」——
        /// 编辑期间撤销 / 删除 / 拖动会把它挪走，按旧下标写回会改错步骤。
        /// </summary>
        public Step? Original { get; init; }

        public string UnitKey { get; init; } = "";
    }

    private readonly List<DrawerFrame> _drawer = new();

    /// <summary>保存过程中重入保护（保存会重建步骤列表，行事件可能再回来一次）。</summary>
    private bool _drawerBusy;

    private bool DrawerOpen => _drawer.Count > 0;

    // ============ 入口（Steps.cs 调） ============

    /// <summary>编辑一行步骤：在抽屉里开根层。<paramref name="row"/> 必须是真正的那一行。</summary>
    private void EditInDrawer(StepRow row)
    {
        if (row.Step == null || row.Owner == null) return;

        DrawerFrame? top = DrawerOpen ? _drawer[^1] : null;
        if (top != null && top.Kind == DrawerKind.edit
            && ReferenceEquals(top.Owner, row.Owner) && top.Index == row.Index)
            return;   // 已经在编辑这一步（连点两下 / ✎）：别白扔表单里刚填的内容

        if (!ConfirmReplaceDrawer()) return;

        OpenRootEdit(row.Step, row.Owner, row.Index);
    }

    /// <summary>新增步骤（没有默认参数、要走表单的那种）。</summary>
    private void AddInDrawer(string unitKey, List<Step> list, int index)
    {
        if (!ConfirmReplaceDrawer()) return;

        var form = CreateStepForm(Step.Of(unitKey), inline: false);
        form.EnableContinueAdding();
        PushFrame(new DrawerFrame
        {
            Form = form,
            Label = StepSummary.Title(unitKey),
            Kind = DrawerKind.add,
            Owner = list,
            Index = Math.Clamp(index, 0, list.Count),
            UnitKey = unitKey,
        });
    }

    private void OpenRootEdit(Step step, List<Step> owner, int index)
    {
        var form = CreateStepForm(step, inline: false);

        // 「上一步 / 下一步」：按**当前行**在列表里的邻居算，保存后照旧能顺着改一串。
        StepRow? row = _rows.FirstOrDefault(r => ReferenceEquals(r.Step, step));
        if (row != null)
            form.EnableNavigation(NeighborStepRow(row, -1) != null, NeighborStepRow(row, 1) != null);

        _drawer.Clear();
        PushFrame(new DrawerFrame
        {
            Form = form,
            Label = StepSummary.Title(step.Unit),
            Kind = DrawerKind.edit,
            Owner = owner,
            Index = index,
            Original = step,
            UnitKey = step.Unit,
        });
    }

    private StepFormView CreateStepForm(Step step, bool inline) =>
        new(step, inline, program: _program, variableNames: VariableNames,
            resourceRoot: ResourceRoot, variableInfos: VariableInfos);

    // ============ 抽屉本身 ============

    private void PushFrame(DrawerFrame frame)
    {
        // 表单里某个参数要编辑内联取值：在抽屉里再推一层，父表单原样留在下面。
        frame.Form.InlineEditorRequested = (field, step, label) => PushInlineFrame(field, step, label);
        frame.Form.Saved += () => ApplyFrame(frame);
        frame.Form.Cancelled += () => CancelFrame(frame);

        _drawer.Add(frame);
        ShowTopFrame();
    }

    private void PushInlineFrame(ValueField field, Step step, string label)
    {
        PushFrame(new DrawerFrame
        {
            Form = CreateStepForm(step, inline: true),
            Label = label,
            Kind = DrawerKind.inline,
            Field = field,
            UnitKey = step.Unit,
        });
    }

    /// <summary>把最上面那一层放出来（内容、面包屑、右栏宽度一起切过去）。</summary>
    private void ShowTopFrame()
    {
        if (_drawer.Count == 0)
        {
            CloseDrawer();
            return;
        }

        DrawerFrame top = _drawer[^1];
        DrawerHost.Content = top.Form;
        RebuildBreadcrumb();

        ParamsPage.Visibility = Visibility.Visible;
        VariablesPage.Visibility = Visibility.Collapsed;
        if (ParamsTab.IsChecked != true) ParamsTab.IsChecked = true;

        RightColumn.Width = new GridLength(440);
    }

    /// <summary>收起抽屉：右栏回到「变量」与原来的宽度。</summary>
    private void CloseDrawer()
    {
        _drawer.Clear();
        DrawerHost.Content = null;
        DrawerBreadcrumb.Children.Clear();
        DrawerBreadcrumb.Visibility = Visibility.Collapsed;
        ParamsPage.Visibility = Visibility.Collapsed;
        VariablesPage.Visibility = Visibility.Visible;
        RightColumn.Width = new GridLength(VariablesColumnWidth);

        if (VariablesTab.IsChecked != true) VariablesTab.IsChecked = true;
    }

    /// <summary>
    /// 保存一层：根层写回动作（编辑 / 插入），内联层写回父表单的字段。
    /// <paramref name="allowContinue"/> 为 false 时不再续开下一层（窗口「确定」/ 关闭前收尾用）。
    /// </summary>
    private void ApplyFrame(DrawerFrame frame, bool allowContinue = true)
    {
        if (_drawerBusy) return;
        if (frame.Form.Result is not Step result)
        {
            CancelFrame(frame);
            return;
        }

        // 只有最上面那层能交互；下面几层的事件不该在这一刻真的落到动作里。
        if (_drawer.Count == 0 || !ReferenceEquals(_drawer[^1], frame)) return;

        if (frame.Kind == DrawerKind.inline)
        {
            frame.Field?.ApplyInlineEdit(result);
            _drawer.RemoveAt(_drawer.Count - 1);
            ShowTopFrame();
            return;
        }

        _drawerBusy = true;
        try
        {
            // 编辑期间撤销 / 删除 / 拖动可能把目标列表整条换掉：先确认真实还在动作里。
            if (!IsLiveList(frame.Owner))
            {
                CloseDrawer();
                ConfirmDialog.Info(this, "步骤已经变了",
                    "编辑这一步的时候，动作里的步骤被撤销、删除或移动过，这次修改没有保存。");
                return;
            }

            List<Step> owner = frame.Owner!;

            if (frame.Kind == DrawerKind.edit)
            {
                // 再确认那一格还是刚才那一步（列表还在、但这一步被挪走了）。
                if (frame.Original == null
                    || frame.Index >= owner.Count
                    || !ReferenceEquals(owner[frame.Index], frame.Original))
                {
                    CloseDrawer();
                    ConfirmDialog.Info(this, "步骤已经变了",
                        "编辑这一步的时候，动作里的步骤被撤销、删除或移动过，这次修改没有保存。");
                    return;
                }

                PushUndo($"编辑「{StepSummary.Title(frame.UnitKey)}」");
                owner[frame.Index] = result;
                ReloadRows(result);

                int delta = allowContinue ? frame.Form.NavigateDelta : 0;
                if (delta == 0)
                {
                    CloseDrawer();
                    return;
                }

                StepRow? saved = _rows.FirstOrDefault(r => ReferenceEquals(r.Step, result));
                StepRow? next = saved == null ? null : NeighborStepRow(saved, delta);
                if (next?.Step == null || next.Owner == null)
                {
                    CloseDrawer();
                    return;
                }

                OpenRootEdit(next.Step, next.Owner, next.Index);
                return;
            }

            // 新增：插到发起时的位置，接着可能再开一个同样的空表单。
            PushUndo($"添加「{StepSummary.Title(frame.UnitKey)}」");
            int at = Math.Clamp(frame.Index, 0, owner.Count);
            owner.Insert(at, result);
            NoteUnitUsed(frame.UnitKey);
            ReloadRows(result);

            if (!allowContinue || !frame.Form.ContinueAdding)
            {
                CloseDrawer();
                return;
            }

            _drawer.Clear();
            AddInDrawer(frame.UnitKey, owner, at + 1);
        }
        finally
        {
            _drawerBusy = false;
        }
    }

    /// <summary>这个列表还挂在动作的步骤树里吗（撤销 / 删除会把整条分支换掉）。</summary>
    private bool IsLiveList(List<Step>? list) =>
        list != null && (ReferenceEquals(list, _program.Steps) || ContainsList(_program.Steps, list));

    private static bool ContainsList(List<Step> steps, List<Step> target)
    {
        foreach (Step step in steps)
        {
            if (ReferenceEquals(step.Then, target) || ReferenceEquals(step.Else, target)
                || ReferenceEquals(step.Body, target))
                return true;

            if (ContainsList(step.Then, target) || ContainsList(step.Else, target)
                || ContainsList(step.Body, target))
                return true;
        }

        return false;
    }

    /// <summary>取消一层：它自己和它上面几层一起收掉（下面还在的那层接着显示）。</summary>
    private void CancelFrame(DrawerFrame frame)
    {
        int index = _drawer.IndexOf(frame);
        if (index < 0) return;

        _drawer.RemoveRange(index, _drawer.Count - index);
        ShowTopFrame();
    }

    /// <summary>抽屉里已经有表单改过内容时，换目标先问一句（别静默丢掉刚填的东西）。</summary>
    private bool ConfirmReplaceDrawer()
    {
        if (!_drawer.Any(frame => frame.Form.HasEdits)) return true;

        return ConfirmDialog.Confirm(this, "放弃当前编辑？",
            "右侧还有一步没保存，换到别的步骤会丢掉那部分改动。",
            danger: true, okText: "放弃并切换");
    }

    // ============ 面包屑 ============

    /// <summary>
    /// 按抽屉里的层重建面包屑：`编辑「文本处理」› 取值：路径`。
    /// 最后一段是当前层（不可点），前面每段都能点回去。
    /// </summary>
    private void RebuildBreadcrumb()
    {
        DrawerBreadcrumb.Children.Clear();

        if (_drawer.Count <= 1)
        {
            DrawerBreadcrumb.Visibility = Visibility.Collapsed;
            return;
        }

        DrawerBreadcrumb.Visibility = Visibility.Visible;
        for (int i = 0; i < _drawer.Count; i++)
        {
            if (i > 0)
                DrawerBreadcrumb.Children.Add(new TextBlock
                {
                    Text = "›",
                    Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (System.Windows.Media.Brush)FindResource("HintTextBrush"),
                    FontSize = (double)FindResource("FontSizeHint"),
                });

            int depth = i;
            if (i == _drawer.Count - 1)
            {
                DrawerBreadcrumb.Children.Add(new TextBlock
                {
                    Text = _drawer[i].Label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = (double)FindResource("FontSizeHint"),
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                break;
            }

            var button = new Button
            {
                Content = _drawer[i].Label,
                Style = (Style)FindResource("BtnGhost"),
                Padding = new Thickness(4, 1, 4, 1),
                MinHeight = 20,
                FontSize = (double)FindResource("FontSizeHint"),
                ToolTip = "回到这一层（上面几层没保存的内容会丢掉）",
            };
            button.Click += (_, _) => PopTo(depth);
            DrawerBreadcrumb.Children.Add(button);
        }
    }

    /// <summary>点面包屑：回到第 <paramref name="depth"/> 层，上面几层丢掉。</summary>
    private void PopTo(int depth)
    {
        if (depth < 0 || depth >= _drawer.Count - 1) return;

        _drawer.RemoveRange(depth + 1, _drawer.Count - depth - 1);
        ShowTopFrame();
    }

    // ============ 右栏 tab ============

    private void RightTab_Checked(object sender, RoutedEventArgs e)
    {
        // 构造期 XAML 还没建完，Checked 可能比字段更早到。
        if (VariablesPage == null || ParamsPage == null || ParamsTab == null) return;

        if (ParamsTab.IsChecked == true && !DrawerOpen)
        {
            // 没有正在编辑的步骤时「参数」页是空的：直接弹回「变量」。
            if (VariablesTab.IsChecked != true) VariablesTab.IsChecked = true;
            return;
        }

        bool showParams = ParamsTab.IsChecked == true;
        VariablesPage.Visibility = showParams ? Visibility.Collapsed : Visibility.Visible;
        ParamsPage.Visibility = showParams ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============ 窗口收尾 ============

    /// <summary>
    /// 窗口「确定」/ 关闭时选保存：先把抽屉里正在编辑的表单收尾。
    /// 校验不过返回 false（留在编辑器里，表单自己会标出缺哪一项）；取值层没确定就按取消处理。
    /// </summary>
    private bool TryCommitDrawer()
    {
        if (_drawer.Count == 0) return true;

        DrawerFrame top = _drawer[^1];
        if (top.Kind == DrawerKind.inline)
        {
            CancelFrame(top);
            return true;
        }

        if (!top.Form.TrySaveStep()) return false;

        // 窗口马上要关了：不再续开下一层（上一步 / 下一步 / 继续添加都按「就到这里」处理）。
        ApplyFrame(top, allowContinue: false);
        return true;
    }

    /// <summary>窗口「取消」：抽屉里的东西一起丢掉。</summary>
    private void DiscardDrawer() => CloseDrawer();
}
