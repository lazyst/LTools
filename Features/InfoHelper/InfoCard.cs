using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;

namespace LTools.Features.InfoHelper;

/// <summary>「辅助工具」里一张取值卡的类别。</summary>
public enum InfoKind
{
    /// <summary>窗口信息：标题 / 类名 / 进程 / 矩形 / 客户区 / 状态 / 句柄。</summary>
    Window,

    /// <summary>屏幕区域：一块框选矩形的坐标。</summary>
    Region,

    /// <summary>屏幕坐标：一个点。</summary>
    Point,

    /// <summary>屏幕取色：一个点的颜色。</summary>
    Color,

    /// <summary>屏幕环境：虚拟桌面 / 每块显示器 / 光标位置。</summary>
    Screen,

    /// <summary>选中的文件 / 文件夹。</summary>
    Selection,

    /// <summary>截图：落盘的 png 路径 + 截的矩形。</summary>
    Shot,
}

/// <summary>
/// 一张取值卡。<b>只存在内存里</b>：关掉「辅助工具」窗口就没了，不落盘、不进配置、不进备份。
/// </summary>
/// <remarks>
/// <para>
/// <b>一份内容两个出口</b>：卡片正文直接显示 <see cref="Text"/>（一个只读多行文本框，可以自己划选、
/// 只复制挑中的几行），卡片右上角的 ⧉ 复制的也是这同一段文本。所以「看到的」= 「能选中的」= 「复制的」，
/// 别再给正文另写一套渲染（原来那套两列 <c>TextField</c> 已删）。
/// </para>
/// <para>
/// 内容全部由 <see cref="InfoCards"/> 的纯函数造出来（取值只负责采数据），
/// 所以 <c>--selftest</c> 能直接断言每一行文案。
/// </para>
/// </remarks>
public sealed class InfoCard
{
    public InfoKind Kind { get; init; }

    /// <summary>卡片标题（「窗口 · 记事本」「截图 · 框选区域」）。</summary>
    public string Title { get; init; } = "";

    /// <summary>卡片正文，同时是 ⧉ 复制出去的那段文本。</summary>
    public string Text { get; init; } = "";

    /// <summary>截图卡的缩略图（内存位图，不再从磁盘读一次）。</summary>
    public BitmapSource? Preview { get; init; }

    /// <summary>窗口卡的句柄，「再取」时按它刷新；窗口已关闭时为 <see cref="IntPtr.Zero"/>。</summary>
    public IntPtr Handle { get; init; }

    /// <summary>截图卡截的那块矩形（物理像素），「再取」时按同一块重截。</summary>
    public (int X, int Y, int W, int H)? Area { get; init; }

    /// <summary>截图卡的文件绝对路径；删卡片时顺手删掉（只动 <c>cache/info-helper/</c> 下的文件）。</summary>
    public string? FilePath { get; init; }

    /// <summary>截图卡的取图方式（「整个屏幕」/「框选区域」/「窗口「记事本」」），「再取」时沿用。</summary>
    public string How { get; init; } = "";
}

/// <summary>卡片里的取值格式化——<b>纯函数</b>，坐标文案只有这一处实现。</summary>
public static class InfoText
{
    /// <summary>页首与复制文本里统一的单位说明：所有坐标 / 矩形都是虚拟桌面物理像素。</summary>
    public const string Unit = "坐标与矩形都是虚拟桌面物理像素（多屏时可能为负），与截图、找图、鼠标注入同一套坐标。";

    /// <summary>矩形文案：<c>x=100, y=200, w=640, h=480</c>。</summary>
    public static string Rect(int x, int y, int w, int h) => $"x={x}, y={y}, w={w}, h={h}";

    /// <summary>区域参数：动作单元「固定区域」那种 <c>x,y,w,h</c> 逗号串。</summary>
    public static string RegionParams(int x, int y, int w, int h) => $"{x},{y},{w},{h}";

    /// <summary>点文案：<c>x=1234, y=567</c>。</summary>
    public static string Point(int x, int y) => $"x={x}, y={y}";

    /// <summary><c>#RRGGBB</c>（大写）。</summary>
    public static string Hex(int r, int g, int b) => $"#{r:X2}{g:X2}{b:X2}";

    /// <summary>RGB 三元组文案。</summary>
    public static string Rgb(int r, int g, int b) => $"r={r}, g={g}, b={b}";

