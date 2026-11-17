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
/// 每行先压成一条<b>灰度签名</b>（等距抽 <see cref="SignatureWidth"/> 个点），再按「对上的行占多少」
/// 给每个候选位移打分（<b>分数式</b>，不是「逐行必须像素级一致」）：真实网页里总有光标闪烁、
/// 视频、加载中的图片、亚像素抗锯齿，要求逐行严丝合缝会在真机上第一帧就认不出重叠（用户报过）。
/// 三条抗干扰：① 顶部 / 底部那两截可能是固定页眉页脚（滚动时不动），检测时跳过；
/// ② 灰度跨度太小的死板行（纯色背景）不参与打分，免得「整片空白怎么对都对」；
/// ③ 分数一样好时取<b>小位移</b>——周期性的列表 / 表格会出现多个同样好的候选，
/// 取小的既不会多接内容，也不会把「没动」当成「滚了一屏」。
/// </para>
/// <para>
/// 位移 0 = 画面没动（到底 / 滚轮没打到目标窗口），调用方据此收尾；认不出重叠返回 <c>null</c>。
/// 行数据一律 Bgra32、每行 <c>宽 × 4</c> 字节；<see cref="Start"/> / <see cref="Append"/> 都吃
/// <see cref="BitmapSource"/>，<see cref="Render"/> / <see cref="Tail"/> / <see cref="Preview"/> 吐回位图。
/// 全是纯逻辑（不碰窗口），<c>--selftest</c> 用合成帧直接断言。
/// </para>
/// <para>
/// <b>固定条</b>（页顶 / 页底的「返回顶部」「下载」这类不随内容滚动的条）：每一帧里它都在同一个 y
/// 上、像素一样，照直接下去会在长图中间重复十几遍。所以 ① 与上一帧同 y 一模一样的行不接进中间；
/// ② 首帧尾部那条固定条先剪掉（<see cref="StickyBottom"/>），<see cref="Finish"/> 时把那一条补一次
/// 到整张图的最下面；③ 页顶的固定条天然只出现一次（首帧顶部），不用管。
/// 只处理<b>整行级</b>的固定条：只占一部分行宽的浮动按钮（比如右下角悬浮的「回到顶部」）不处理。
/// </para>
/// </remarks>
internal sealed class ScrollStitch
{
    /// <summary>至少要有这么多行重叠才认这个位移（太少就是在猜）。</summary>
    public const int MinOverlap = 24;

    /// <summary>
    /// 底部固定条至少有这么高（行）才当「固定页脚 / 置底按钮条」处理。
    /// 太矮的多半只是巧合的纯色行，宁可不动它（固定条重复一遍也比剪错内容好）。
    /// </summary>
    public const int MinStickyRows = 6;

    /// <summary>拼接高度上限（行）：再高也不接了，参数给个明确收尾。</summary>
    public const int MaxRows = 12000;

    /// <summary>拼接位图的内存上限（按行累加，超了就停）。</summary>
    private const long MaxBytes = 96L * 1024 * 1024;

    /// <summary>检测时跳过顶部 / 底部的比例（固定页眉页脚不参与对齐）。</summary>
    private const double SkipTopRatio = 0.12;
    private const double SkipBottomRatio = 0.08;

    /// <summary>每行签名抽多少个点（宽度不足就按宽度来）。</summary>
    private const int SignatureWidth = 128;

    /// <summary>单行「算同一行」的平均灰度差上限（0–255）。</summary>
    private const int RowTolerance = 12;

    /// <summary>行内灰度跨度低于这个值 = 没有信息（纯色），不参与打分。</summary>
    private const int FlatSpread = 6;

    /// <summary>整段重叠里「对上的行」至少占这么多（视频 / 动画占掉一块也还认得出来）。</summary>
    private const double MinGoodRatio = 0.72;

    /// <summary>分数差在这个范围内算「一样好」，取更小的位移。</summary>
    private const double TieRatio = 0.02;

    /// <summary>粗筛时最多比多少行（大画面按等距抽样，位移多的时候才跑得动）。</summary>
    private const int CoarseRows = 48;

    /// <summary>粗筛留下几个候选去精筛。</summary>
    private const int RefineCount = 4;

    /// <summary>找「更小的等分候选」时，粗筛分数的宽容度（粗筛只比几十行，天然抖一点）。</summary>
    private const double CoarseSlack = 0.10;

