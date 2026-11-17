using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 一帧画面的「叠加层」：摄像头画中画 + 键鼠特效，直接画在待编码的 BGRA 缓冲上。
/// </summary>
/// <remarks>
/// <para>
/// 只包一层 <see cref="Graphics"/>：缓冲被固定（<see cref="GCHandle"/>）后用
/// <c>Format32bppRgb</c> 包成 <see cref="Bitmap"/>——抓屏得到的 alpha 字节本来就是未定义的，
/// 用 RGB 格式让 GDI+ 当它是「不透明」处理，半透明笔刷才会正常混色（用 ARGB 时 dst alpha = 0
/// 会让叠上去的东西变成纯色块）。
/// </para>
/// <para>
/// 画完把画笔 / 画刷都释放掉；每帧不新建位图（只有摄像头新帧到达时才刷新它的位图），
/// 所以这块开销基本只是几次 <see cref="Graphics.DrawImage(Image, Rectangle)"/>。
/// </para>
/// </remarks>
internal sealed class RecordComposer : IDisposable
{
    private readonly RecordOptions _options;
    private readonly int _width;
    private readonly int _height;

    private readonly GCHandle _pixels;
    private readonly Bitmap _target;
    private readonly Graphics _graphics;

    private readonly RecordEffects? _effects;
    private readonly RecordCamera? _camera;
    private readonly int _cameraSide;

    private readonly GCHandle? _cameraPixels;
    private readonly Bitmap? _cameraBitmap;
    private readonly byte[]? _cameraBuffer;

    private readonly Pen _cameraBorder = new(Color.FromArgb(230, 255, 255, 255), 2f);
    private readonly SolidBrush _cameraBack = new(Color.FromArgb(180, 20, 20, 24));

    private bool _hasCameraFrame;

    private RecordComposer(byte[] buffer, int width, int height, RecordOptions options,
        RecordCamera? camera)
    {
        _options = options;
        _width = width;
        _height = height;

        _pixels = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        _target = new Bitmap(width, height, width * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, _pixels.AddrOfPinnedObject());
        _graphics = Graphics.FromImage(_target);
        _graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        _graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;

        // 特效层**总是**建：录屏面板第二行的开关是录制中也能点的，
        // 到这一帧才去问 `_options` 有没有特效（以前按 HasEffects 早退，录制中打开也没效果）。
        _effects = new RecordEffects(options);

        if (camera != null)
        {
            _camera = camera;
            _cameraSide = camera.Side;

            var frame = new byte[_cameraSide * _cameraSide * 4];
            _cameraBuffer = frame;
            _cameraPixels = GCHandle.Alloc(frame, GCHandleType.Pinned);
            _cameraBitmap = new Bitmap(_cameraSide, _cameraSide, _cameraSide * 4,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb, _cameraPixels.Value.AddrOfPinnedObject());
        }
    }

    /// <summary>
    /// 建叠加层。**不再按「有没有特效」跳过**：面板上的光标高亮 / 点击涟漪 / 拖尾 / 按键浮层
    /// 都是录制中随时可切的，跳过就等于「打开也没用」。没摄像头时只是少画一块画中画。
    /// </summary>
    public static RecordComposer? Create(byte[] buffer, int width, int height, RecordOptions options,
        RecordCamera? camera)
    {
        try
        {
            return new RecordComposer(buffer, width, height, options, camera);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "建画面叠加层失败（这一轮不叠特效）：" + ex.Message);
            return null;
        }
    }

    /// <summary>在写进编码器之前把这一帧补完（<paramref name="area"/> = 这一帧实际抓的屏幕矩形）。</summary>
    public void Apply(Int32Rect area, long nowMs)
    {
        _effects?.Sample(nowMs);

        int bottomInset = 0;
        if (_camera != null && TryRefreshCamera())
        {
            var (x, y, side) = RecordOptions.CameraPlacement(
                _width, _height, _cameraSide, _options.CameraOffsetLeft, _options.CameraOffsetBottom);
            DrawCamera(x, y, side);
            bottomInset = _height - y + 8;
        }

        _effects?.Render(_graphics, _width, _height, area, Math.Max(0, bottomInset), nowMs);
    }

    private bool TryRefreshCamera()
    {
        if (_camera == null || _cameraBitmap == null || _cameraBuffer == null) return false;

        if (_camera.TryCopyFrame(_cameraBuffer)) _hasCameraFrame = true;
        return _hasCameraFrame;
    }

    private void DrawCamera(int x, int y, int side)
    {
        var rect = new Rectangle(x, y, side, side);

        using var clip = new GraphicsPath();
        if (_options.CameraShape == RecordCameraShape.circle)
        {
            // 圆形占满整格（半径 = 边长 / 2）
            clip.AddEllipse(rect);
            _graphics.SmoothingMode = SmoothingMode.AntiAlias;
        }
        else
        {
            float radius = Math.Max(6f, side * 0.14f);
            float d = radius * 2;
            clip.AddArc(rect.X, rect.Y, d, d, 180, 90);
            clip.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            clip.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            clip.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            clip.CloseFigure();
        }

        var saved = _graphics.Save();
        _graphics.SetClip(clip, CombineMode.Replace);

        _graphics.FillRectangle(_cameraBack, rect);
        _graphics.DrawImage(_cameraBitmap!, rect);

        _graphics.Restore(saved);
        _graphics.DrawPath(_cameraBorder, clip);
    }

    public void Dispose()
    {
        _effects?.Dispose();

        _graphics.Dispose();
        _target.Dispose();

        _cameraBitmap?.Dispose();
        if (_cameraPixels is { IsAllocated: true }) _cameraPixels.Value.Free();

        if (_pixels.IsAllocated) _pixels.Free();

        _cameraBorder.Dispose();
        _cameraBack.Dispose();
    }
}
