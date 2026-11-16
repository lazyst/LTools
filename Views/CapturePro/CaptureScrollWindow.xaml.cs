using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 长截图：滚一屏 → 抓一帧 → 认出重叠 → 把新内容接到下面，循环到「到底 / 到上限 / 用户点完成」。
/// </summary>
/// <remarks>
/// <para>
/// **不抢焦点**（<c>ShowActivated=False</c> + <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>）：
/// 滚轮必须打到目标窗口上，面板一激活就滚不动自己了。代价是收不到 <c>Esc</c>，
/// 出口只有「完成」「取消」两个鼠标按钮。
/// </para>
/// <para>
/// 抓帧与拼接都在后台线程（每轮要 <c>Sleep</c> 等画面稳定），只有预览 / 状态行走
/// <c>Dispatcher</c>；<c>ScreenShot.Capture</c> 是 GDI，不需要 UI 线程。
/// 面板尽量摆在选区**外面**（<see cref="ScrollLayout"/>）；实在没地方放时抓帧期间自己变透明，
/// 免得把自己拼进长图。
/// </para>
/// <para>
/// 抓帧 / 滚动两个动作都是字段（<see cref="FrameSource"/> / <see cref="ScrollBy"/>）：
/// 冒烟用合成帧驱动整套循环，不真滚用户的桌面。
/// </para>
/// </remarks>
public partial class CaptureScrollWindow : Window
{
    private const int DefaultNotches = 3;    // 一轮滚几格（一格 = 120）
    private const int MaxNotches = 8;
    private const int PreviewWidth = 236;    // 预览框（DIP，与 XAML 的边距对得上）
    private const int PreviewHeight = 250;

    private readonly Int32Rect _area;
    private readonly ScrollStitch _stitch = new();
    private readonly bool _hideWhileCapturing;
    private IntPtr _hwnd;

    private volatile bool _stop;      // 用户点了「完成」
    private volatile bool _cancel;    // 用户点了「取消」/ 关窗

    /// <summary>抓一帧（默认真抓选区那块屏幕；冒烟注入合成帧）。</summary>
    internal Func<Int32Rect, BitmapSource> FrameSource;

    /// <summary>滚一屏（默认把光标挪到选区中心再注入滚轮；冒烟注入空操作）。</summary>
    internal Action<int> ScrollBy;

    /// <summary>开始滚之前把目标窗口激活（冒烟注入空操作，别动真桌面）。</summary>
    internal Action PrepareTarget;

    /// <summary>等画面稳定的间隔 / 次数（冒烟调 0 就不用等）。</summary>
    internal int StableDelayMs = 80;

    internal int StableTries = 8;

    /// <summary>拼好的长图；用户取消 / 失败时为 null。</summary>
    internal BitmapSource? Result { get; private set; }

    /// <summary>失败原因（用户主动取消时为 null）。</summary>
    internal string? Failure { get; private set; }

    internal CaptureScrollWindow(Int32Rect area, double scale)
    {
        InitializeComponent();

        _area = area;
        double factor = scale > 0 ? scale : 1;

        FrameSource = a => ScreenShot.Capture(a);
        ScrollBy = notches =>
        {
            // 光标停在面板上 = 用户正要按「完成」/「取消」：先别滚，也别把光标从按钮上拽走
            while (!_stop && !_cancel && PanelContainsCursor()) Thread.Sleep(80);
            if (_stop || _cancel) return;

            MouseOps.Move(area.X + area.Width / 2, area.Y + area.Height / 2);
            MouseOps.Perform(MouseActionKind.wheelDown, 120 * Math.Clamp(notches, 1, MaxNotches));
        };
        PrepareTarget = ActivateTarget;

        var place = ScrollLayout.Place(area, WorkArea(area), factor, new Size(Width, Height));
        Left = place.Left;
        Top = place.Top;

        // 面板盖住选区时每一帧都会把它拍进去：抓帧那一瞬把自己藏起来（很少走到这一步）
        var selection = new Rect(area.X / factor, area.Y / factor,
            Math.Max(1, area.Width) / factor, Math.Max(1, area.Height) / factor);
        _hideWhileCapturing = place.IntersectsWith(selection);
    }