    private readonly List<byte[]> _rows = new();
    private byte[][] _last = Array.Empty<byte[]>();
    private int _stride;
    private int _limit;
    private int _stickyBottom;

    /// <summary>拼接宽度（物理像素）；第一帧进来之后才有值。</summary>
    public int Width { get; private set; }

    /// <summary>当前拼接高度（行 / 物理像素）。</summary>
    public int Height => _rows.Count;

    /// <summary>单帧高度。</summary>
    public int FrameHeight { get; private set; }

    /// <summary>已经到高度上限：调用方该收尾了。</summary>
    public bool Full => _limit > 0 && _rows.Count >= _limit;

    /// <summary>
    /// 底部固定条的高度（行）：中间不重复它，<see cref="Finish"/> 时补一次到最下面。
    /// 0 = 这次拼接没有固定条（或还没认出来）。
    /// </summary>
    public int StickyBottom => _stickyBottom;

    /// <summary>拿第一帧当拼接起点（同一份实例也可以重来：会清空重来）。</summary>
    public void Start(BitmapSource frame)
    {
        var rows = ReadRows(frame);
        if (rows.Length == 0) throw new ArgumentException("画面高度为 0");

        Width = frame.PixelWidth;
        FrameHeight = rows.Length;
        _stride = Width * 4;
        _limit = Math.Min(MaxRows, (int)Math.Max(MinOverlap, MaxBytes / Math.Max(1, _stride)));

        _stickyBottom = 0;
        _rows.Clear();
        _rows.AddRange(rows);
        _last = rows;
    }

    /// <summary>
    /// 接下一帧。返回检测到的位移（行数：0 = 没动），认不出重叠返回 <c>null</c>。
    /// 位移大于 0 时把这一帧新露出来的那 <c>shift</c> 行接进拼接图（固定条那几行除外）。
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

        // 尾部与上一帧同 y 一模一样的行 = 没跟着内容走（固定页脚 / 置底按钮条）。
        // 第一次认出来时把首帧尾部那条从拼接图里剪掉，收尾再补回去（见 Finish）。
        int stuck = StuckTail(_last, rows);
        if (_stickyBottom == 0 && s > 0 && _rows.Count <= FrameHeight &&
            TryStickyBottom(rows, stuck, out int sticky))
        {
            _stickyBottom = sticky;
            if (_rows.Count > sticky) _rows.RemoveRange(_rows.Count - sticky, sticky);
        }

        // 固定条下面那截永远不滚：这一帧新露出来的行在它**上面**
        int drop = _stickyBottom > 0 ? Math.Max(_stickyBottom, stuck) : 0;
        int content = rows.Length - Math.Min(drop, rows.Length);
        if (s > content) return null;        // 一次滚了超过一屏：这一帧没有能用的重叠，等下一格

        if (s > 0 && !Full && _rows.Count > 0)
        {
            int from = content - s;
            for (int r = from; r < content; r++)
            {
                // 别处「和上一帧同 y 一模一样」的行 = 浮动的固定条，别接进中间
                if (_stickyBottom > 0 && SameRow(_last[r], rows[r])) continue;
                _rows.Add(rows[r]);
                if (Full) break;
            }
        }

