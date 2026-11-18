using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 把贴图**拖出**到别的程序（照上游 <c>screenshotpinneddragexport.cpp</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 拖的数据与上游一致：一条临时 png 的文件 URL + 位图本身（有些接收方只认位图，比如聊天输入框），
/// 拖拽图是 160px 的等比缩略图、热点在正中。临时文件不是拖完就删——接收方可能在拖拽循环返回之后
/// 才去读，所以交给 <see cref="PinDragRetention"/> 记 30 分钟宽限期，取消则立刻删。
/// </para>
/// <para>
/// 拖拽图没有走 OLE 的 <c>IDragSourceHelper</c>，而是一个跟着光标走的无激活分层窗口
/// （<c>WS_EX_TRANSPARENT</c>，不挡拖放命中）。它在**后台线程**按 <c>GetCursorPos</c> + <c>SetWindowPos</c>
/// 挪位，因为 <c>DoDragDrop</c> 期间 UI 线程被 OLE 模态循环占着，WPF 的定时器不一定会走。
/// </para>
/// </remarks>
internal static class PinDragExport
{
    /// <summary>拖拽图边长（上游 <c>scaled(160, 160, KeepAspectRatio)</c>）。</summary>
    public const int PreviewSide = 160;

    /// <summary>
    /// 一次拖出的全部材料：拖拽数据 + 临时文件 + 保留额度。
    /// </summary>
    /// <remarks>
    /// 拆出来是为了能单独验（冒烟不需要真的按下鼠标跑 OLE 循环）：
    /// <see cref="Prepare"/> → 检查数据 / 文件 / 额度 → <see cref="Complete"/>。
    /// </remarks>
    internal sealed class Payload
    {
        private bool _settled;

        public DataObject Data { get; init; } = new();

        public string File { get; init; } = "";

        public string Directory { get; init; } = "";

        internal PinDragRetention.Lease? Lease { get; init; }

        /// <summary>结账：<paramref name="dropped"/> = 有程序接下（保留 30 分钟），否则立刻删。</summary>
        public void Complete(bool dropped)
        {
            if (_settled) return;
            _settled = true;
            PinDragRetention.Shared.Complete(Lease, dropped);
        }
    }

    /// <summary>
    /// 准备一次拖出（写临时 png、预约额度、组数据）。失败返回 <c>null</c>（临时文件已删干净）。
    /// </summary>
    public static Payload? Prepare(BitmapSource image, string baseName)
    {
        if (image == null) return null;

        string dir = "";
        try
        {
            dir = PinDragRetention.CreateDirectory();
            string file = Path.Combine(dir, SafeName(baseName) + ".png");
            using (var stream = File.Create(file))
                CaptureSave.Encode(stream, image, SaveFormat.Png, 90, 100);

            long bytes = new FileInfo(file).Length;
            var lease = PinDragRetention.Shared.Reserve(dir, bytes);
            if (lease == null)
            {
                PinDragRetention.Discard(dir);
                ToastService.Show("拖出的临时文件太多，稍后再试", "贴图", 3200, ToastKind.warning,
                    key: "pin-drag");
                return null;
            }

            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { file });
            data.SetData(DataFormats.Bitmap, image);

            return new Payload { Data = data, File = file, Directory = dir, Lease = lease };
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinDragExport", "准备拖出失败：" + ex.Message);
            if (dir.Length > 0) PinDragRetention.Discard(dir);
            return null;
        }
    }

    /// <summary>
    /// 开始一次拖出。返回有没有程序接下这份内容（没接下时临时文件已删干净）。
    /// </summary>
    /// <param name="owner">发起的贴图窗口（接收拖放的模态循环挂在它身上）。</param>
    /// <param name="image">要拖出去的图。</param>
    /// <param name="baseName">临时文件名（不含扩展名）。</param>
    public static bool Start(Window owner, BitmapSource image, string baseName)
    {
        var payload = Prepare(image, baseName);
        if (payload == null) return false;

        var preview = new PinDragPreview();
        bool dropped = false;

        try
        {
            preview.Show(image);
            var effect = DragDrop.DoDragDrop(owner, payload.Data, DragDropEffects.Copy);
            dropped = effect != DragDropEffects.None;
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinDragExport", "拖出失败：" + ex.Message);
        }
        finally
        {
            preview.Close();
            payload.Complete(dropped);
        }

        return dropped;
    }

    /// <summary>临时文件名：去掉路径里不能用的字符（上游用保存设置的命名模板，这里用贴图 + 时间）。</summary>
    private static string SafeName(string name)
    {
        string text = string.IsNullOrWhiteSpace(name)
            ? $"贴图-{DateTime.Now:yyyyMMdd-HHmmss}"
            : name;

        foreach (char bad in Path.GetInvalidFileNameChars())
            text = text.Replace(bad, '-');

        return text.Length > 80 ? text[..80] : text;
    }
}

/// <summary>跟着光标走的拖拽预览（160px 缩略图）。</summary>
internal sealed class PinDragPreview
{
    private Window? _window;
    private IntPtr _hwnd;
    private System.Threading.Timer? _timer;
    private int _busy;

    /// <summary>预览窗口（冒烟看尺寸 / 扩展样式用）。</summary>
    internal Window? Window => _window;

    /// <summary>在光标下摆出来（必须在 UI 线程调）。</summary>
    public void Show(BitmapSource image)
    {
        if (_window != null) return;

        var picture = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            Width = PinDragExport.PreviewSide,
            Height = PinDragExport.PreviewSide,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = PinDragExport.PreviewSide,
            Height = PinDragExport.PreviewSide,
            IsHitTestVisible = false,
            Opacity = 0,                       // 摆到光标下之前不显示，免得闪一下左上角
            Content = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x10, 0x14)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = picture,
            },
        };

        window.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(window).Handle;
            if (_hwnd == IntPtr.Zero) return;

            long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GwlExstyle).ToInt64();
            Win32.SetWindowLongPtr(_hwnd, Win32.GwlExstyle,
                new IntPtr(ex | Win32.WsExTransparent | Win32.WsExNoactivate | Win32.WsExToolwindow));
        };

        _window = window;
        window.Show();
        Follow();

        // DoDragDrop 期间 UI 线程在 OLE 的模态循环里，WPF 定时器不一定走；
        // 位置靠后台线程直接 SetWindowPos（跨线程调 Win32 是允许的）。
        _timer = new System.Threading.Timer(_ => Follow(), null, 15, 15);
        window.Opacity = 1;
    }

    /// <summary>关掉预览（可以重复调）。</summary>
    public void Close()
    {
        _timer?.Dispose();
        _timer = null;

        var window = _window;
        _window = null;
        _hwnd = IntPtr.Zero;

        if (window == null) return;
        try { window.Close(); } catch { /* 已经关了 */ }
    }

    /// <summary>把预览挪到光标中心（物理像素，任意线程可调）。</summary>
    private void Follow()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            IntPtr hwnd = _hwnd;
            if (hwnd == IntPtr.Zero) return;
            if (!Win32.GetCursorPos(out var pt)) return;
            if (!Win32.GetWindowRect(hwnd, out var rect)) return;

            int w = Math.Max(1, rect.Right - rect.Left);
            int h = Math.Max(1, rect.Bottom - rect.Top);

            Win32.SetWindowPos(hwnd, Win32.HwndTopmost,
                pt.X - w / 2, pt.Y - h / 2, 0, 0, Win32.SwpNosize | Win32.SwpNoactivate);
        }
        catch
        {
            // 预览只是视觉反馈，挪不动就算了
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}
