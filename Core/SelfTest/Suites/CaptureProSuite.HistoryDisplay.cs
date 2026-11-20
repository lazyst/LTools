using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 采集历史的「整屏底图 + 选区还原」（照上游 <c>CaptureHistoryDraft</c> 的 displays / selection）：
/// 落盘形状与窗口 DIP 的互换、每块屏幕预览从整屏图上裁的几何。
/// </summary>
/// <remarks>
/// 纯换算 + 纯绘制：真窗口 / 真鼠标在 <c>capture-overlay-smoke</c> 的 <c>historydisplay</c> 段。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void HistoryDisplayCases()
    {
        // ============ ① 一块矩形选区：屏幕物理像素 ↔ 窗口 DIP ============
        var rect = SelectionRegion.FromRect(RegionMode.Add, new Rect(10, 20, 100, 50));
        var dto = CaptureRegionDto.Of(rect, 1000, 500);

        Check.Equal("add", dto.Mode, "矩形：加区");
        Check.Equal(1010.0, dto.X, "矩形：X 加上底图原点");
        Check.Equal(520.0, dto.Y, "矩形：Y 加上底图原点");
        Check.Equal(100.0, dto.Width, "矩形：宽原样");
        Check.True(dto.Points == null, "矩形不写点列");

        var back = dto.ToDip(1000, 500, 1);
        Check.Equal(RegionMode.Add, back.Mode, "还原回加区");
        Check.Equal(new Rect(10, 20, 100, 50), back.Box, "还原回同一块矩形");
        Check.False(back.IsPolygon, "还原出来还是矩形");

        Check.Equal(new Rect(5, 10, 50, 25), dto.ToDip(1000, 500, 2).Box,
            "缩放过（2 倍）时坐标按比例除回去");
        Check.Equal(new Rect(10, 20, 100, 50), dto.ToImage(1000, 500).Box, "ToImage = 1:1 的 ToDip");

        // ============ ② 点列（减区 / 自由绘制）：点与平滑都保住 ============
        var points = new[]
        {
            new Point(0, 0), new Point(30, 0), new Point(30, 20), new Point(0, 20),
        };
        var polygon = SelectionRegion.FromPoints(RegionMode.Subtract, points, smooth: true);
        var polyDto = CaptureRegionDto.Of(polygon, -200, 100);

        Check.Equal("subtract", polyDto.Mode, "点列：减区");
        Check.True(polyDto.Smooth, "自由绘制的平滑标记");
        Check.Equal(8, polyDto.Points?.Count ?? 0, "点列按 x / y 平铺");
        Check.Equal(-200.0, polyDto.Points![0], "点列 X 加底图原点");
        Check.Equal(100.0, polyDto.Points[1], "点列 Y 加底图原点");

        var polyBack = polyDto.ToDip(-200, 100, 1);
        Check.Equal(RegionMode.Subtract, polyBack.Mode, "点列还原成减区");
        Check.True(polyBack.IsPolygon, "点列还原出来还是点列");
        Check.True(polyBack.Smooth, "平滑标记也还原");
        Check.Equal(4, polyBack.Points?.Length ?? 0, "点数不变");
        Check.Equal(new Point(30, 20), polyBack.Points![2], "第三个点原样（30, 20）");

        // 一组：顺序就是叠放顺序（后画的在上），别在还原时被打乱
        var group = CaptureRegionDto.Of(new[] { rect, polygon }, 0, 0);
        Check.Equal(2, group.Count, "整组落盘");
        Check.Equal("add,subtract", group[0].Mode + "," + group[1].Mode, "顺序保持");

        // RegionsOf：多块照落盘的来；只存了外接框的记录还原成一块矩形；都没有返回空表
        var multi = new CaptureEntry { Display = "displays/x.png", DisplayX = -100, DisplayY = 20,
            DisplayW = 400, DisplayH = 300 };
        multi.Regions.Add(CaptureRegionDto.Of(rect, -100, 20));
        var fromMulti = CaptureRegionDto.RegionsOf(multi, 1);
        Check.Equal(1, fromMulti.Count, "有多块记录时用落盘的那份");
        Check.Equal(new Rect(10, 20, 100, 50), fromMulti[0].Box, "多块记录按落盘形状还原");

        var boxOnly = new CaptureEntry { Display = "displays/y.png", DisplayX = -100, DisplayY = 20,
            DisplayW = 400, DisplayH = 300, SelectionX = -60, SelectionY = 60,
            SelectionW = 150, SelectionH = 90 };
        var fromBox = CaptureRegionDto.RegionsOf(boxOnly, 2);
        Check.Equal(1, fromBox.Count, "只有外接框的记录也还原出一块");
        Check.Equal(new Rect(20, 20, 75, 45), fromBox[0].Box, "外接框按窗口缩放换算回 DIP");

        var nothing = new CaptureEntry { DisplayX = 0, DisplayY = 0, DisplayW = 10, DisplayH = 10 };
        Check.Equal(0, CaptureRegionDto.RegionsOf(nothing, 1).Count, "连外接框都没有时返回空表");

        // ============ ③ 条目上的整屏字段 ============
        var entry = new CaptureEntry();
        Check.False(entry.HasDisplay, "新记录没有整屏底图");
        Check.Equal(Int32Rect.Empty, entry.DisplayRect, "没有底图时矩形为空");

        entry.Display = CaptureCatalog.DisplayRelative("abc");
        entry.DisplayX = -100;
        entry.DisplayY = 20;
        entry.DisplayW = 1920;
        entry.DisplayH = 1080;
        Check.Equal("displays/abc.png", entry.Display, "落盘路径固定 shapes");
        Check.True(entry.HasDisplay, "三个尺寸齐了才算有底图");
        Check.Equal(new Int32Rect(-100, 20, 1920, 1080), entry.DisplayRect, "整屏矩形的屏幕坐标（可为负）");
        Check.Equal(System.IO.Path.Combine(CaptureCatalog.Root, "displays", "abc.png"),
            CaptureCatalog.DisplayFilePath(entry), "绝对路径拼在 captures/ 下");

        entry.DisplayW = 0;
        Check.False(entry.HasDisplay, "缺一个尺寸就不算有底图");

        // ============ ④ 每块屏幕的预览：按记录下来的屏幕矩形从整屏图上裁 ============
        static WriteableBitmap Ramp(int width, int height)
        {
            var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            var bytes = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bytes[i] = (byte)(x & 0xFF);        // B = x
                bytes[i + 1] = (byte)(y & 0xFF);    // G = y
                bytes[i + 2] = 0x40;                // R 固定，方便看出「这是同一张底图裁出来的」
                bytes[i + 3] = 0xFF;
            }

            bitmap.WritePixels(new Int32Rect(0, 0, width, height), bytes, width * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }

        static byte[] PixelsOf(BitmapSource image)
        {
            var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(bytes, image.PixelWidth * 4, 0);
            return bytes;
        }

        var display = Ramp(200, 100);

        // 底图原点是 (-50, -20)：屏幕 (0, 0) 落在底图的 (50, 20)
        var crop = HistorySlides.Crop(display, new Int32Rect(0, 0, 20, 10), -50, -20, 1);
        Check.True(crop != null, "屏幕矩形落在底图里就裁得出来");
        Check.Equal(20, crop!.PixelWidth, "裁出来的宽 = 屏幕宽");
        Check.Equal(10, crop.PixelHeight, "裁出来的高 = 屏幕高");

        var cropPixels = PixelsOf(crop);
        Check.Equal((byte)50, cropPixels[0], "裁出来的第一列是底图的 x = 50");
        Check.Equal((byte)20, cropPixels[1], "裁出来的第一行是底图的 y = 20");

        // 底图缩过（显示 100 像素 = 屏幕 200）：屏幕矩形跟着缩一半
        var scaled = HistorySlides.Crop(Ramp(100, 100), new Int32Rect(0, 0, 40, 40), 0, 0, 0.5);
        Check.Equal(20, scaled!.PixelWidth, "缩过的底图按比例裁（40 × 0.5）");
        Check.Equal(20, scaled.PixelHeight, "高也按比例裁");

        // 越界：裁到边缘就停，不抛（换机 / 换缩放后矩形可能超出那张底图）
        var clipped = HistorySlides.Crop(display, new Int32Rect(150, 60, 100, 100), 0, 0, 1);
        Check.Equal(50, clipped!.PixelWidth, "右边越界：裁到 200 - 150 = 50");
        Check.Equal(40, clipped.PixelHeight, "下边越界：裁到 100 - 60 = 40");

        // 空矩形（老记录没有屏幕信息）→ null，让上层退回「整屏一张」
        Check.True(HistorySlides.Crop(display, new Int32Rect(0, 0, 0, 0), 0, 0, 1) == null,
            "没有尺寸的屏幕不裁");

        // 角标文案：单屏 = 「整个屏幕」，多屏 = 「屏幕 N」
        Check.Equal("成品图", HistorySlides.ResultLabel, "第一张的角标是「成品图」");
        Check.Equal(-1920, CaptureScreenDto.Of(new Int32Rect(-1920, 0, 1920, 1080)).X,
            "屏幕矩形可以落在负坐标（左屏）");
    }
}
