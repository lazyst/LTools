using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>
/// 采集历史里落盘的一块选区（照上游 <c>PersistedSelection.region</c>）：加 / 减 + 矩形或点列，
/// 坐标是**虚拟桌面物理像素**（与截图 / 找图 / 鼠标注入同一套）。
/// </summary>
/// <remarks>
/// 只存事实：<see cref="SelectionX"/> 那一套只记外接框，多块 / 自由 / 折线靠这份记录才还原得出来。
/// 纯数据 + 两处换算（写盘时图像像素 → 屏幕物理像素、重开覆盖层时屏幕物理像素 → 窗口 DIP），
/// <c>--selftest</c> 直接断言。
/// </remarks>
internal sealed class CaptureRegionDto
{
    /// <summary><c>add</c> / <c>subtract</c>（见 <see cref="RegionMode"/>）。</summary>
    public string Mode { get; set; } = "add";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    /// <summary>点列（<c>x, y</c> 交替）；矩形为空。自由绘制是平滑曲线（<see cref="Smooth"/>）。</summary>
    public List<double>? Points { get; set; }

    /// <summary>点列是不是平滑曲线（自由绘制）。</summary>
    public bool Smooth { get; set; }

    /// <summary>一块选区（底图图像像素，0,0 = 底图左上角）→ 落盘形状（虚拟桌面物理像素）。</summary>
    public static CaptureRegionDto Of(SelectionRegion region, int offsetX, int offsetY)
    {
        var dto = new CaptureRegionDto
        {
            Mode = region.Mode == RegionMode.Subtract ? "subtract" : "add",
            Smooth = region.Smooth,
            X = region.Bounds.X + offsetX,
            Y = region.Bounds.Y + offsetY,
            Width = region.Bounds.Width,
            Height = region.Bounds.Height,
        };

        if (region.IsPolygon)
        {
            var flat = new List<double>(region.Points!.Length * 2);
            foreach (var point in region.Points)
            {
                flat.Add(point.X + offsetX);
                flat.Add(point.Y + offsetY);
            }
            dto.Points = flat;
        }

        return dto;
    }

    /// <summary>整组落盘形状（读历史时用）。</summary>
    public static List<CaptureRegionDto> Of(IEnumerable<SelectionRegion> regions, int offsetX, int offsetY) =>
        regions.Select(region => Of(region, offsetX, offsetY)).ToList();

    /// <summary>落盘形状 → 覆盖层窗口 DIP（<paramref name="originX"/> / <paramref name="originY"/> = 底图左上角，物理像素）。</summary>
    public SelectionRegion ToDip(int originX, int originY, double scale)
    {
        double s = scale > 0 ? scale : 1;
        var mode = (Mode ?? "").Trim().ToLowerInvariant() == "subtract"
            ? RegionMode.Subtract
            : RegionMode.Add;

        if (Points is { Count: >= 6 })
        {
            var points = new Point[Points.Count / 2];
            for (int i = 0; i < points.Length; i++)
                points[i] = new Point((Points[i * 2] - originX) / s, (Points[i * 2 + 1] - originY) / s);

            return SelectionRegion.FromPoints(mode, points, Smooth);
        }

        return SelectionRegion.FromRect(mode,
            new Rect((X - originX) / s, (Y - originY) / s, Width / s, Height / s));
    }

    /// <summary>落盘形状 → 底图图像像素（0,0 = 底图左上角；整屏预览画选框用）。</summary>
    public SelectionRegion ToImage(int originX, int originY) => ToDip(originX, originY, 1);

    /// <summary>
    /// 这条记录当初的每一块选区（覆盖层窗口 DIP）：优先用落盘的 <see cref="CaptureEntry.Regions"/>；
    /// 单块矩形的记录只存了外接框（<see cref="CaptureEntry.SelectionX"/> 那一套），就按它还原成一块加区；
    /// 连外接框都没有（老记录）返回空表，调用方自己决定是不是框满整张。
    /// </summary>
    public static List<SelectionRegion> RegionsOf(CaptureEntry entry, double scale)
    {
        var regions = new List<SelectionRegion>(Math.Max(1, entry.Regions.Count));

        if (entry.Regions.Count > 0)
        {
            foreach (var dto in entry.Regions)
                regions.Add(dto.ToDip(entry.DisplayX, entry.DisplayY, scale));
            return regions;
        }

        if (entry.SelectionW >= 2 && entry.SelectionH >= 2)
        {
            double s = scale > 0 ? scale : 1;
            regions.Add(SelectionRegion.FromRect(RegionMode.Add, new Rect(
                (entry.SelectionX - entry.DisplayX) / s,
                (entry.SelectionY - entry.DisplayY) / s,
                entry.SelectionW / s,
                entry.SelectionH / s)));
        }

        return regions;
    }
}
