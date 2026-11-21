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
/// 「▾ 来源」弹层：两页一次建好（主列表 + 取值动作单元），换页只切可见性；
/// 菜单里只列这个参数适用的项（由 InputSpec.PickerSet 决定）。
/// </summary>
public partial class ValueField
{
    // ============ 来源菜单 ============

    private void Source_Click(object sender, RoutedEventArgs e) => OpenMenu();

    private void OpenMenu()
    {
        if (Pickers == PickerKind.none) return;
        if (Menu.IsOpen) { Menu.IsOpen = false; return; }

        BuildMenu();
        AttachMenuWindow();
        Menu.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SearchBox.Text = "";
            FitMenuHeight();     // 先按「所有行都显示」量高度：两页共用一个高度，换页才不跳
            ApplyFilter("");
            SearchBox.Focus();
        }), DispatcherPriority.Input);
    }

    private void BuildMenu()
    {
        MenuList.Children.Clear();
        _rows.Clear();
        _active = null;
        _showingUnits = false;
        MenuScroll.Height = double.NaN;
        BuildMenuCore();
    }

    /// <summary>
    /// 两页一次建好（主列表 + 取值动作单元）：换页只切可见性——不关弹层、不改高度。
    /// 搜索时跨页匹配，所以在动作单元页也能输入变量名。
    /// </summary>
    private void BuildMenuCore()
    {
        AddVariables();

        if (HasSelectionPickers)
        {
            AddHeader("选择", 0);
            AddSelectionPickers();
        }

        AddChoices();
        AddSuggestions();

        if (_inline != null && Pickers.HasFlag(PickerKind.inline))
        {
            var inlineSpec = StepRegistry.Default.TryGet(_inline.Unit, out var inlineUnit)
                ? inlineUnit.Spec
                : null;

            AddHeader("当前取值", 0);
            AddItem("▷", "改参数…",
                inlineSpec is { Inputs.Count: > 0 } ? "调整这个取值动作单元的参数" : "这个取值不需要参数",
                "取值 改参数 inline", EditInline);
            AddItem("✎", "切回文本", "清掉取值动作单元，改成直接写字",
                "取值 切回文本 inline 文本", LeaveInline);
        }

        if (Pickers.HasFlag(PickerKind.inline))
        {
            AddHeader("取值", 0);
            AddItem("▷", "取值动作单元…", "运行到这里时现场取一个值",
                "取值动作单元 inline 取值", () => ShowMenuPage(true), page: 0, nav: true);

            AddHeader("取值动作单元", 1);
            AddUnitItems();
        }

        if (Pickers.HasFlag(PickerKind.clear))
        {
            AddSeparator(0);
            AddItem("⌫", "清空", "", "清空 clear", ClearValue);
        }
    }

    /// <summary>同一弹层里换页（导航项）：保持打开，清掉搜索词并回到顶部。</summary>
    private void ShowMenuPage(bool units)
    {
        _showingUnits = units;
        SearchBox.Text = "";
        ApplyFilter("");
        MenuScroll.ScrollToTop();
        SearchBox.Focus();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowMenuPage(false);

    /// <summary>
    /// 高度按「所有行都显示」来定：两页共用一个高度，换页不会忽大忽小；
    /// 行少时保持紧凑，多到放不下就让列表滚。
    /// </summary>
    private void FitMenuHeight()
    {
        MenuScroll.Height = double.NaN;
        foreach (var row in _rows) row.Host.Visibility = Visibility.Visible;
        MenuList.UpdateLayout();

        // 弹层刚开、还没量出来：保持自适应（下一次打开会量到）。
        if (MenuList.ActualHeight <= 0) return;
        MenuScroll.Height = Math.Clamp(MenuList.ActualHeight + 2, 120, 330);
    }

    private void AddVariables()
    {
        if (!Pickers.HasFlag(PickerKind.variable)) return;

        var infos = VariableInfos();
        if (infos.Count == 0) return;

        AddHeader("变量", 0);
        foreach (var info in infos)
        {
            string detail = ValueKindText.Of(info.Kind);
            if (!string.IsNullOrWhiteSpace(info.Origin)) detail += " · " + info.Origin;
            AddItem("·", info.Name, detail,
                info.Name + " " + detail,
                () => InsertVariable(info.Name));
        }
    }

    private bool HasSelectionPickers =>
        (Pickers & (PickerKind.file | PickerKind.folder | PickerKind.image | PickerKind.region
                    | PickerKind.window | PickerKind.windowClass | PickerKind.process
                    | PickerKind.point | PickerKind.color | PickerKind.keys
                    | PickerKind.control)) != PickerKind.none;

    private void AddSelectionPickers()
    {
        if (Pickers.HasFlag(PickerKind.window))
            AddItem("▤", "选择窗口…", "填窗口标题（按包含匹配）", "窗口 window 标题", PickWindowTitle);

        if (Pickers.HasFlag(PickerKind.windowClass))
            AddItem("▤", "选择窗口…", "填窗口类名（精确匹配）", "窗口 window 类名", PickWindowClass);

        if (Pickers.HasFlag(PickerKind.process))
            AddItem("▥", "选择进程…", "从正在运行的程序里挑", "进程 process", PickProcessName);

        if (Pickers.HasFlag(PickerKind.file))
            AddItem("▦", "文件…", "", "文件 file 浏览", PickFile);

        if (Pickers.HasFlag(PickerKind.folder))
            AddItem("▦", "文件夹…", "", "文件夹 folder 目录", PickFolder);

        if (Pickers.HasFlag(PickerKind.image))
        {
            AddItem("▧", "框选截图…", "截下来的就是模板图", "截图 框选 图片 image", CaptureImage);
            AddItem("▧", "换图…", "从磁盘选一张", "换图 图片 image 文件", PickImageFile);
            AddItem("▧", "预览", "", "预览 图片 preview", PreviewImage);
        }

        if (Pickers.HasFlag(PickerKind.region))
            AddItem("▣", "屏幕区域…", "拖一块，填 x,y,w,h", "区域 region 框选", PickRegion);

        if (Pickers.HasFlag(PickerKind.point))
            AddItem("✛", "屏幕取点…", "单击一次，填好 X / Y", "坐标 取点 point", PickPoint);

        if (Pickers.HasFlag(PickerKind.color))
            AddItem("◐", "屏幕取色…", "填 #RRGGBB", "颜色 取色 color", PickColor);

        if (Pickers.HasFlag(PickerKind.keys))
            AddItem("⌨", "录制按键…", "按一下组合键，追加一行", "按键 录制 keys", RecordKey);

        if (Pickers.HasFlag(PickerKind.control))
            AddItem("▩", "选控件…", "在屏幕上点一个控件，填名称 / AutomationId / 类型 / 窗口类名",
                "控件 选择器 uia 控件树 automationId", PickControl);
    }

    /// <summary>
    /// 「从列表选…」：按键 / 宏 / 子动作这类候选随用户数据变化，收进 ▾ 之后就不再在行内
    /// 摆一个下拉；副标题写分组（键盘分区 / 宏时长 / 子动作来源）。
    /// </summary>
    private void AddChoices()
    {
        if (!Pickers.HasFlag(PickerKind.choices) || ChoiceSource == null) return;

        List<StepChoice> choices;
        try { choices = ChoiceSource().ToList(); }
        catch { return; }
        if (choices.Count == 0) return;

        AddHeader("从列表选", 0);
        foreach (var choice in choices)
        {
            string label = choice.Label.Length > 0 ? choice.Label : choice.Value;
            AddItem("·", label, choice.Group,
                label + " " + choice.Group + " " + choice.Value,
                () => SetLiteral(choice.Value));
        }
    }

    private void AddSuggestions()
    {
        if (Pickers.HasFlag(PickerKind.noteCategory))
        {
            AddHeader("速记分类", 0);
            var dirs = NoteCategories();
            if (dirs.Count == 0)
                AddItem("·", "（还没有分类）", "在速记窗口里新建分类后会出现在这里",
                    "速记 分类 note", () => { });
            foreach (string name in dirs)
                AddItem("·", name, "", "速记 分类 " + name, () => SetLiteral(name));
        }

        if (Pickers.HasFlag(PickerKind.timeFormat))
        {
            AddHeader("常用格式", 0);
            foreach (string format in new[]
                     {
                         "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "HH:mm:ss",
                         "yyyyMMdd_HHmmss", "M月d日 HH:mm", "yyyy年M月d日",
                     })
                AddItem("·", format, "", "时间 格式 " + format, () => SetLiteral(format));
        }

        if (Pickers.HasFlag(PickerKind.fileFilter))
        {
            AddHeader("常用筛选", 0);
            foreach (var (label, filter) in new[]
                     {
                         ("所有文件", "所有文件|*.*"),
                         ("图片", "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif"),
                         ("文本 / 日志", "文本|*.txt;*.md;*.log;*.json"),
                         ("文档", "文档|*.docx;*.pdf;*.txt;*.md"),
                         ("可执行 / 脚本", "程序|*.exe;*.bat;*.cmd;*.ps1;*.ahk"),
                     })
                AddItem("·", label, filter, "筛选 文件类型 " + label + " " + filter,
                    () => SetLiteral(filter));
        }

        if (Pickers.HasFlag(PickerKind.separator))
        {
            // 存的是 \n / \t 转义写法（不是看不见的真字符）：单行框里能看见自己填了什么，
            // 「文本处理」「列表操作」读分隔符时会还原成真正的换行 / 制表符（TextEscapes）。
            AddHeader("常用分隔符", 0);
            foreach (var (label, value) in new (string, string)[]
                     {
                         ("逗号 ,", ","), ("分号 ;", ";"), ("竖线 |", "|"),
                         ("空格", " "),
                         ("换行 \\n", "\\n"),
                         ("Windows 换行 \\r\\n", "\\r\\n"),
                         ("Tab \\t", "\\t"),
                     })
                AddItem("·", label, "", "分隔符 separator " + label, () => SetLiteral(value));
        }
    }

    /// <summary>取值动作单元（第二页）：运行时现场取值的纯动作单元。</summary>
    private void AddUnitItems()
    {
        var units = StepRegistry.Default.Units
            .Where(u => u.Spec.Kind == UnitKind.pure)
            .Select(u => u.Spec)
            .OrderBy(s => UnitCategories.Rank(s.Category))
            .ThenBy(s => s.Category, StringComparer.CurrentCulture)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture);

        foreach (var spec in units)
            AddItem("▷", spec.Name, spec.Category, spec.Name + " " + spec.Category + " " + spec.Key,
                () =>
                {
                    _inline = Step.Of(spec.Key);
                    UpdatePanels();
                    Changed?.Invoke();
                },
                page: 1);
    }

}
