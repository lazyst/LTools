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
/// 新切到「取值动作单元」时默认选中的那一个：挑最常用、参数最少的，
/// 别让新用户一进来就撞上要填进程名、说明又长的动作单元。
/// </summary>
public partial class ValueField
{
    /// <summary>
    /// 新切到「取值动作单元」时默认选中的动作单元：挑最常用、参数最少的一个，
    /// 别让新用户一进来就撞上「进程」这种要填进程名、说明又长的动作单元。
    /// </summary>
    private static readonly string[] PreferredDefaults =
    {
        "text.selection", "data.time", "input.cursor", "screen.findImage",
    };

    private static string FirstPureUnit()
    {
        var keys = StepRegistry.Default.Units
            .Where(u => u.Spec.Kind == UnitKind.pure)
            .Select(u => u.Spec.Key)
            .ToList();

        foreach (string preferred in PreferredDefaults)
            if (keys.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                return preferred;

        return keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
               ?? "text.process";
    }

}
