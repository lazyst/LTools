using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro.Stitch;

namespace LTools.Features.CapturePro;

/// <summary>长截图的拼接方向：纵向（往下滚）/ 横向（往右滚）。</summary>
internal enum StitchAxis
{
    /// <summary>往下滚，接的是「行」。</summary>
    Vertical,

    /// <summary>往右滚，接的是「列」。</summary>
    Horizontal,
}

/// <summary>
/// 长截图的对外入口：把抓到的每一帧交给 <see cref="StitchStitcher"/>，再把拼出来的画布
/// 变成界面要的几张小位图。
/// </summary>
/// <remarks>
/// <para>
/// 拼接算法整体移植自 Snow Shot 的 <c>snow-stitch-images</c>（ORB 特征互匹配 → SSIM 候选打分 →
/// 补丁细化 → 视口状态机 → 分块画布），核心在 <see cref="StitchStitcher"/> 与
/// <see cref="StitchEstimator"/>；这个类只做三件 LTools 特有的事：
/// </para>
/// <list type="number">
/// <item><b>末尾固定条</b>（页底「返回顶部」「下载」这类不随内容滚动的条）：识别出它以后，
/// 喂进拼接器的每一帧都先剪掉最后那一截，成品末尾再补一次——这样它只出现一次，也不会
/// 干扰运动估计。识别方式与旧实现一致：尾部与上一帧同位置逐线一致、够长（≥
/// <see cref="MinStickyRows"/>）、不超过一屏的 1/3、里面有的是信息的线。</item>
/// <item><b>长度上限</b>（<see cref="MaxLines"/> 条线 / 96 MB）：到了就报 <see cref="Full"/>，
/// 调用方停抓帧。</item>
/// <item><b>输出形状</b>：<see cref="Extent"/> / <see cref="OutputWidth"/> / <see cref="OutputHeight"/>
/// 都把固定条算在里面（它是成品的最后几线），所以每帧的 <c>Extent</c> 只增不减。</item>
/// </list>
/// <para>
/// <see cref="Append"/> 返回运动估计给出的**带符号**位移（沿轴像素数，与 Snow Shot 一致）：
/// <c>&lt; 0</c> = 往下滚（内容朝上走，接在成品后面），<c>&gt; 0</c> = 往回滚 / 往上补，
/// <c>0</c> = 画面没动，<c>null</c> = 认不出重叠（或帧尺寸变了）。全是纯逻辑（不碰窗口），
/// <c>--selftest</c> 用合成帧直接断言。
/// </para>
/// </remarks>
internal sealed class ScrollStitch
{
    /// <summary>长度上限里至少也要留这么多线（帧窄到内存上限小于这个数时以它为准）。</summary>
    public const int MinOverlap = 24;

    /// <summary>
    /// 末尾固定条至少要有这么长（线）才当「固定页脚 / 置底按钮条」处理。
    /// 太矮的多半只是巧合的纯色行，宁可不动它（固定条重复一遍也比剪错内容好）。
    /// </summary>
    public const int MinStickyRows = 6;

    /// <summary>拼接长度上限（线）：再长也不接了，参数给个明确收尾。</summary>
    public const int MaxLines = 12000;

    /// <summary>拼接位图的内存上限（按线累加，超了就停）。</summary>
    private const long MaxBytes = 96L * 1024 * 1024;

    /// <summary>固定条判定的「同一条线」容差（平均差 ≤ 4/255：视频、光标闪一下也算没动）。</summary>
    private const int LineTolerance = 4;

    /// <summary>线内灰度跨度低于这个值 = 没有信息（纯色），不能当固定条的证据。</summary>
    private const int FlatSpread = 6;

    /// <summary>判断「这条线有没有信息」时最多抽几个点。</summary>
    private const int SignatureWidth = 128;

    private readonly StitchAxis _axis;
    private readonly int _maxLines;

    private StitchStitcher? _stitch;
    private StitchFrame? _first;
    private StitchFrame? _previous;
    private StitchFrame? _footerSource;
    private int _sticky;
    private bool _stickyDecided;
    private int _limit;

    public ScrollStitch() : this(StitchAxis.Vertical) { }

    public ScrollStitch(StitchAxis axis) : this(axis, MaxLines) { }

    /// <summary>同上，但换掉长度上限（自检用它把「到顶就停」压到几帧就能验）。</summary>
    public ScrollStitch(StitchAxis axis, int maxLines)
    {
        _axis = axis;
        _maxLines = Math.Max(MinOverlap, maxLines);
    }

    /// <summary>拼接方向（决定「线」是行还是列）。</summary>
    public StitchAxis Axis => _axis;

