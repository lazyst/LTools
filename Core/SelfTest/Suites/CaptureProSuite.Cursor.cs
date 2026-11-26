using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 「显示光标」（照上游 <c>screenshot/capture_cursor</c>）：指针相对底图的落位、
/// 没有快照时开关必须失效、历史里带回来的那一份能原样装回去。
/// </summary>
internal static partial class CaptureProSuite
{
    private static void CursorCases()
    {
        // 热点换算：屏幕坐标 − 图标热点 − 整屏底图左上角
        Point offset = CaptureDocument.CursorTopLeft(new Point(1000, 500), 3, 5,
            new Int32Rect(-1920, 100, 3840, 1000));
        Check.Equal(1000 - 3 + 1920, (int)offset.X, "指针偏移：X = 热点 − 图标热点 − 底图左边");
        Check.Equal(500 - 5 - 100, (int)offset.Y, "指针偏移：Y = 热点 − 图标热点 − 底图上边");

        var doc = new CaptureDocument(Png(8, 8), new Int32Rect(0, 0, 8, 8), 1);

        Check.False(doc.CursorAvailable, "新文档默认没有指针");
        Check.False(doc.CursorVisible, "没有指针时不会画");
        Check.False(doc.SetCursorVisible(true), "没有快照时开关不生效");

        doc.SetCursorImage(Png(24, 24), new Point(3, 4), visible: true);
        Check.True(doc.CursorAvailable, "装了小图之后可以画");
        Check.True(doc.CursorVisible, "visible 照传进来的值");
        Check.Equal(3, (int)doc.CursorSnapshot.Offset.X, "偏移原样存着");
        Check.Equal(4, (int)doc.CursorSnapshot.Offset.Y, "偏移原样存着");

        Check.False(doc.SetCursorVisible(false), "关掉之后不画");
        Check.True(doc.CursorAvailable, "关掉不等于没有快照（还能再开）");
        Check.True(doc.SetCursorVisible(true), "还能再打开");

        // 空快照 = 这张没有指针：可用性与开关一起清掉
        doc.SetCursorImage(null, default, true);
        Check.False(doc.CursorAvailable, "传 null 清掉快照");
        Check.False(doc.CursorVisible, "清掉之后不再画");

        // 换底图（翻采集历史）会顺手把指针清掉，别把上一张的指针留在新图上
        doc.SetCursorImage(Png(24, 24), new Point(3, 4), visible: true);
        doc.LoadBase(Png(8, 8), new Int32Rect(0, 0, 8, 8), 1, default);
        Check.False(doc.CursorAvailable, "LoadBase 之后旧指针不再留着");
    }

    private static BitmapSource Png(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;
            pixels[i + 3] = 255;
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            pixels, width * 4);
        image.Freeze();
        return image;
    }
}
