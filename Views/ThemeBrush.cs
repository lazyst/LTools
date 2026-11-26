using System.Windows;
using System.Windows.Media;

namespace LTools.Views;

/// <summary>
/// 画刷：冻结的纯色画笔 / 线条（自绘画布在 <c>OnRender</c> 里用）。
/// </summary>
/// <remarks>
/// <para>
/// 截图 Pro 那批自绘画布（拼图 / 切图 / 贴图 / 录屏框）每帧都要画笔，
/// 每帧新建一个没冻结的 <see cref="SolidColorBrush"/> 既慢又会被 WPF 的线程检查卡住，
/// 所以统一走这里：建好就 <c>Freeze()</c>。
/// </para>
/// <para>
/// **别再在窗口里各写一份 <c>Frozen</c> / <c>FrozenPen</c>**：全项目原来有十几份同样的
/// 五行代码；也别用 <c>new SolidColorBrush(Colors.X)</c> 直接画。
/// </para>
/// </remarks>
internal static class ThemeBrush
{
    /// <summary>冻结的纯色画刷。</summary>
    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>冻结的实线画笔（1 像素对齐由调用方的坐标决定）。</summary>
    public static Pen Pen(Color color, double width)
    {
        var pen = new Pen(Frozen(color), width);
        pen.Freeze();
        return pen;
    }

    /// <summary>冻结的虚线画笔（5,3 的短划线，截图 Pro 的辅助线一直用这个节奏）。</summary>
    public static Pen DashPen(Color color, double width)
    {
        var pen = new Pen(Frozen(color), width) { DashStyle = new DashStyle(new[] { 5.0, 3.0 }, 0) };
        pen.Freeze();
        return pen;
    }
}