    /// <summary>帧宽（物理像素）；第一帧进来之后才有值。</summary>
    public int FrameWidth { get; private set; }

    /// <summary>帧高（物理像素）；第一帧进来之后才有值。</summary>
    public int FrameHeight { get; private set; }

    /// <summary>已经拼出来的长度（沿轴，物理像素，含末尾固定条）。</summary>
    public int Extent => (_stitch?.CanvasExtent ?? 0) + _sticky;

    /// <summary>拼出来的位图宽（物理像素）。</summary>
    public int OutputWidth => _axis == StitchAxis.Vertical ? FrameWidth : Extent;

    /// <summary>拼出来的位图高（物理像素）。</summary>
    public int OutputHeight => _axis == StitchAxis.Vertical ? Extent : FrameHeight;

    /// <summary>已经到长度上限：调用方该收尾了。</summary>
    public bool Full => _limit > 0 && Extent >= _limit;

    /// <summary>
    /// 末尾固定条的长度（线）：它不参与中间拼接，成品末尾补一次。
    /// 0 = 这次拼接没有固定条（或还没认出来）。
    /// </summary>
    public int StickyBottom => _sticky;

    /// <summary>拿第一帧当拼接起点（同一份实例也可以重来：会清空重来）。</summary>
    public void Start(BitmapSource frame)
    {
        var full = StitchFrame.FromBitmap(frame);
        FrameWidth = full.Width;
        FrameHeight = full.Height;

        int lineBytes = (_axis == StitchAxis.Vertical ? FrameWidth : FrameHeight) * StitchFrame.Channels;
        _limit = Math.Min(_maxLines, (int)Math.Max(MinOverlap, MaxBytes / Math.Max(1, lineBytes)));

        _sticky = 0;
        _stickyDecided = false;
        _first = full;
        _previous = full;
        _footerSource = full;
        _stitch = new StitchStitcher(full, _axis);
    }

    /// <summary>
    /// 接下一帧。返回值是带符号的位移：<c>&lt; 0</c> = 往下滚（接新内容），
    /// <c>&gt; 0</c> = 往回滚 / 往上补，<c>0</c> = 没动，<c>null</c> = 认不出重叠（或帧尺寸变了）。
    /// </summary>
    public int? Append(BitmapSource frame)
    {
        if (_stitch == null)
        {
            Start(frame);
            return 0;
        }

        var full = StitchFrame.FromBitmap(frame);
        if (full.Width != FrameWidth || full.Height != FrameHeight) return null;
        if (Full)
        {
            _previous = full;
            _footerSource = full;
            return 0;
        }

        // 固定条只在「画布还没长过」时判定：那之后画布里已经接了内容，重来就丢东西了。
        if (!_stickyDecided && _stitch.MaxPosition == 0 && _previous != null)
        {
            int viewport = _stitch.ViewportExtent;
            int stuck = StuckTail(_previous, full);
            // 整帧都和上一帧一样 = 画面根本没动，说明不了尾部是不是固定条，等下一帧再判。
            if (stuck < viewport)
            {
                _stickyDecided = true;
                if (TryStickyBottom(full, stuck, out int sticky) && sticky < viewport - 4)
                {
                    _sticky = sticky;
                    _stitch = new StitchStitcher(CropTail(_first!, sticky), _axis);
                }
            }
        }

        StitchFrame incoming = _sticky > 0 ? CropTail(full, _sticky) : full;
        StitchDecision decision = _stitch.Process(incoming);

        _previous = full;
        _footerSource = full;

        return decision.Branch switch
        {
            StitchBranch.Skip => 0,
            StitchBranch.NoMovement => decision.Motion?.Kind == MotionOutcomeKind.NoMotion ? 0 : null,
            _ => decision.AcceptedOffset ?? 0,
        };
    }

    /// <summary>
    /// 收尾。固定条已经算在 <see cref="Extent"/> 里（成品末尾那一截），所以不需要再补；
    /// 保留这个方法是为了让调用方的「收尾 → 出图」流程不变，并且可以重复调用。
    /// </summary>
    public void Finish()
    {
    }

    /// <summary>拼成一张长图（物理像素，Bgra32，已冻结）。</summary>
    public BitmapSource Render()
    {
        if (_stitch == null) throw new InvalidOperationException("还没有拼到内容");

        var target = StitchFrame.Create(OutputWidth, OutputHeight);
        int canvasExtent = _stitch.CanvasExtent;
        _stitch.CopyAxisTo(0, canvasExtent, target);
        if (_sticky > 0 && _footerSource != null)
            BlitAxis(_footerSource, _footerSource.AxisExtent(_axis) - _sticky, _sticky, target, canvasExtent);
        return target.ToBitmap();
    }