    /// <summary>跑一次长截图（模态）。返回拼好的长图（标注已叠上去）；取消 / 失败返回 null。</summary>
    internal static BitmapSource? Run(CaptureDocument doc)
    {
        if (!doc.HasArea) return null;

        var window = new CaptureScrollWindow(doc.Area, doc.Scale);
        bool? ok = window.ShowDialog();

        if (ok == true && window.Result != null)
            return doc.RenderOnto(window.Result);   // 覆盖层里画的标注叠到长图上

        if (window.Failure != null) TrayService.Notify("长截图失败：" + window.Failure);
        return null;
    }

    // ============ 循环 ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "准备滚动…";
        Task.Run(Loop);
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 兜底：任何关闭方式都让循环收尾，别留下一直在滚桌面的后台线程
        _cancel = true;
        _stop = true;
    }

    private void Loop()
    {
        try
        {
            PrepareTarget();

            var first = GrabStable();
            if (first == null) { Fail(Failure ?? "抓不到画面"); return; }
            _stitch.Start(first);
            Report(1);

            int notches = DefaultNotches;
            int frames = 1;
            int zeroStreak = 0;
            string reason = "已到底部";

            while (!_cancel)
            {
                if (_stop) { reason = "已停止"; break; }

                ScrollBy(notches);
                var frame = GrabStable();
                if (frame == null) { Fail(Failure ?? "抓不到画面"); return; }

                if (_stitch.Append(frame) is not int shift)
                {
                    reason = "认不出重叠（内容变化太大，重来一次）";
                    break;
                }

                frames++;
                if (shift <= 0)
                {
                    // 画面没动：可能到头了，也可能滚轮没打到目标窗口——再试一次大一点的幅度
                    zeroStreak++;
                    if (zeroStreak >= 2) break;
                    notches = Math.Min(MaxNotches, notches + 2);
                    continue;
                }

                zeroStreak = 0;

                // 位移过大说明重叠太少（容易认错），位移过小说明白跑一趟：自适应幅度
                int frameHeight = Math.Max(1, _stitch.FrameHeight);
                if (shift > frameHeight * 92 / 100) notches = Math.Max(1, notches - 1);
                else if (shift < frameHeight * 30 / 100) notches = Math.Min(MaxNotches, notches + 1);

                Report(frames);
                if (_stitch.Full) { reason = "到高度上限"; break; }
            }

            if (_cancel) { Cancel(); return; }
            if (_stitch.Height == 0) { Fail("没有拼到内容"); return; }

            var image = _stitch.Render();
            Result = image;
            AppLog.Info("CapturePro.Scroll",
                $"长截图：{frames} 帧 → {image.PixelWidth} × {image.PixelHeight} px（{reason}）");
            CloseOnUi(true, $"完成 · {image.PixelWidth} × {image.PixelHeight} px");
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.Scroll", ex);
            Fail(ex.Message);
        }
    }

    /// <summary>抓一帧「已经不动」的画面：连续两帧一样才算稳定（平滑滚动 / 动画走完）。</summary>
    private BitmapSource? GrabStable()
    {
        try
        {
            if (_hideWhileCapturing) SetPanelShown(false);

            var previous = FrameSource(_area);
            for (int i = 0; i < StableTries; i++)
            {
                if (_stop || _cancel) break;
                if (StableDelayMs > 0) Thread.Sleep(StableDelayMs);

                var next = FrameSource(_area);
                bool same = LooksSame(previous, next);
                previous = next;
                if (same) break;
            }
            return previous;
        }
        catch (Exception ex)
        {
            Failure = ex.Message;
            return null;
        }
        finally
        {
            if (_hideWhileCapturing) SetPanelShown(true);
        }
    }

    /// <summary>抽样比几行：整帧逐像素比（8 MB × 8 次）纯属浪费，滚动是整体平移，几行就够判断。</summary>
    private static bool LooksSame(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;

        int stride = a.PixelWidth * 4;
        var rowA = new byte[stride];
        var rowB = new byte[stride];

        for (int i = 1; i <= 8; i++)
        {
            int y = Math.Clamp(a.PixelHeight * i / 9, 0, a.PixelHeight - 1);
            var rect = new Int32Rect(0, y, a.PixelWidth, 1);
            a.CopyPixels(rect, rowA, stride, 0);
            b.CopyPixels(rect, rowB, stride, 0);

            for (int p = 0; p + 2 < stride; p += 16)
            {
                if (Math.Abs(rowA[p] - rowB[p]) > 2) return false;
                if (Math.Abs(rowA[p + 1] - rowB[p + 1]) > 2) return false;
                if (Math.Abs(rowA[p + 2] - rowB[p + 2]) > 2) return false;
            }
        }
        return true;
    }

    /// <summary>刷新预览与状态行（后台线程 → UI 线程）。</summary>
    private void Report(int frames)
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = $"已拼接 {frames} 帧 · 高 {_stitch.Height} px";
                try { Preview.Source = _stitch.Preview(PreviewWidth, PreviewHeight); }
                catch { /* 预览是辅助信息，糊了不影响拼接 */ }
            }));
        }
        catch { /* 窗口已经关了 */ }
    }

    /// <summary>抓帧期间把自己藏起来（只对「面板压在选区上」那种落点有用）。</summary>
    private void SetPanelShown(bool shown)
    {
        try
        {
            Dispatcher.Invoke(new Action(() => Opacity = shown ? 1 : 0));
            Thread.Sleep(60);   // 等分层窗口真的刷新，否则抓到的还是带面板的那一帧
        }
        catch { /* 窗口已经关了 */ }
    }

    /// <summary>光标是不是停在面板上（那一下用户要按按钮，别再滚屏、也别把光标拽走）。</summary>
    private bool PanelContainsCursor()
    {
        if (_hwnd == IntPtr.Zero || Opacity <= 0) return false;
        if (!Win32.GetCursorPos(out var cursor)) return false;
        if (!Win32.GetWindowRect(_hwnd, out var rect)) return false;

        return cursor.X >= rect.Left && cursor.X < rect.Right &&
               cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
    }

    /// <summary>把光标挪到选区中心，再把那个窗口调到前台——滚轮只会打给前台窗口。</summary>
    private void ActivateTarget()
    {
        try
        {
            MouseOps.Move(_area.X + _area.Width / 2, _area.Y + _area.Height / 2);
            Thread.Sleep(60);

            var hwnd = Win32.TopLevelWindowAtCursor();
            if (hwnd == IntPtr.Zero || hwnd == _hwnd || ForegroundApp.IsShellWindow(hwnd)) return;

            WindowAction.ActivateNoWait(hwnd);
            Thread.Sleep(120);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Scroll", $"激活目标窗口失败：{ex.Message}");
        }
    }

    // ============ 收尾 ============

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _stop = true;
        StatusText.Text = "正在收尾…";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancel = true;

    private void Cancel()
    {
        Result = null;
        CloseOnUi(false, "已取消");
    }

    private void Fail(string message)
    {
        Failure = message;
        CloseOnUi(false, message);
    }

    /// <summary>回到 UI 线程收尾：写状态行 + 关窗（<c>DialogResult</c> 只在模态可用）。</summary>
    private void CloseOnUi(bool keep, string status)
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = status;
                try { DialogResult = keep; }
                catch (InvalidOperationException)
                {
                    // 非模态 / 没显示过（冒烟）时 DialogResult 不可用，直接关窗
                    try { Close(); }
                    catch (Exception ex) { AppLog.Warn("CapturePro.Scroll", $"关窗失败：{ex.Message}"); }
                }
            }));
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Scroll", $"收尾失败：{ex.Message}");
        }
    }

    /// <summary>选区中心所在显示器的工作区（物理像素）；找不到退回整个虚拟桌面。</summary>
    private static Int32Rect WorkArea(Int32Rect area)
    {
        int cx = area.X + area.Width / 2;
        int cy = area.Y + area.Height / 2;

        foreach (var screen in Screens.All())
        {
            if (cx < screen.Left || cx >= screen.Left + screen.Width) continue;
            if (cy < screen.Top || cy >= screen.Top + screen.Height) continue;
            return new Int32Rect(screen.WorkLeft, screen.WorkTop, screen.WorkWidth, screen.WorkHeight);
        }

        return new Int32Rect(ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
            Math.Max(1, ScreenCapture.VirtualWidth), Math.Max(1, ScreenCapture.VirtualHeight));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GwlExstyle).ToInt64();
        Win32.SetWindowLongPtr(_hwnd, Win32.GwlExstyle, (IntPtr)(ex | Win32.WsExNoactivate | Win32.WsExToolwindow));
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0,
            Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);
    }

    /// <summary>同步跑一遍整个循环（冒烟用：注入合成帧，不弹窗、不滚真桌面）。</summary>
    internal void RunForTest() => Loop();
}
