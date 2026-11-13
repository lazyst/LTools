using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Views;

namespace LTools.Views.Controls;

/// <summary>
/// 差异清单（<c>+ / − / ~</c> + 缩进）。草稿采纳前与历史版本恢复前共用这一份，
/// 别在两个对话框里各画一遍。
/// </summary>
public partial class DiffList : UserControl
{
    public DiffList()
    {
        InitializeComponent();
    }

    /// <summary>填内容；没有差异时给一行灰字，而不是留一块空白让人以为出错了。</summary>
    internal void Fill(IReadOnlyList<DiffLine> lines)
    {
        var rows = new List<DiffRow>();
        if (lines.Count == 0)
        {
            rows.Add(new DiffRow
            {
                Glyph = "",
                Text = "（没有差异）",
                Brush = (Brush)FindResource("HintTextBrush"),
                Indent = new Thickness(0),
            });
        }
        else
        {
            foreach (DiffLine line in lines)
            {
                rows.Add(new DiffRow
                {
                    Glyph = line.Kind switch
                    {
                        DiffKind.added => "+",
                        DiffKind.removed => "−",
                        _ => "~",
                    },
                    Text = line.Text,
                    Brush = (Brush)FindResource(line.Kind switch
                    {
                        DiffKind.added => "SuccessBrush",
                        DiffKind.removed => "DangerBrush",
                        _ => "WarningBrush",
                    }),
                    Indent = new Thickness(line.Depth * 14, 0, 0, 0),
                });
            }
        }

        List.ItemsSource = rows;
    }
}

/// <summary>差异清单的一行（绑定用，颜色 / 缩进在 <see cref="DiffList.Fill"/> 里算好）。</summary>
public sealed class DiffRow
{
    public string Glyph { get; init; } = "";

    public string Text { get; init; } = "";

    public Brush Brush { get; init; } = Brushes.Transparent;

    public Thickness Indent { get; init; }
}