    /// <summary>把 <c>#RRGGBB</c> 解回三个通道；格式不对返回 false（不猜、不补 0）。</summary>
    public static bool TryRgb(string? hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string text = hex.Trim().TrimStart('#');
        if (text.Length != 6) return false;
        try
        {
            r = Convert.ToInt32(text[..2], 16);
            g = Convert.ToInt32(text.Substring(2, 2), 16);
            b = Convert.ToInt32(text.Substring(4, 2), 16);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>窗口状态（最大化 / 最小化 / 普通）。</summary>
    public static string WindowState(bool zoomed, bool iconic) =>
        iconic ? "最小化" : zoomed ? "最大化" : "普通";

    /// <summary>缩放比例：DPI 96 → <c>100%</c>。</summary>
    public static string Scale(uint dpi) => $"{Math.Round((dpi <= 0 ? 96 : dpi) * 100.0 / 96.0)}%";

    /// <summary>窗口句柄文案（<c>0x00010A2C</c>）。</summary>
    public static string Handle(IntPtr hwnd) => $"0x{hwnd.ToInt64():X}";

    /// <summary>数量文案（<c>3 项</c>）。</summary>
    public static string Count(int n) => $"{n} 项";

    /// <summary>多行拼接（统一用 <c>\n</c>，复制出去不夹 CRLF）。</summary>
    public static string Lines(params string[] lines) => string.Join("\n", lines.Where(l => l.Length > 0));
}

/// <summary>一块显示器（<see cref="InfoCards.Screen"/> 的入参）。</summary>
public sealed record InfoScreen(int Index, bool Primary, uint Dpi,
    int X, int Y, int W, int H,
    int WorkX, int WorkY, int WorkW, int WorkH);

/// <summary>取值卡的唯一构造处——纯函数，不碰 Win32 / COM，取值由 <see cref="InfoGather"/> 负责。</summary>
public static class InfoCards
{
    public static InfoCard Window(string title, string className, string process, IntPtr handle,
        (int X, int Y, int W, int H) bounds, (int X, int Y, int W, int H) client,
        bool zoomed, bool iconic)
    {
        string name = title.Length == 0 ? $"（无标题 · {process}）" : title;
        string state = InfoText.WindowState(zoomed, iconic);
        string rect = InfoText.Rect(bounds.X, bounds.Y, bounds.W, bounds.H);
        string clientText = InfoText.Rect(client.X, client.Y, client.W, client.H);

        return new InfoCard
        {
            Kind = InfoKind.Window,
            Title = $"窗口 · {name}",
            Handle = handle,
            Text = InfoText.Lines(
                "窗口信息（坐标与矩形是虚拟桌面物理像素）",
                $"标题：{name}",
                $"类名：{className}",
                $"进程：{process}",
                $"矩形：{rect}",
                $"客户区：{clientText}",
                $"状态：{state}",
                $"句柄：{InfoText.Handle(handle)}"),
        };
    }

    public static InfoCard Region(int x, int y, int w, int h)
    {
        string rect = InfoText.Rect(x, y, w, h);
        string args = InfoText.RegionParams(x, y, w, h);
        return new InfoCard
        {
            Kind = InfoKind.Region,
            Title = "屏幕区域",
            Area = (x, y, w, h),
            Text = InfoText.Lines(
                "屏幕区域（虚拟桌面物理像素；「区域参数」可直接填进截图 / 找图 / 文字识别的「固定区域」）",
                $"矩形：{rect}",
                $"区域参数：{args}"),
        };
    }

    public static InfoCard Point(int x, int y)
    {
        return new InfoCard
        {
            Kind = InfoKind.Point,
            Title = "屏幕坐标",
            Text = InfoText.Lines(
                "屏幕坐标（虚拟桌面物理像素）",
                InfoText.Point(x, y)),
        };
    }

    public static InfoCard Color(int x, int y, string hex)
    {
        bool known = InfoText.TryRgb(hex, out int r, out int g, out int b);
        string rgb = known ? InfoText.Rgb(r, g, b) : "（无法解析）";
        string normalized = known ? InfoText.Hex(r, g, b) : hex;
        return new InfoCard
        {
            Kind = InfoKind.Color,
            Title = $"屏幕取色 · {normalized}",
            Text = InfoText.Lines(
                "屏幕取色（坐标为虚拟桌面物理像素）",
                $"坐标：{InfoText.Point(x, y)}",
                $"颜色：{normalized}",
                $"RGB：{rgb}"),
        };
    }

    public static InfoCard Screen(int vx, int vy, int vw, int vh,
        IReadOnlyList<InfoScreen> screens, int cursorX, int cursorY)
    {
        var lines = new List<string>
        {
            "屏幕环境（虚拟桌面物理像素）",
            $"虚拟桌面：{InfoText.Rect(vx, vy, vw, vh)}",
            $"光标：{InfoText.Point(cursorX, cursorY)}",
        };

        foreach (InfoScreen s in screens)
        {
            string tag = s.Primary ? "主屏" : "副屏";
            string scale = InfoText.Scale(s.Dpi);
            lines.Add($"显示器 {s.Index}（{tag}，{scale}）：{InfoText.Rect(s.X, s.Y, s.W, s.H)}"
                      + $" · 工作区 {InfoText.Rect(s.WorkX, s.WorkY, s.WorkW, s.WorkH)}");
        }

        return new InfoCard
        {
            Kind = InfoKind.Screen,
            Title = $"屏幕环境 · {screens.Count} 块屏",
            Text = InfoText.Lines(lines.ToArray()),
        };
    }

    public static InfoCard Selection(string folder, IReadOnlyList<string> paths)
    {
        var lines = new List<string> { $"选中的文件（{InfoText.Count(paths.Count)}）" };
        if (folder.Length > 0) lines.Add($"文件夹：{folder}");
        lines.AddRange(paths);

        return new InfoCard
        {
            Kind = InfoKind.Selection,
            Title = $"选中的文件 · {InfoText.Count(paths.Count)}",
            Text = InfoText.Lines(lines.ToArray()),
        };
    }

    public static InfoCard Shot(string how, string file, int x, int y, int w, int h,
        BitmapSource? preview)
    {
        string rect = InfoText.Rect(x, y, w, h);
        return new InfoCard
        {
            Kind = InfoKind.Shot,
            Title = $"截图 · {how}",
            How = how,
            Preview = preview,
            FilePath = file,
            Area = (x, y, w, h),
            Text = InfoText.Lines(
                $"截图（{how}）",
                $"文件：{file}",
                $"区域：{rect}",
                "区域是虚拟桌面物理像素；图片本身按该矩形原分辨率保存。"),
        };
    }
}
