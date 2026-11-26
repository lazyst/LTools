using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.AutoFilter;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Views.Controls;
using ShapePath = System.Windows.Shapes.Path;

namespace LTools.Views.CapturePro;

/// <summary>CaptureToolbar 的「多选「多个值」（上游 Mixed 位）」部分。</summary>
public partial class CaptureToolbar
{
    // ============ 多选：「多个值」（上游 screenshot 工具面板的 Mixed 位） ============

    private const string MixedText = "多个值";

    /// <summary>当前被标成「多个值」的那几个控件（<see cref="RefreshValues"/> 每次重算前会清掉）。</summary>
    private readonly List<FrameworkElement> _mixedParts = new();

    /// <summary>标记时各控件原来的 ToolTip（清掉混合态要还回去，别把参数自己的说明吃掉）。</summary>
    private readonly Dictionary<FrameworkElement, object?> _mixedTips = new();

    /// <summary>冒烟 / 自检看「哪几项现在是混合态」。</summary>
    internal IReadOnlyList<FrameworkElement> MixedParts => _mixedParts;

    /// <summary>
    /// 参数行里能表达值的那些控件（顺序稳定，逐条比较靠它对上号）：
    /// 文本块（读数 / 字体名）、按钮（下拉当前值）、开关（勾选态）、输入框（文本）。
    /// </summary>
    private List<FrameworkElement> ProbeParts()
    {
        var parts = new List<FrameworkElement>();
        foreach (var host in _paramHosts.Values) Collect(host, parts);
        return parts;

        static void Collect(DependencyObject node, List<FrameworkElement> into)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement element && IsProbe(element)) into.Add(element);
                Collect(child, into);
            }
        }

        static bool IsProbe(FrameworkElement element) =>
            element is TextBlock or TextBox or ToggleButton or Button;
    }

    /// <summary>一个控件现在显示的值（只比这三类，够区分「这一条和那一条不一样」）。</summary>
    private static string ProbeValue(FrameworkElement part) => part switch
    {
        ToggleButton toggle => toggle.IsChecked == true ? "on" : "off",
        Button button => button.Content as string ?? string.Empty,
        TextBox box => box.Text,
        TextBlock text => text.Text,
        _ => string.Empty,
    };

    /// <summary>把上一次标的「多个值」还原（刷新前先清，值由各自的 refresher 重新写回）。</summary>
    private void ClearMixedMarks()
    {
        if (_mixedParts.Count == 0) return;
        foreach (var part in _mixedParts)
        {
            if (part is TextBox box) TextBoxPlaceholder.SetText(box, string.Empty);
            if (_mixedTips.TryGetValue(part, out object? tip)) part.ToolTip = tip;
        }

        _mixedParts.Clear();
        _mixedTips.Clear();
    }

    /// <summary>
    /// 逐条比较：把显示样式临时换成同族其它成员，重跑一遍 refresher，哪个控件的值变了
    /// 就说明这一项「各条不一样」，标成「多个值」。比较完复原成显示样式的值。
    /// </summary>
    private void MarkMixedValues()
    {
        if (_stylePeers is not { Count: > 1 } peers || _style == null) return;

        var parts = ProbeParts();
        if (parts.Count == 0) return;

        var baseline = parts.Select(ProbeValue).ToArray();
        var differs = new bool[parts.Count];
        ToolStyle display = _style;

        try
        {
            for (int i = 1; i < peers.Count; i++)
            {
                _styleOverride = peers[i];
                foreach (var refresh in _refreshers) refresh();

                for (int p = 0; p < parts.Count; p++)
                    if (!differs[p] && ProbeValue(parts[p]) != baseline[p]) differs[p] = true;
            }
        }
        finally
        {
            _styleOverride = display;
            foreach (var refresh in _refreshers) refresh();
        }

        _syncing = true;
        for (int p = 0; p < parts.Count; p++)
            if (differs[p]) MarkMixed(parts[p]);
        _syncing = false;
    }

    /// <summary>把这一项显示成「多个值」：选中态清空、读数换成字、输入框留空 + 占位。</summary>
    private void MarkMixed(FrameworkElement part)
    {
        _mixedParts.Add(part);
        _mixedTips[part] = part.ToolTip;

        switch (part)
        {
            case TextBox box:
                box.Text = string.Empty;
                TextBoxPlaceholder.SetText(box, MixedText);
                break;
            case ToggleButton toggle:
                toggle.IsChecked = false;
                break;
            case Button button:
                button.Content = MixedText;
                break;
            case TextBlock text:
                text.Text = MixedText;
                break;
        }

        part.ToolTip = "多个值：选中的这几条这一项不一样，改了会一起写成同一个值";
    }

    /// <summary>按步长取整（步长 &lt; 1 时保留小数，别被 <c>Math.Round</c> 吃掉 0.1）。</summary>
    private static double RoundTo(double value, double step) =>
        step >= 1 ? Math.Round(value) : Math.Round(Math.Round(value / step) * step, 3);

}
