using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace LTools.Views;

/// <summary>
/// 冻结屏幕的**取点 / 取色**窗口（由 <see cref="ScreenPicker"/> 创建）。
/// </summary>
/// <remarks>
/// <para>
/// 画面是**调用时拍下的静态截图**，不是实时屏幕：用户点的时候目标不会动，
/// 取到的坐标与颜色天然就是画面上的那一点。
/// </para>
/// <para>
/// 框选区域**已经不在这里**（2026-11-26 合并）：要框一块给参数用就走截图 Pro
/// （<c>CaptureSession.TryCapture(selectionOnly: true)</c>），吸附 / 智能选区 / 多屏都只有一份实现。
/// 这里保留的是 Pro 没有的能力：**放大镜 + 单像素取色**。
/// </para>
/// <para>
/// 窗口盖住光标所在的那台显示器；坐标换算为「显示器左上角 + 图像像素」，
/// 与 <see cref="Native.ScreenCapture"/> 的物理像素坐标系一致（多显示器 / 混合 DPI 都不会偏）。
/// </para>
/// </remarks>
public partial class ScreenPickerWindow : Window
{
    private readonly BitmapSource _shot;
    private readonly System.Drawing.Rectangle _monitor;
    private readonly bool _wantColor;
    private readonly string _pickHint = "";   // 调用方给的提示文字（「屏幕取点」的 hint 参数）

    /// <summary>取点结果（物理屏幕像素）。</summary>
    public int PointX { get; private set; }
    public int PointY { get; private set; }

    /// <summary>取点处的颜色 <c>#RRGGBB</c>（取色时才有意义）。</summary>
    public string Hex { get; private set; } = "";

    /// <summary>十字准线已经落到画面上（可以确认）。</summary>
    private bool _hasPoint;

    /// <summary>用户是否确认（关闭 / Esc / 右键取消时为 false）。</summary>
    public bool Confirmed { get; private set; }

    public ScreenPickerWindow(BitmapSource shot, System.Drawing.Rectangle monitor, bool wantColor,
        string hint = "")
    {
        InitializeComponent();
        _shot = shot;
        _monitor = monitor;
        _wantColor = wantColor;
        _pickHint = (hint ?? "").Trim();

        Shot.Source = shot;
        MagImage.Source = shot;
        MagImage.Width = shot.PixelWidth;
        MagImage.Height = shot.PixelHeight;

        // 按目标显示器的 DPI 把物理矩形换算成 DIP：窗口正好盖住它。
        (double scaleX, double scaleY) = MonitorScale(monitor);
        Left = monitor.Left / scaleX;
        Top = monitor.Top / scaleY;
        Width = Math.Max(1, monitor.Width / scaleX);
        Height = Math.Max(1, monitor.Height / scaleY);

        HintText.Text = PointHint();
        // 放大镜只在取色时显示：取点只要十字准线 + 坐标，132×132 的黑框会把要点的目标挡住。
        MagBox.Visibility = ShowMagnifier(wantColor) ? Visibility.Visible : Visibility.Collapsed;

        // 显示器 DPI 与窗口当前 DPI 不一致时（把窗口挪到别的显示器），WPF 会按建议矩形重排一次，
        // 再钉一次保证仍是 1:1 覆盖。
        DpiChanged += (_, _) => PinToMonitor();
    }
}
