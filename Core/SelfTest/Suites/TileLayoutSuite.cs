using System.Collections.Generic;
using System.Windows;
using LTools.Views.Controls;

namespace LTools.Core.SelfTest;

/// <summary>
/// 磁贴网格的落位几何（<see cref="TileLayout.InsertIndex"/>）：拖动排序与「从资源管理器拖文件进面板」
/// 共用它，所以这里是纯函数的断言——真窗口里的鼠标轨迹靠面板冒烟。
/// </summary>
internal static class TileLayoutSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("tileLayout", "空网格 → 0", Empty));
        cases.Add(new("tileLayout", "单行：按各磁贴的 X 中点插入", SingleRow));
        cases.Add(new("tileLayout", "多行：按 Y 找行，行内仍按 X", TwoRows));
        cases.Add(new("tileLayout", "行上方 → 0；两行之间归下一行；最后一行下面 → 末尾", RowsEdges));
    }

    // 一行 4 个磁贴：x = 0/72/144/216，宽 68（间隔 4），高 56。
    private static readonly Rect[] Row1 =
    {
        new(0, 0, 68, 56), new(72, 0, 68, 56), new(144, 0, 68, 56), new(216, 0, 68, 56),
    };

    private static void Empty()
    {
        Check.Equal(0, TileLayout.InsertIndex(new List<Rect>(), new Point(500, 500)), "没有磁贴时插到 0");
    }

    private static void SingleRow()
    {
        Check.Equal(0, TileLayout.InsertIndex(Row1, new Point(0, 28)), "第一个磁贴左半边 → 0");
        Check.Equal(1, TileLayout.InsertIndex(Row1, new Point(40, 28)), "越过第一个的中点 → 1");
        Check.Equal(1, TileLayout.InsertIndex(Row1, new Point(72, 28)), "第二个左缘 → 1（它自己前面）");
        Check.Equal(2, TileLayout.InsertIndex(Row1, new Point(110, 28)), "第二个右半边 → 2");
        Check.Equal(4, TileLayout.InsertIndex(Row1, new Point(600, 28)), "最后一个右边 → 末尾");
        Check.Equal(0, TileLayout.InsertIndex(Row1, new Point(0, -40)), "行上方 → 0");
        Check.Equal(4, TileLayout.InsertIndex(Row1, new Point(0, 400)), "行下方 → 末尾");
    }

    private static void TwoRows()
    {
        var tiles = new List<Rect>(Row1)
        {
            new(0, 60, 68, 56), new(72, 60, 68, 56), new(144, 60, 68, 56), new(216, 60, 68, 56),
        };

        Check.Equal(0, TileLayout.InsertIndex(tiles, new Point(10, 10)), "第一行左 → 0");
        Check.Equal(3, TileLayout.InsertIndex(tiles, new Point(200, 10)), "第一行右 → 3");
        Check.Equal(4, TileLayout.InsertIndex(tiles, new Point(10, 80)), "第二行左 → 4");
        Check.Equal(7, TileLayout.InsertIndex(tiles, new Point(200, 80)), "第二行右 → 7");
        Check.Equal(8, TileLayout.InsertIndex(tiles, new Point(10, 500)), "两行下面 → 末尾");
    }

    private static void RowsEdges()
    {
        var tiles = new List<Rect>(Row1)
        {
            new(0, 60, 68, 56), new(72, 60, 68, 56),
        };

        // 两行之间的缝（56..60）：归下面那一行（离鼠标最近），再按 X 决定插在谁前面。
        Check.Equal(5, TileLayout.InsertIndex(tiles, new Point(80, 58)), "两行之间归下一行");
        Check.Equal(6, TileLayout.InsertIndex(tiles, new Point(600, 58)), "缝里靠右 → 下一行末尾");
        Check.Equal(6, TileLayout.InsertIndex(tiles, new Point(0, 200)), "最后一行下面 → 末尾");
    }
}
