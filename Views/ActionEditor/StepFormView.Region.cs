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
/// 屏幕框选 / 取点之后回填：区域写进固定区域参数并把配套枚举切过去，
/// 坐标一次填好同组的 X / Y。
/// </summary>
public partial class StepFormView
{
    /// <summary>
    /// 框选完一块区域：先写「固定区域」文本，再把配套枚举（<c>region</c> / <c>mode</c>）切到 <c>rect</c>。
    /// 顺序不能反——枚举一切换就会同步参数并把隐藏行丢掉，那时文本还没写进去。
    /// </summary>
    private void ApplyPickedRegion(Int32Rect area)
    {
        string rect = string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2},{3}", area.X, area.Y, area.Width, area.Height);

        var rectSpec = _spec.Inputs.FirstOrDefault(i =>
            string.Equals(i.Key, "rect", StringComparison.OrdinalIgnoreCase));
        if (rectSpec == null) return;   // 这个动作单元没有固定区域参数（如「文字识别」）

        if (_rectSetters.TryGetValue(rectSpec.Key, out var setRect)) setRect(rect);

        if (StepSummary.TryGetOptionFor(rectSpec, _spec, out string key, out string value)
            || StepSummary.TryGetVisibleRule(rectSpec, out key, out value))
        {
            if (_enumSetters.TryGetValue(key, out var setEnum))
                setEnum(value);
        }

        OnEdited();
    }

    /// <summary>
    /// 屏幕上取了一次点：一次填好同组的 <c>x</c> / <c>y</c>（没有对应参数时只填当前框），
    /// 用户不必为坐标点两次、也不会点出两个不一样的位置。
    /// </summary>
    private void ApplyPickedPoint(InputSpec source, int x, int y)
    {
        bool wrote = false;
        foreach (var (key, value) in new[] { ("x", x), ("y", y) })
        {
            var spec = _spec.Inputs.FirstOrDefault(i =>
                string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
            if (spec == null || !_fieldSetters.TryGetValue(spec.Key, out var set)) continue;
            set(value.ToString(CultureInfo.InvariantCulture));
            wrote = true;
        }

        if (!wrote && _fieldSetters.TryGetValue(source.Key, out var self))
            self(x.ToString(CultureInfo.InvariantCulture));

        OnEdited();
    }

}
