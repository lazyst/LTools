using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 冻结屏幕的取点 / 框选窗口（由 <see cref="ScreenPicker"/> 创建）。
/// </summary>
/// <remarks>
/// <para>
/// 画面是**调用时拍下的静态截图**，不是实时屏幕：用户框选时目标不会动，
/// 截下来的模板也没有 LTools 自己的窗口，匹配结果与框选内容天然一致。
/// </para>
/// <para>
/// 框选松手后**不直接确定**：选区带八个把手，可拖边 / 拖角 / 整体移动、方向键逐像素微调，
/// 再按「使用这块区域」/ Enter / 双击确认。像截图软件一样可以来回调，直到刚好框住目标。
/// </para>
/// <para>
/// 探测与吸附（Snipaste / PixPin 同款交互）：鼠标移动时高亮光标下的窗口 / 控件，
/// **单击**即选中它（拖动了就是自由框选）；滚轮在「控件 → 父容器 → 窗口」之间换层级；
/// 拖动边角时自动吸附到附近元素的边，按住 Ctrl 临时关闭。
/// </para>
/// <para>
/// 窗口盖住光标所在的那台显示器；坐标换算为「显示器左上角 + 图像像素」，
/// 与 <see cref="ScreenCapture"/> 的物理像素坐标系一致（多显示器 / 混合 DPI 都不会偏）。
/// </para>
/// </remarks>
public partial class ScreenPickerWindow : Window
{
    private const double MinSize = 4;     // 选区最小边长（DIP）
    private const double GripSize = 9;    // 把手方块尺寸（DIP）
    private const double GripHit = 8;     // 把手命中半径（DIP）
    private const double SnapDistance = 8;  // 边 / 角吸附距离（DIP）
    private const double PressSlop = 3;     // 按下后位移小于此值算「单击」而不是拖动

    private readonly BitmapSource _shot;
    private readonly System.Drawing.Rectangle _monitor;
    private readonly bool _pointMode;
    private readonly bool _wantColor;
    private readonly string _pickHint = "";   // 取点时调用方给的提示文字（「屏幕取点」的 hint 参数）
    private readonly List<Rectangle> _grips = new();

    // —— 窗口 / 控件探测（框选模式）——
    private readonly List<SnapBox> _windowTargets = new();   // Win32 窗口树，打开时枚举一次
    private readonly SnapProbe _probe = new();               // 按窗口缓存的「懒下钻」控件树
    private IReadOnlyList<SnapBox> _uiaTargets = Array.Empty<SnapBox>();   // 光标那条支路（深 → 浅）
    private SnapStop _uiaStop = SnapStop.complete;
    private int _uiaSignature;
    private IntPtr _uiaWindow;
    private readonly List<SnapBox> _hoverCandidates = new();
    private int _hoverIndex;
    private SnapBox? _hoverTarget;
    private Point _hoverAt = new(-9999, -9999);
    private IntPtr _uiaPending;      // 正在后台补层的窗口
    private string _notice = "";     // 「已复制坐标」这类一次性提示
    private DispatcherTimer? _noticeTimer;

    private List<double> _snapXs = new();
    private List<double> _snapYs = new();

    private Point _pressAt;
    private bool _pressMoved;
    private SnapBox? _pressTarget;

    private Rect? _sel;          // 当前选区（窗口 DIP）
    private DragMode _drag;
    private Handle _handle;
    private Point _origin;       // 本次拖动起点
    private Rect _originRect;    // 本次拖动开始时的选区

    /// <summary>框选的屏幕矩形（物理像素，含显示器偏移）。</summary>
    public Int32Rect Area { get; private set; }

    /// <summary>框选出来的模板图（从冻结画面裁剪，未包含任何 LTools 窗口）。</summary>
    public BitmapSource? Crop { get; private set; }

    /// <summary>取点结果（物理屏幕像素）。</summary>
    public int PointX { get; private set; }
    public int PointY { get; private set; }

    /// <summary>取点处的颜色 <c>#RRGGBB</c>。</summary>
    public string Hex { get; private set; } = "";

    /// <summary>十字准线已经落到画面上（取点 / 取色都可以确认）。</summary>
    private bool _hasPoint;

    /// <summary>用户是否确认（关闭 / Esc / 右键取消时为 false）。</summary>
    public bool Confirmed { get; private set; }

    private enum DragMode { None, New, Move, Resize }

    private enum Handle { None, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    public ScreenPickerWindow(BitmapSource shot, System.Drawing.Rectangle monitor, bool pointMode, bool wantColor,
        string hint = "")
    {
        InitializeComponent();
        _shot = shot;
        _monitor = monitor;
        _pointMode = pointMode;
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

        if (pointMode)
        {
            HintText.Text = PointHint();
            // 放大镜只在取色时显示：取点只要十字准线 + 坐标，132×132 的黑框会把要点的目标挡住。
            MagBox.Visibility = ShowMagnifier(pointMode, wantColor) ? Visibility.Visible : Visibility.Collapsed;
            DimPath.Visibility = Visibility.Collapsed;
            ActionBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            HintText.Text = "移动选窗口 / 控件 · 拖动自由框选 · 滚轮换层级 · 右键 / Esc 取消";
            ActionHint.Text = "拖动边角微调，方向键逐像素";
            BuildGrips();
        }

        // 显示器 DPI 与窗口当前 DPI 不一致时（把窗口挪到别的显示器），WPF 会按建议矩形重排一次，
        // 再钉一次保证仍是 1:1 覆盖。
        DpiChanged += (_, _) => PinToMonitor();
    }

    /// <summary>选区上的八个把手（框选模式下创建）。</summary>
    private void BuildGrips()
    {
        foreach (Handle h in new[]
                 {
                     Handle.TopLeft, Handle.TopRight, Handle.BottomLeft, Handle.BottomRight,
                     Handle.Top, Handle.Bottom, Handle.Left, Handle.Right,
                 })
        {
            var grip = new Rectangle
            {
                Width = GripSize,
                Height = GripSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Tag = h,
            };
            _grips.Add(grip);
            Layer.Children.Add(grip);
        }
    }

}
