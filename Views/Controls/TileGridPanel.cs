using System;
using System.Windows;
using System.Windows.Controls;

namespace LTools.Views.Controls;

/// <summary>
/// 磁贴网格：按可用宽度均分列宽，列数由 <see cref="MinTileWidth"/> 决定
/// （等价 CSS 的 <c>grid-template-columns: repeat(auto-fill, minmax(72px, 1fr))</c>）。
/// 用于动作面板的全局区 / 上下文区磁贴。
/// </summary>
public sealed class TileGridPanel : Panel
{
    /// <summary>磁贴最小宽度（DIP）。列数 = floor((宽 + GapX) / (MinTileWidth + GapX))。</summary>
    public double MinTileWidth { get; set; } = 72;

    /// <summary>横向间距。</summary>
    public double GapX { get; set; } = 4;

    /// <summary>纵向间距。</summary>
    public double GapY { get; set; } = 2;

    /// <summary>磁贴高度。</summary>
    public double TileHeight { get; set; } = 72;

    private int Columns(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return 1;
        double step = Math.Max(24, MinTileWidth) + GapX;
        return Math.Max(1, (int)Math.Floor((width + GapX) / step));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int cols = Columns(availableSize.Width);
        double width = double.IsInfinity(availableSize.Width)
            ? MinTileWidth
            : Math.Max(0, (availableSize.Width - GapX * (cols - 1)) / cols);

        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(width, TileHeight));

        int rows = (InternalChildren.Count + cols - 1) / cols;
        double height = rows * TileHeight + Math.Max(0, rows - 1) * GapY;
        return new Size(
            double.IsInfinity(availableSize.Width) ? cols * MinTileWidth : availableSize.Width,
            height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = Columns(finalSize.Width);
        double width = Math.Max(0, (finalSize.Width - GapX * (cols - 1)) / cols);

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int r = i / cols;
            int c = i % cols;
            InternalChildren[i].Arrange(new Rect(
                c * (width + GapX),
                r * (TileHeight + GapY),
                width,
                TileHeight));
        }
        return finalSize;
    }
}