    /// <summary>
    /// 末尾 <paramref name="rows"/> 条线的位图：就地显示「拼接图最后这一屏」用（每帧只搬这几条，
    /// 不必把上百 MB 的整张长图重画一遍）。
    /// </summary>
    public BitmapSource Tail(int rows)
    {
        int total = Extent;
        if (_stitch == null || total <= 0) throw new InvalidOperationException("还没有拼到内容");

        int count = Math.Clamp(rows, 1, total);
        return BuildLines(total - count, count).ToBitmap();
    }

    /// <summary>
    /// 预览：把整张拼接图缩进 <paramref name="maxWidth"/> × <paramref name="maxHeight"/>。
    /// 直接抽样，不从 <see cref="Render"/> 的大图上缩放（每帧都建一张上百 MB 的图没必要）。
    /// </summary>
    public BitmapSource Preview(int maxWidth, int maxHeight)
    {
        int alongCount = Extent;
        if (_stitch == null || alongCount <= 0) throw new InvalidOperationException("还没有拼到内容");
        if (maxWidth < 1 || maxHeight < 1) throw new ArgumentOutOfRangeException(nameof(maxWidth));

        bool vertical = _axis == StitchAxis.Vertical;
        int crossCount = vertical ? FrameWidth : FrameHeight;
        int maxAlong = vertical ? maxHeight : maxWidth;
        int maxCross = vertical ? maxWidth : maxHeight;

        int stepAlong = Math.Max(1, (int)Math.Ceiling(alongCount / (double)maxAlong));
        int stepCross = Math.Max(1, (int)Math.Ceiling(crossCount / (double)maxCross));
        int previewAlong = Math.Max(1, alongCount / stepAlong);
        int previewCross = Math.Max(1, crossCount / stepCross);

        int previewWidth = vertical ? previewCross : previewAlong;
        int previewHeight = vertical ? previewAlong : previewCross;
        int stride = previewWidth * StitchFrame.Channels;
        var pixels = new byte[stride * previewHeight];
        var line = new byte[LineBytes];

        for (int a = 0; a < previewAlong; a++)
        {
            CopyLine(a * stepAlong, line);
            for (int c = 0; c < previewCross; c++)
            {
                int from = c * stepCross * StitchFrame.Channels;
                int x = vertical ? c : a;
                int y = vertical ? a : c;
                int to = y * stride + x * StitchFrame.Channels;
                pixels[to] = line[from];
                pixels[to + 1] = line[from + 1];
                pixels[to + 2] = line[from + 2];
                pixels[to + 3] = line[from + 3];
            }
        }

        var image = BitmapSource.Create(previewWidth, previewHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    // ============ 输出 ============

    private int LineBytes => (_axis == StitchAxis.Vertical ? FrameWidth : FrameHeight) * StitchFrame.Channels;

    /// <summary>把成品里从第 <paramref name="start"/> 条线起的 <paramref name="count"/> 条线拼成一张图。</summary>
    private StitchFrame BuildLines(int start, int count)
    {
        bool vertical = _axis == StitchAxis.Vertical;
        var target = StitchFrame.Create(vertical ? FrameWidth : count, vertical ? count : FrameHeight);
        var line = new byte[LineBytes];

        for (int i = 0; i < count; i++)
        {
            CopyLine(start + i, line);
            if (vertical)
            {
                Buffer.BlockCopy(line, 0, target.Pixels, i * target.RowBytes, LineBytes);
                continue;
            }

            for (int y = 0; y < FrameHeight; y++)
            {
                Buffer.BlockCopy(
                    line, y * StitchFrame.Channels,
                    target.Pixels, (y * target.Width + i) * StitchFrame.Channels,
                    StitchFrame.Channels);
            }
        }

        return target;
    }

    /// <summary>成品第 <paramref name="index"/> 条线（画布那一段走分块画布，末尾那几线走固定条）。</summary>
    private void CopyLine(int index, byte[] destination)
    {
        StitchStitcher stitch = _stitch ?? throw new InvalidOperationException("还没有拼到内容");
        int canvasExtent = stitch.CanvasExtent;
        if (index < canvasExtent)
        {
            stitch.CopyLine(index, destination);
            return;
        }

        StitchFrame source = _footerSource ?? throw new InvalidOperationException("固定条来源帧不见了");
        CopyFrameLine(source, source.AxisExtent(_axis) - _sticky + (index - canvasExtent), destination);
    }

    private void CopyFrameLine(StitchFrame frame, int index, byte[] destination)
    {
        if (_axis == StitchAxis.Vertical)
        {
            Buffer.BlockCopy(frame.Pixels, index * frame.RowBytes, destination, 0, LineBytes);
            return;
        }

        for (int y = 0; y < frame.Height; y++)
        {
            Buffer.BlockCopy(
                frame.Pixels, (y * frame.Width + index) * StitchFrame.Channels,
                destination, y * StitchFrame.Channels,
                StitchFrame.Channels);
        }
    }

    /// <summary>把 <paramref name="source"/> 的 [<paramref name="sourceStart"/>, +count) 写到 <paramref name="target"/> 的第 <paramref name="dest"/> 条线起。</summary>
    private void BlitAxis(StitchFrame source, int sourceStart, int count, StitchFrame target, int dest)
    {
        if (count <= 0) return;
        if (_axis == StitchAxis.Vertical)
        {
            Buffer.BlockCopy(
                source.Pixels, sourceStart * source.RowBytes,
                target.Pixels, dest * target.RowBytes,
                count * target.RowBytes);
            return;
        }

        int bytes = count * StitchFrame.Channels;
        for (int y = 0; y < target.Height; y++)
        {
            Buffer.BlockCopy(
                source.Pixels, (y * source.Width + sourceStart) * StitchFrame.Channels,
                target.Pixels, (y * target.Width + dest) * StitchFrame.Channels,
                bytes);
        }
    }

    /// <summary>剪掉沿轴末尾的 <paramref name="drop"/> 条线（固定条那几线）。</summary>
    private StitchFrame CropTail(StitchFrame frame, int drop)
    {
        int extent = frame.AxisExtent(_axis) - drop;
        if (drop <= 0 || extent <= 0) return frame;

        StitchFrame target = _axis == StitchAxis.Vertical
            ? StitchFrame.Create(frame.Width, extent)
            : StitchFrame.Create(extent, frame.Height);
        BlitAxis(frame, 0, extent, target, 0);
        return target;
    }

    // ============ 固定条 ============

    /// <summary>尾部「和上一帧同位置一模一样」的线数（从最后一条往前数）。</summary>
    private int StuckTail(StitchFrame previous, StitchFrame next)
    {
        int lineBytes = LineBytes;
        var left = new byte[lineBytes];
        var right = new byte[lineBytes];
        int height = Math.Min(previous.AxisExtent(_axis), next.AxisExtent(_axis));

        int count = 0;
        for (int index = height - 1; index >= 0; index--)
        {
            CopyFrameLine(previous, index, left);
            CopyFrameLine(next, index, right);
            if (!SameLine(left, right)) break;
            count++;
        }

        return count;
    }

    /// <summary>
    /// 这一段尾部算不算末尾固定条：够长、不超过一屏三分之一，而且里面有信息的线足够多
    /// （一整片纯色背景也可能「同位置一模一样」，那是巧合，不能当固定条剪掉）。
    /// </summary>
    private bool TryStickyBottom(StitchFrame frame, int stuck, out int height)
    {
        height = 0;
        int extent = frame.AxisExtent(_axis);
        if (stuck < MinStickyRows || stuck > extent / 3) return false;

        var line = new byte[LineBytes];
        int informative = 0;
        for (int index = extent - stuck; index < extent; index++)
        {
            CopyFrameLine(frame, index, line);
            if (IsInformative(line)) informative++;
        }

        if (informative < MinStickyRows) return false;
        height = stuck;
        return true;
    }

    /// <summary>两条线像素是不是一样（平均差 ≤ <see cref="LineTolerance"/>）。</summary>
    private static bool SameLine(byte[] left, byte[] right)
    {
        int length = Math.Min(left.Length, right.Length);
        if (length == 0) return true;

        int limit = LineTolerance * length;
        int sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum += Math.Abs(left[i] - right[i]);
            if (sum > limit) return false;
        }

        return true;
    }

    /// <summary>这条线有没有信息（灰度跨度够大）；整片纯色的线比了也说明不了什么。</summary>
    private static bool IsInformative(byte[] line)
    {
        int pixels = line.Length / StitchFrame.Channels;
        if (pixels <= 0) return false;

        int samples = Math.Min(SignatureWidth, pixels);
        int min = 255, max = 0;
        for (int i = 0; i < samples; i++)
        {
            int x = samples == 1 ? 0 : i * (pixels - 1) / (samples - 1);
            int p = x * StitchFrame.Channels;
            // Bgra32：byte0 = B、1 = G、2 = R。
            byte gray = (byte)((line[p] * 77 + line[p + 1] * 151 + line[p + 2] * 28) >> 8);
            if (gray < min) min = gray;
            if (gray > max) max = gray;
        }

        return max - min >= FlatSpread;
    }
}
