using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro;

/// <summary>
/// 长截图的行拼接：把「滚动前后两帧」按行对齐，只把新露出来的那几行接到下面。
/// </summary>
/// <remarks>
/// <para>
/// 判定只看**行**：滚动是纯垂直平移，上一帧第 <c>r + shift</c> 行就是这一帧第 <c>r</c> 行，
/// 所以按行做位移搜索（<see cref="FindShift"/>）比整图相似度省事得多，也不用 OCR。
/// </para>
/// <para>
/// 两条抗干扰：① 顶部 / 底部那两截可能是固定页眉页脚（滚动时不动），检测时跳过；
/// ② 行内按步长抽样、允许每通道 ±<see cref="Tolerance"/> 的误差（光标闪烁、亚像素抖动）。
/// 位移 0 = 画面没动（到底 / 滚轮没打到目标窗口），调用方据此收尾；认不出重叠返回 <c>null</c>。
/// </para>
/// <para>
/// 行数据一律 Bgra32、每行 <c>宽 × 4</c> 字节；<see cref="Start"/> / <see cref="Append"/> 都吃
/// <see cref="BitmapSource"/>，<see cref="Render"/> / <see cref="Preview"/> 吐回位图。
/// 全是纯逻辑（不碰窗口），<c>--selftest</c> 用合成帧直接断言。
/// </para>
/// </remarks>
internal sealed class ScrollStitch
{
    /// <summary>至少要有这么多行重叠才认这个位移（太少就是在猜）。</summary>
    public const int MinOverlap = 24;

    /// <summary>拼接高度上限（行）：再高也不接了，参数给个明确收尾。</summary>
    public const int MaxRows = 12000;

    /// <summary>拼接位图的内存上限（按行累加，超了就停）。</summary>
    private const long MaxBytes = 96L * 1024 * 1024;

    /// <summary>检测时跳过顶部 / 底部的比例（固定页眉页脚不参与对齐）。</summary>
    private const double SkipTopRatio = 0.15;
    private const double SkipBottomRatio = 0.08;

    /// <summary>行内像素允许的误差（每通道 0–255）。</summary>
    private const int Tolerance = 2;

    /// <summary>行内抽样步长（每隔几个像素比一次）。</summary>
    private const int SampleStep = 3;

    /// <summary>允许的差异行占比（千分比）：动画 / 光标闪烁不该判成「对不上」。</summary>
    private const int MismatchPermille = 20;

    private readonly List<byte[]> _rows = new();
    private byte[][] _last = Array.Empty<byte[]>();
    private int _stride;
    private int _limit;

    /// <summary>拼接宽度（物理像素）；第一帧进来之后才有值。</summary>
    public int Width { get; private set; }

    /// <summary>当前拼接高度（行 / 物理像素）。</summary>
    public int Height => _rows.Count;

    /// <summary>单帧高度（位移自适应那套按它算比例）。</summary>
    public int FrameHeight { get; private set; }

    /// <summary>已经到高度上限：调用方该收尾了。</summary>
    public bool Full => _limit > 0 && _rows.Count >= _limit;

    /// <summary>拿第一帧当拼接起点（同一份实例也可以重来：会清空重来）。</summary>
    public void Start(BitmapSource frame)
    {
        var rows = ReadRows(frame);
        if (rows.Length == 0) throw new ArgumentException("画面高度为 0");

        Width = frame.PixelWidth;
        FrameHeight = rows.Length;
        _stride = Width * 4;
        _limit = Math.Min(MaxRows, (int)Math.Max(MinOverlap, MaxBytes / Math.Max(1, _stride)));

        _rows.Clear();
        _rows.AddRange(rows);
        _last = rows;
    }

    /// <summary>
    /// 接下一帧。返回检测到的位移（行数：0 = 没动），认不出重叠返回 <c>null</c>。
    /// 位移大于 0 时把这帧最后那 <c>shift</c> 行接进拼接图。
    /// </summary>
    public int? Append(BitmapSource frame)
    {
        if (_rows.Count == 0)
        {
            Start(frame);
            return 0;
        }

        if (frame.PixelWidth != Width) return null;

        var rows = ReadRows(frame);
        int? shift = FindShift(_last, rows);
        if (shift is not int s) return null;

        if (s > 0 && !Full) AppendBottom(rows, Math.Min(s, _limit - _rows.Count));
        _last = rows;
        return s;
    }

    /// <summary>把这一帧最后 <paramref name="count"/> 行接到拼接图下面。</summary>
    private void AppendBottom(byte[][] rows, int count)
    {
        int from = rows.Length - count;
        for (int r = from; r < rows.Length; r++) _rows.Add(rows[r]);
    }

