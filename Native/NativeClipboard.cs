using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Media.Imaging;

namespace LTools.Native;

/// <summary>
/// 原始 Win32 剪贴板读写（文本 / 文件列表 / 图片 / HTML / RTF）。
/// </summary>
/// <remarks>
/// <b>为何不用 WinForms Clipboard（OLE）？</b>——独立剪贴板/符号跳转的工作线程是裸 STA（无消息泵），
/// WinForms Clipboard 的 OLE 读取（<c>OleGetClipboard</c>→<c>GetData</c>）在跨进程复制后调用会
/// <b>长时间阻塞</b>（实测单次 5 秒，因 OLE 跨单元回 marshaling 需要消息泵而工作线程不泵送），
/// 导致 ClipWait 的 500ms 超时形同虚设、整条搜索链超时。
/// 原始 <c>OpenClipboard</c>/<c>GetClipboardData</c> 直接读 Win32 剪贴板层，不经 OLE，<b>不阻塞</b>，
/// 且与目标应用 <c>SetDataObject(text, copy:true)</c> 触发的 <c>OleFlushClipboard</c> 落到 Win32 层的数据一致。
/// </remarks>
/// <remarks>
/// 覆盖 <c>CF_UNICODETEXT</c>（文本）、<c>CF_HDROP</c>（文件列表 + <c>Preferred DropEffect</c> 剪切标记）、
/// <c>CF_DIB</c> / 注册格式 <c>PNG</c>（图片）与注册格式 <c>HTML Format</c> / <c>Rich Text Format</c>。
/// 剪贴板的完整格式备份/恢复（IDataObject）仍走 WinForms OLE——那是每次操作前后各一次，
/// 不在热循环里，且 OLE 的 GetDataObject/SetDataObject 在剪贴板空闲时不会阻塞。
/// </remarks>
internal static class NativeClipboard
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterClipboardFormatW")]
    private static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern int GlobalSize(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint CF_DIBV5 = 17;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;

    // —— 句柄型格式（不是 HGLOBAL 字节，没法按原样存 / 还原）——
    private const uint CF_BITMAP = 2;
    private const uint CF_METAFILEPICT = 3;
    private const uint CF_PALETTE = 9;
    private const uint CF_ENHMETAFILE = 14;
    private const uint CF_OWNERDISPLAY = 0x0080;
    private const uint CF_DSPBITMAP = 0x0082;
    private const uint CF_DSPMETAFILEPICT = 0x0083;
    private const uint CF_DSPENHMETAFILE = 0x008E;

    /// <summary><c>Preferred DropEffect</c>：资源管理器据此判断粘贴是复制还是剪切。</summary>
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;

    /// <summary>剪贴板被别的程序占用时的重试次数 / 间隔。</summary>
    /// <remarks>
    /// 剪贴板是全局独占资源：剪贴板管理器、云同步、截图工具都会在内容变化后立刻打开它。
    /// 写完后马上读（流程里「写入剪贴板」紧跟「取值 · 剪贴板文本」）或读之前正好被抢走，
    /// <c>OpenClipboard</c> 就会返回失败——不重试的话看起来就像内容凭空消失。
    /// 总等待上限约 60ms，远小于钩子 300ms 的约束。
    /// </remarks>
    private const int OpenRetries = 5;
    private const int OpenRetryDelayMs = 15;

    private static uint _pngFormat;
    private static uint _htmlFormat;
    private static uint _rtfFormat;
    private static uint _dropEffectFormat;

    private static uint PngFormat => _pngFormat != 0 ? _pngFormat : _pngFormat = RegisterClipboardFormat("PNG");
    private static uint HtmlFormat => _htmlFormat != 0 ? _htmlFormat : _htmlFormat = RegisterClipboardFormat("HTML Format");
    private static uint RtfFormat => _rtfFormat != 0 ? _rtfFormat : _rtfFormat = RegisterClipboardFormat("Rich Text Format");
    private static uint DropEffectFormat => _dropEffectFormat != 0
        ? _dropEffectFormat
        : _dropEffectFormat = RegisterClipboardFormat("Preferred DropEffect");

    /// <summary>
    /// 「LTools 自己写过剪贴板」的时间戳（<c>Environment.TickCount64</c>）。自动触发的剪贴板
    /// 事件源用它判 <c>ignoreOwnWrites</c>（默认开）：写入后一小段窗口内收到的
    /// <c>WM_CLIPBOARDUPDATE</c> 视为自触发，避免「发送文本 / 粘贴」自己触发自己。
    /// 只在 <see cref="EmptyOwn"/> 里更新，读路径不碰。
    /// </summary>
    private static long _lastOwnWriteTick;
    internal static long LastOwnWriteTick => Interlocked.Read(ref _lastOwnWriteTick);

    /// <summary>清空剪贴板并打一次「自己写过」的标记。所有写路径统一走这里，别再直接调 EmptyClipboard。</summary>
    private static bool EmptyOwn()
    {
        if (!EmptyClipboard()) return false;
        Interlocked.Exchange(ref _lastOwnWriteTick, Environment.TickCount64);
        return true;
    }

    /// <summary>
    /// 剪贴板当前的逻辑格式清单（<c>text</c> / <c>image</c> / <c>files</c>，分号分隔）。
    /// 自动触发的剪贴板事件源用它填 <c>Fields["formats"]</c> 与 <c>triggerClipboardFormats</c>；
    /// HTML / RTF 按 <c>text</c> 归类（§13.5 只认三种），PNG 按 <c>image</c>。
    /// </summary>
    public static string FormatTags()
    {
        if (!OpenWithRetry()) return "";
        try
        {
            bool text = false, image = false, files = false;
            uint fmt = 0;
            while ((fmt = EnumClipboardFormats(fmt)) != 0)
            {
                if (fmt == CF_UNICODETEXT || fmt == 1) text = true;          // CF_UNICODETEXT / CF_TEXT
                else if (fmt == CF_HDROP) files = true;
                else if (fmt is CF_BITMAP or CF_METAFILEPICT or CF_DIB or CF_DIBV5 or CF_ENHMETAFILE
                         or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE) image = true;
                else if (fmt == HtmlFormat || fmt == RtfFormat) text = true;
                else if (fmt == PngFormat) image = true;
            }
            var tags = new List<string>();
            if (text) tags.Add("text");
            if (image) tags.Add("image");
            if (files) tags.Add("files");
            return string.Join(";", tags);
        }
        finally { CloseClipboard(); }
    }

    private static bool OpenWithRetry()
    {
        for (int i = 0; i < OpenRetries; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            if (i < OpenRetries - 1) Thread.Sleep(OpenRetryDelayMs);
        }
        return false;
    }

    /// <summary>清空剪贴板（EmptyClipboard）。返回是否真的清掉了（被别的程序占用时为 false）。</summary>
    public static bool Clear()
    {
        if (!OpenWithRetry()) return false;

        try
        {
            EmptyOwn();
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    // ============ 文本 ============

    /// <summary>读取剪贴板文本。无文本/失败返回 false。</summary>
    public static bool TryGetText(out string text)
    {
        text = "";
        if (!OpenWithRetry()) return false;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return false;
            int len = GlobalSize(h);
            if (len <= 0) return false;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return false;
            try
            {
                int charCount = len / 2;
                var buf = new char[charCount];
                Marshal.Copy(p, buf, 0, charCount);
                int end = Array.IndexOf(buf, '\0');
                text = end < 0 ? new string(buf) : new string(buf, 0, end);
                return true;
            }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>写入文本到剪贴板（CF_UNICODETEXT，立即落 Win32 层）。失败返回 false。</summary>
    public static bool SetText(string text)
    {
        if (!OpenWithRetry()) return false;
        try
        {
            EmptyOwn();
            // 空串只清空
            if (string.IsNullOrEmpty(text)) return true;
            var hMem = AllocText(text);
            if (hMem == IntPtr.Zero) return false;
            // SetClipboardData 接管 hMem 所有权（勿再 Free）
            return SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero;
        }
        finally { CloseClipboard(); }
    }

    // ============ 文件列表（CF_HDROP）============

    /// <summary>读取剪贴板里复制的文件 / 文件夹路径。没有文件列表返回 false。</summary>
    public static bool TryGetFileList(out List<string> files) => TryGetFileList(out files, out _);

    /// <summary>同上，并给出这是「剪切」还是「复制」（来自 <c>Preferred DropEffect</c>）。</summary>
    public static bool TryGetFileList(out List<string> files, out bool cut)
    {
        files = new List<string>();
        cut = false;
        if (!OpenWithRetry()) return false;
        try
        {
            IntPtr h = GetClipboardData(CF_HDROP);
            if (h == IntPtr.Zero) return false;

            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return false;
            try
            {
                int size = GlobalSize(h);
                if (size < 20) return false;

                int offset = Marshal.ReadInt32(p);
                bool wide = Marshal.ReadInt32(p, 16) != 0;
                if (offset <= 0 || offset >= size) return false;

                if (wide)
                {
                    int at = offset;
                    while (at + 1 < size)
                    {
                        int end = at;
                        while (end + 1 < size && Marshal.ReadInt16(p + end) != 0) end += 2;
                        if (end == at) break;
                        files.Add(Marshal.PtrToStringUni(p + at, (end - at) / 2) ?? "");
                        at = end + 2;
                    }
                }
                else
                {
                    int at = offset;
                    var raw = new List<byte>();
                    while (at < size)
                    {
                        byte b = Marshal.ReadByte(p + at++);
                        if (b == 0)
                        {
                            if (raw.Count == 0) break;
                            files.Add(DecodeAnsi(raw.ToArray()));
                            raw.Clear();
                            continue;
                        }
                        raw.Add(b);
                    }
                }
            }
            finally { GlobalUnlock(h); }

            cut = ReadDropEffect() == DropEffectMove;
            return files.Count > 0;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>
    /// 把文件 / 文件夹路径放进剪贴板（CF_HDROP + <c>Preferred DropEffect</c>）。
    /// <paramref name="cut"/> = true 时资源管理器里粘贴是「移动」（剪切）。
    /// </summary>
    public static bool SetFileList(IReadOnlyList<string> paths, bool cut)
    {
        var list = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();
        if (list.Count == 0) return false;

        // DROPFILES 头 20 字节 + 宽字符列表（每项以 \0 结尾，最后再补一个 \0）
        int headerSize = 20;
        int chars = list.Sum(p => p.Length + 1) + 1;
        int bytes = headerSize + chars * 2;

        if (!OpenWithRetry()) return false;
        try
        {
            EmptyOwn();

            IntPtr h = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)bytes);
            if (h == IntPtr.Zero) return false;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero)
            {
                Marshal.FreeHGlobal(h);
                return false;
            }
            try
            {
                Marshal.WriteInt32(p, 0, headerSize);   // pFiles
                Marshal.WriteInt32(p, 4, 0);            // pt.x
                Marshal.WriteInt32(p, 8, 0);            // pt.y
                Marshal.WriteInt32(p, 12, 0);           // fNC
                Marshal.WriteInt32(p, 16, 1);           // fWide

                int at = headerSize;
                foreach (string path in list)
                {
                    Marshal.Copy(path.ToCharArray(), 0, p + at, path.Length);
                    at += path.Length * 2;
                    Marshal.WriteInt16(p + at, 0);
                    at += 2;
                }
                Marshal.WriteInt16(p + at, 0);
            }
            finally { GlobalUnlock(h); }

            if (SetClipboardData(CF_HDROP, h) == IntPtr.Zero) return false;
            WriteDropEffect(cut);
            return true;
        }
        finally { CloseClipboard(); }
    }

    private static int ReadDropEffect()
    {
        IntPtr h = GetClipboardData(DropEffectFormat);
        if (h == IntPtr.Zero) return 0;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return 0;
        try { return GlobalSize(h) >= 4 ? Marshal.ReadInt32(p) : 0; }
        finally { GlobalUnlock(h); }
    }

    private static void WriteDropEffect(bool cut)
    {
        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)4);
        if (h == IntPtr.Zero) return;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(h);
            return;
        }
        try { Marshal.WriteInt32(p, cut ? DropEffectMove : DropEffectCopy); }
        finally { GlobalUnlock(h); }
        SetClipboardData(DropEffectFormat, h);
    }

    private static string DecodeAnsi(byte[] bytes)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int codePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            return Encoding.GetEncoding(codePage).GetString(bytes);
        }
        catch
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // ============ 图片（PNG / CF_DIB）============

    /// <summary>读取剪贴板里的图片（优先注册格式 PNG，退回 CF_DIB / CF_DIBV5）。</summary>
    public static bool TryGetImage(out BitmapSource? image)
    {
        image = null;
        if (!OpenWithRetry()) return false;
        try
        {
            byte[]? png = ReadAll(GetClipboardData(PngFormat));
            if (png != null && TryDecode(png, out image)) return true;

            foreach (uint format in new[] { CF_DIBV5, CF_DIB })
            {
                byte[]? dib = ReadAll(GetClipboardData(format));
                if (dib == null) continue;
                byte[]? bmp = WrapDibAsBmp(dib);
                if (bmp != null && TryDecode(bmp, out image)) return true;
            }

            return false;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>把图片放进剪贴板：注册格式 PNG（现代应用）+ CF_DIB（老应用）。</summary>
    public static bool SetImage(BitmapSource image)
    {
        if (image == null) return false;

        byte[]? png = Encode(image, new PngBitmapEncoder());
        byte[]? dib = EncodeDib(image);

        if (!OpenWithRetry()) return false;
        try
        {
            EmptyOwn();
            bool any = false;
            if (png != null) any |= SetBytes(PngFormat, png);
            if (dib != null) any |= SetBytes(CF_DIB, dib);
            return any;
        }
        finally { CloseClipboard(); }
    }

    private static byte[]? Encode(BitmapSource image, BitmapEncoder encoder)
    {
        try
        {
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Core.CrashLog.Write("NativeClipboard.Encode", ex);
            return null;
        }
    }

    private static byte[]? EncodeDib(BitmapSource image)
    {
        byte[]? bmp = Encode(image, new BmpBitmapEncoder());
        if (bmp == null || bmp.Length <= 14) return null;

        // CF_DIB 是「去掉 BITMAPFILEHEADER 的 BMP」
        var dib = new byte[bmp.Length - 14];
        Buffer.BlockCopy(bmp, 14, dib, 0, dib.Length);
        return dib;
    }

    private static bool TryDecode(byte[] data, out BitmapSource? image)
    {
        image = null;
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            image = frame;
            return true;
        }
        catch (Exception ex)
        {
            Core.CrashLog.Write("NativeClipboard.TryDecode", ex);
            return false;
        }
    }

    /// <summary>把剪贴板里的 CF_DIB 数据补上 BITMAPFILEHEADER，变成能交给 WPF 解码的 BMP。</summary>
    private static byte[]? WrapDibAsBmp(byte[] dib)
    {
        if (dib.Length < 40) return null;

        int headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 12 || headerSize > dib.Length) return null;

        int bitCount = BitConverter.ToInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int colorsUsed = headerSize >= 36 ? BitConverter.ToInt32(dib, 32) : 0;

        int masks = headerSize == 40 && compression == 3 ? 12 : 0;
        int palette = bitCount <= 8 ? (colorsUsed != 0 ? colorsUsed : 1 << bitCount) * 4 : 0;
        int offsetBits = 14 + headerSize + masks + palette;

        int fileSize = 14 + dib.Length;
        var bmp = new byte[fileSize];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(offsetBits).CopyTo(bmp, 10);
        Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
        return bmp;
    }

    // ============ HTML / RTF ============

    /// <summary>读取剪贴板里的 HTML（CF_HTML 的 Fragment 段）。没有返回 false。</summary>
    public static bool TryGetHtml(out string html)
    {
        html = "";
        if (!TryGetBytes(HtmlFormat, out byte[] data)) return false;

        string text = Encoding.UTF8.GetString(data);
        int start = FindOffset(text, "StartFragment:");
        int end = FindOffset(text, "EndFragment:");

        if (start >= 0 && end > start && end <= data.Length)
        {
            html = Encoding.UTF8.GetString(data, start, end - start);
            return true;
        }

        html = text;
        return html.Length > 0;
    }

    /// <summary>写入 HTML 到剪贴板（按 CF_HTML 规范生成头部偏移）。</summary>
    public static bool SetHtml(string html)
    {
        html ??= "";

        const string template =
            "Version:0.9\r\n" +
            "StartHTML:{0:D10}\r\n" +
            "EndHTML:{1:D10}\r\n" +
            "StartFragment:{2:D10}\r\n" +
            "EndFragment:{3:D10}\r\n";

        const string pre = "<html><body><!--StartFragment-->";
        const string post = "<!--EndFragment--></body></html>";

        int headerBytes = Encoding.UTF8.GetByteCount(string.Format(template, 0, 0, 0, 0));
        int startFragment = headerBytes + Encoding.UTF8.GetByteCount(pre);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(html);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(post);

        string payload = string.Format(template, headerBytes, endHtml, startFragment, endFragment)
                       + pre + html + post;

        return SetSingleFormat(HtmlFormat, Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>读取剪贴板里的 RTF。没有返回 false。</summary>
    public static bool TryGetRtf(out string rtf)
    {
        rtf = "";
        if (!TryGetBytes(RtfFormat, out byte[] data)) return false;
        rtf = Encoding.UTF8.GetString(data).TrimEnd('\0');
        return rtf.Length > 0;
    }

    /// <summary>写入 RTF 到剪贴板。</summary>
    public static bool SetRtf(string rtf) =>
        SetSingleFormat(RtfFormat, Encoding.UTF8.GetBytes((rtf ?? "") + "\0"));

    private static int FindOffset(string text, string name)
    {
        int at = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return -1;
        at += name.Length;

        // 跳过空白，读连续数字（标准写法是 10 位补零，但不强求）
        while (at < text.Length && (text[at] == ' ' || text[at] == '\t')) at++;
        int start = at;
        while (at < text.Length && char.IsAsciiDigit(text[at])) at++;
        if (at == start) return -1;

        return int.TryParse(text.AsSpan(start, at - start), out int value) ? value : -1;
    }

    // ============ 原始字节 ============

    private static bool TryGetBytes(uint format, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (!OpenWithRetry()) return false;
        try
        {
            byte[]? bytes = ReadAll(GetClipboardData(format));
            if (bytes == null) return false;
            data = bytes;
            return true;
        }
        finally { CloseClipboard(); }
    }

    private static bool SetBytes(uint format, byte[] data)
    {
        if (data.Length == 0) return false;
        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (h == IntPtr.Zero) return false;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero)
        {
            // 锁不住就没人接管这块内存，只能自己释放
            Marshal.FreeHGlobal(h);
            return false;
        }
        try { Marshal.Copy(data, 0, p, data.Length); }
        finally { GlobalUnlock(h); }
        return SetClipboardData(format, h) != IntPtr.Zero;
    }

    /// <summary>整块替换剪贴板内容为单一格式（HTML / RTF 用）。</summary>
    private static bool SetSingleFormat(uint format, byte[] data)
    {
        if (!OpenWithRetry()) return false;
        try
        {
            EmptyOwn();
            return SetBytes(format, data);
        }
        finally { CloseClipboard(); }
    }

    private static byte[]? ReadAll(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        int size = GlobalSize(h);
        if (size <= 0) return null;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var buffer = new byte[size];
            Marshal.Copy(p, buffer, 0, size);
            return buffer;
        }
        finally { GlobalUnlock(h); }
    }

    private static IntPtr AllocText(string text)
    {
        int bytes = (text.Length + 1) * 2; // 含终止符
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
        if (hMem == IntPtr.Zero) return IntPtr.Zero;
        var p = GlobalLock(hMem);
        if (p == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(hMem);
            return IntPtr.Zero;
        }
        try
        {
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            // GMEM_MOVEABLE 不保证零初始化，显式写终止符
            Marshal.WriteInt16(p + text.Length * 2, 0);
        }
        finally { GlobalUnlock(hMem); }
        return hMem;
    }

    // ============ 整块快照 / 还原 ============

    /// <summary>剪贴板里的一种格式：格式号 + Win32 层的原始字节。</summary>
    internal sealed class ClipboardFormat
    {
        public required uint Format { get; init; }
        public required byte[] Data { get; init; }
    }

    /// <summary>
    /// 剪贴板整块快照：粘贴前 <see cref="Capture"/>、粘贴后 <see cref="Restore"/>，
    /// 用户原来复制的图片 / 文件列表 / HTML 不会因为一次粘贴就没了。
    /// </summary>
    /// <remarks>
    /// 只按「格式号 + 原始字节」存，不做语义解析——同一个格式号写回去就是原样，
    /// 新出现的注册格式（浏览器 / 编辑器自定义的）也不用逐个适配。
    /// 位图 / 元文件这类**句柄型**格式存不了，见 <see cref="IsByteFormat"/>。
    /// </remarks>
    internal sealed class ClipboardSnapshot
    {
        public List<ClipboardFormat> Formats { get; } = new();
        public bool IsEmpty => Formats.Count == 0;
    }

    /// <summary>把剪贴板当前所有能按字节保存的格式整块读下来。剪贴板被占用时返回 null。</summary>
    public static ClipboardSnapshot? Capture()
    {
        if (!OpenWithRetry()) return null;

        try
        {
            var snapshot = new ClipboardSnapshot();
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                if (!IsByteFormat(format)) continue;
                byte[]? data = ReadAll(GetClipboardData(format));
                if (data == null || data.Length == 0) continue;
                snapshot.Formats.Add(new ClipboardFormat { Format = format, Data = data });
            }
            return snapshot;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>把快照整块写回剪贴板（先清空）。空快照 = 恢复成「剪贴板为空」。</summary>
    public static bool Restore(ClipboardSnapshot snapshot)
    {
        if (!OpenWithRetry()) return false;

        try
        {
            EmptyOwn();
            if (snapshot.IsEmpty) return true;

            bool ok = true;
            foreach (var item in snapshot.Formats)
                if (!SetBytes(item.Format, item.Data)) ok = false;
            return ok;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>
    /// 只有 HGLOBAL 字节格式能按原样存回；位图 / 元文件 / 调色板是 GDI 句柄，
    /// owner-draw 格式只有拥有它的窗口能解释，都跳过（还原后这些格式会丢，其余格式照常）。
    /// </summary>
    private static bool IsByteFormat(uint format) => format switch
    {
        CF_BITMAP or CF_METAFILEPICT or CF_PALETTE or CF_ENHMETAFILE => false,
        CF_OWNERDISPLAY or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE => false,
        >= 0x0300 and <= 0x03FF => false,   // GDI 对象句柄区间
        _ => true,
    };
}
