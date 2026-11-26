using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Link;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Features.Floating;
using LTools.Features.Panel;

namespace LTools.Views.Controls;

/// <summary>PanelManageView 的「辅助」部分。</summary>
public partial class PanelManageView : UserControl
{
    // ============ 辅助 ============

    /// <summary>拖动实例只建一次：构造时窗口还没挂上，首次 Loaded / 首次建磁贴时补建。</summary>
    private void EnsureTileDrag()
    {
        if (_tileDrag != null) return;
        var win = Window.GetWindow(this);
        if (win == null) return;   // 还没挂到窗口上：等下一次 Reload
        _tileDrag = new TileDragSort(win, GhostLayer, DetailHost)
        {
            GhostBody = tref => tref.ActionId == null
                ? null
                : TileContent(tref.ActionId),
            Finished = RefreshAll,
        };
    }

    /// <summary>磁贴拖动：与动作面板同一套（快照幽灵 + 虚线占位 + 实时让位 + Esc 取消）。</summary>
    private void AttachTileDrag(Button btn)
    {
        EnsureTileDrag();
        if (_tileDrag == null) return;   // 还没挂到窗口上：等下一次 Reload
        _tileDrag.Attach(btn);
    }

    private void RefreshAll()
    {
        BuildSceneList();
        RenderDetail();
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = (double)FindResource("FontSizeHint"),
        Foreground = (Brush)FindResource("HintTextBrush"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 8),
    };

    private void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
    }
}