    /// <summary>按行对齐两帧，返回位移（行）；认不出重叠返回 <c>null</c>。</summary>
    /// <remarks>
    /// 候选位移从小到大试（位移越小 = 比较的行越多），第一个对上的就是答案：
    /// 真实位移只会有一个候选能让整段「几乎逐行相同」都对上。
    /// 两帧各自的顶部 / 底部页脚都不参与比对（固定页脚滚动时不动，比了反而会把对齐拽偏）。
    /// </remarks>
    public static int? FindShift(byte[][] prev, byte[][] next)
    {
        if (prev.Length == 0 || next.Length == 0 || prev.Length != next.Length) return null;

        int height = next.Length;
        int top = (int)(height * SkipTopRatio);
        int footer = (int)(height * SkipBottomRatio);
        int bottom = height - footer;            // 这一帧自己的底部页脚
        int maxShift = height - MinOverlap;

        for (int shift = 0; shift <= maxShift; shift++)
        {
            // 上一帧的底部页脚也要避开：对齐之后它落在 shift 之后的那一截
            int end = bottom - shift;
            if (end - top < MinOverlap) break;
            if (RowsMatch(prev, next, shift, top, end)) return shift;
        }

        return null;
    }

    /// <summary>这一帧的最后 <c>shift</c> 行是「新露出来的内容」。</summary>
    private static bool RowsMatch(byte[][] prev, byte[][] next, int shift, int from, int to)
    {
        int limit = Math.Max(1, (to - from) * MismatchPermille / 1000);
        int bad = 0;

        for (int r = from; r < to; r++)
        {
            if (SameRow(prev[r + shift], next[r])) continue;
            if (++bad > limit) return false;
        }
        return true;
    }

    /// <summary>两行是否算同一行（抽样 + 容差）。</summary>
    private static bool SameRow(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;

        for (int i = 0; i + 2 < a.Length; i += 4 * SampleStep)
        {
            if (Math.Abs(a[i] - b[i]) > Tolerance) return false;
            if (Math.Abs(a[i + 1] - b[i + 1]) > Tolerance) return false;
            if (Math.Abs(a[i + 2] - b[i + 2]) > Tolerance) return false;
        }
        return true;
    }

    /// <summary>拼成一张长图（物理像素，Bgra32，已冻结）。</summary>
    public BitmapSource Render()
    {
        if (_rows.Count == 0) throw new InvalidOperationException("还没有拼到内容");

        var pixels = new byte[_stride * _rows.Count];
        for (int y = 0; y < _rows.Count; y++)
            Buffer.BlockCopy(_rows[y], 0, pixels, y * _stride, _stride);

        var image = BitmapSource.Create(Width, _rows.Count, 96, 96, PixelFormats.Bgra32, null, pixels, _stride);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// 预览：把整张拼接图缩进 <paramref name="maxWidth"/> × <paramref name="maxHeight"/>。
    /// 直接抽样，不从 <see cref="Render"/> 的大图上缩放（每帧都建一张上百 MB 的图没必要）。
    /// </summary>
    public BitmapSource Preview(int maxWidth, int maxHeight)
    {
        if (_rows.Count == 0) throw new InvalidOperationException("还没有拼到内容");
        if (maxWidth < 1 || maxHeight < 1) throw new ArgumentOutOfRangeException(nameof(maxWidth));

        int stepX = Math.Max(1, (int)Math.Ceiling(Width / (double)maxWidth));
        int stepY = Math.Max(1, (int)Math.Ceiling(_rows.Count / (double)maxHeight));
        int previewWidth = Math.Max(1, Width / stepX);
        int previewHeight = Math.Max(1, _rows.Count / stepY);

        int stride = previewWidth * 4;
        var pixels = new byte[stride * previewHeight];

        for (int y = 0; y < previewHeight; y++)
        {
            var source = _rows[y * stepY];
            int rowOffset = y * stride;
            for (int x = 0; x < previewWidth; x++)
            {
                int from = x * stepX * 4;
                pixels[rowOffset + x * 4] = source[from];
                pixels[rowOffset + x * 4 + 1] = source[from + 1];
                pixels[rowOffset + x * 4 + 2] = source[from + 2];
                pixels[rowOffset + x * 4 + 3] = source[from + 3];
            }
        }

        var image = BitmapSource.Create(previewWidth, previewHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    /// <summary>把一帧拆成「每行一份 Bgra32 字节」——对齐与追加都按行做。</summary>
    private static byte[][] ReadRows(BitmapSource frame)
    {
        BitmapSource source = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        int stride = source.PixelWidth * 4;
        int height = source.PixelHeight;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var rows = new byte[height][];
        for (int y = 0; y < height; y++)
        {
            var row = new byte[stride];
            Buffer.BlockCopy(pixels, y * stride, row, 0, stride);
            rows[y] = row;
        }
        return rows;
    }
}