        _last = rows;
        return s;
    }

    /// <summary>
    /// 收尾：把底部固定条补一次到整张图的**最下面**（中间那些已经被丢掉了）。
    /// 幂等：重复调用不会再补一遍。
    /// </summary>
    public void Finish()
    {
        if (_stickyBottom <= 0 || _rows.Count == 0) return;
        if (_last.Length < _stickyBottom) { _stickyBottom = 0; return; }

        int from = _last.Length - _stickyBottom;
        int room = _limit - _rows.Count;
        for (int r = from; r < _last.Length && room > 0; r++, room--) _rows.Add(_last[r]);
        _stickyBottom = 0;
    }

    // ============ 固定条 ============

    /// <summary>尾部「和上一帧同 y 一模一样」的行数（从最下面一行往上数）。</summary>
    private static int StuckTail(byte[][] prev, byte[][] next)
    {
        int count = 0;
        int height = Math.Min(prev.Length, next.Length);
        for (int r = height - 1; r >= 0; r--)
        {
            if (!SameRow(prev[r], next[r])) break;
            count++;
        }
        return count;
    }

    /// <summary>
    /// 这一段尾部算不算底部固定条：够高、不超过屏幕三分之一，而且里面有信息的行足够多
    /// （一整片纯色背景也可能「同 y 一模一样」，那是巧合，不能当固定条剪掉）。
    /// </summary>
    private static bool TryStickyBottom(byte[][] rows, int stuck, out int height)
    {
        height = 0;
        if (stuck < MinStickyRows || stuck > rows.Length / 3) return false;

        int informative = 0;
        for (int r = rows.Length - stuck; r < rows.Length; r++)
            if (IsInformative(rows[r])) informative++;

        if (informative < MinStickyRows) return false;
        height = stuck;
        return true;
    }

    /// <summary>两行像素是不是一样（平均差 ≤ 4/255：视频、光标闪一下也算「没动」）。</summary>
    private static bool SameRow(byte[] a, byte[] b)
    {
        int length = Math.Min(a.Length, b.Length);
        if (length == 0) return true;

        int limit = 4 * length;
        int sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
            if (sum > limit) return false;
        }
        return true;
    }

    /// <summary>这一行有没有信息（灰度跨度够大）；整片纯色的行比了也说明不了什么。</summary>
    private static bool IsInformative(byte[] row)
    {
        int pixels = row.Length / 4;
        if (pixels <= 0) return false;

        int samples = Math.Min(SignatureWidth, pixels);
        int min = 255, max = 0;
        for (int i = 0; i < samples; i++)
        {
            int x = samples == 1 ? 0 : i * (pixels - 1) / (samples - 1);
            int p = x * 4;
            byte gray = (byte)((row[p] * 77 + row[p + 1] * 151 + row[p + 2] * 28) >> 8);
            if (gray < min) min = gray;
            if (gray > max) max = gray;
        }
        return max - min >= FlatSpread;
    }

    // ============ 位移检测 ============

    /// <summary>按行对齐两帧，返回位移（行）；认不出重叠返回 <c>null</c>。</summary>
    /// <remarks>
    /// 两段式：先按抽样行（<see cref="CoarseRows"/>）把所有候选位移粗筛一遍，只留最像的几个去精筛
    /// （整段逐行）；再在「和最好的一样好」的候选里取最小的那个（周期性内容的保守选择）。
    /// </remarks>
    public static int? FindShift(byte[][] prev, byte[][] next)
    {
        if (prev.Length == 0 || next.Length == 0 || prev.Length != next.Length) return null;
        if (prev[0].Length != next[0].Length) return null;

        int height = next.Length;
        int width = next[0].Length / 4;
        if (width < 1) return null;

        var before = Signatures(prev, width);
        var after = Signatures(next, width);

        int top = (int)(height * SkipTopRatio);
        int bottom = height - (int)(height * SkipBottomRatio);
        int maxShift = height - MinOverlap;

        // —— 粗筛：留最像的几个（按位移升序，互不相邻）——
        var candidates = new List<Candidate>(RefineCount);
        for (int shift = 0; shift <= maxShift; shift++)
        {
            double ratio = Ratio(before, after, shift, top, bottom, CoarseRows);
            if (ratio < MinGoodRatio) continue;
            Consider(candidates, shift, ratio);
        }
        if (candidates.Count == 0) return null;

        // —— 精筛：这几个候选按整段逐行重算 ——
        var refined = new List<Candidate>(candidates.Count);
        double best = 0;
        foreach (var c in candidates)
        {
            double ratio = Ratio(before, after, c.Shift, top, bottom, 0);
            refined.Add(new Candidate(c.Shift, ratio));
            best = Math.Max(best, ratio);
        }
        if (best < MinGoodRatio) return null;

        // —— 一样好的候选里取最小的（周期性纹理 / 表格不要一口接一屏）——
        int smallest = int.MaxValue;
        foreach (var c in refined)
            if (c.Ratio >= best - TieRatio) smallest = Math.Min(smallest, c.Shift);

        foreach (var c in candidates)
        {
            if (c.Shift >= smallest) break;
            if (c.Ratio < best - CoarseSlack) continue;        // 粗筛就差远了，不用再试
            if (Ratio(before, after, c.Shift, top, bottom, 0) >= best - TieRatio) return c.Shift;
        }

        return smallest;
    }

    /// <summary>粗筛候选表：按位移升序，最多 <see cref="RefineCount"/> 个、彼此隔开 3 行以上。</summary>
    private static void Consider(List<Candidate> list, int shift, double ratio)
    {
        foreach (var c in list)
        {
            if (Math.Abs(c.Shift - shift) <= 2)
            {
                if (ratio > c.Ratio)   // 相邻的更准：就地替换
                {
                    list.Remove(c);
                    list.Add(new Candidate(shift, ratio));
                    list.Sort((x, y) => x.Shift.CompareTo(y.Shift));
                }
                return;
            }
        }

        if (list.Count < RefineCount)
        {
            list.Add(new Candidate(shift, ratio));
            list.Sort((x, y) => x.Shift.CompareTo(y.Shift));
            return;
        }

        // 满了：替掉最差的那个（分数一样时保留更小的位移）
        int worst = 0;
        for (int i = 1; i < list.Count; i++)
            if (list[i].Ratio < list[worst].Ratio) worst = i;

        if (ratio <= list[worst].Ratio) return;
        list.RemoveAt(worst);
        list.Add(new Candidate(shift, ratio));
        list.Sort((x, y) => x.Shift.CompareTo(y.Shift));
    }

    /// <summary>
    /// 这个位移下「对上的行」占比。<paramref name="maxRows"/> = 0 表示逐行全比，
    /// 否则等距抽这么多个采样点（粗筛用）。
    /// </summary>
    private static double Ratio(Signature[] before, Signature[] after, int shift, int top, int bottom, int maxRows)
    {
        int from = top;
        int to = bottom - shift;          // 这一帧自己底部那截页脚不参与
        int count = to - from;
        if (count < MinOverlap) return 0;

        int step = 1;
        if (maxRows > 0 && count > maxRows) step = (count + maxRows - 1) / maxRows;

        int seen = 0, good = 0;
        for (int r = from; r < to; r += step)
        {
            var a = before[r + shift];
            var b = after[r];
            if (!a.Informative && !b.Informative) continue;   // 两边都是纯色：比了也说明不了什么
            seen++;
            if (Distance(a.Bytes, b.Bytes) <= RowTolerance) good++;
        }

        if (seen < MinOverlap) return 0;
        return (double)good / seen;
    }

    /// <summary>两条签名的平均灰度差；超过容差立刻返回（调用方只关心「够不够近」）。</summary>
    private static int Distance(byte[] a, byte[] b)
    {
        int length = Math.Min(a.Length, b.Length);
        if (length == 0) return 255;

        int limit = RowTolerance * length;
        int sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
            if (sum > limit) return RowTolerance + 1;
        }
        return sum / length;
    }

    /// <summary>整帧的行签名（每行只读一遍，逐位移打分时反复用）。</summary>
    private static Signature[] Signatures(byte[][] rows, int width)
    {
        int samples = Math.Min(SignatureWidth, width);
        var result = new Signature[rows.Length];

        for (int y = 0; y < rows.Length; y++)
        {
            var row = rows[y];
            var bytes = new byte[samples];
            byte min = 255, max = 0;

            for (int i = 0; i < samples; i++)
            {
                int x = samples == 1 ? 0 : i * (width - 1) / (samples - 1);
                int p = x * 4;
                // 0.299 / 0.587 / 0.114 的整数近似
                byte gray = (byte)((row[p] * 77 + row[p + 1] * 151 + row[p + 2] * 28) >> 8);
                bytes[i] = gray;
                if (gray < min) min = gray;
                if (gray > max) max = gray;
            }

            result[y] = new Signature(bytes, max - min >= FlatSpread);
        }

        return result;
    }

    // ============ 输出 ============

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
    /// 末尾 <paramref name="rows"/> 行的位图：就地显示「拼接图最后这一屏」用（每帧只搬这几行，
    /// 不必把上百 MB 的整张长图重画一遍）。
    /// </summary>
    public BitmapSource Tail(int rows)
    {
        if (_rows.Count == 0) throw new InvalidOperationException("还没有拼到内容");

        int count = Math.Clamp(rows, 1, _rows.Count);
        int from = _rows.Count - count;
        var pixels = new byte[_stride * count];

        for (int y = 0; y < count; y++)
            Buffer.BlockCopy(_rows[from + y], 0, pixels, y * _stride, _stride);

        var image = BitmapSource.Create(Width, count, 96, 96, PixelFormats.Bgra32, null, pixels, _stride);
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

    /// <summary>候选位移 + 它当时的分数。</summary>
    private readonly record struct Candidate(int Shift, double Ratio);

    /// <summary>一行的灰度签名，外加「这行有没有信息」（纯色行不参与打分）。</summary>
    private readonly record struct Signature(byte[] Bytes, bool Informative);
}
