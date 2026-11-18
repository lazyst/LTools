namespace LTools.Config;

/// <summary>
/// 拼图的默认设置（<c>configs/settings.json</c> 的 <c>Collage</c> 段）。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>collagesettingsstore</c>：键名与取值形状一一对应，方向 / 尺寸方式存字符串
/// （<c>vertical</c> / <c>horizontal</c> / <c>custom</c> 与 <c>uniform</c> / <c>original</c>），
/// 外边距 / 间距**一律存输出像素**（界面上可以按百分比输入，那只是编辑方式，见
/// <c>CollageWindow</c> 的长度输入），背景色是 <c>#RRGGBBAA</c>。
/// </remarks>
public sealed class CollageFile
{
    /// <summary>拼接方向：<c>vertical</c>（纵向）/ <c>horizontal</c>（横向）/ <c>custom</c>（宫格）。</summary>
    public string Direction { get; set; } = "vertical";

    /// <summary>尺寸方式：<c>uniform</c>（统一尺寸）/ <c>original</c>（原始尺寸）。</summary>
    public string Sizing { get; set; } = "uniform";

    /// <summary>外边距（输出像素）。</summary>
    public int BorderPx { get; set; }

    /// <summary>图片间距（输出像素）。</summary>
    public int SpacingPx { get; set; }

    /// <summary>背景色，<c>#RRGGBBAA</c>。</summary>
    public string Background { get; set; } = "#FFFFFFFF";

    /// <summary>宫格行数。</summary>
    public int Rows { get; set; } = 1;

    /// <summary>宫格列数。</summary>
    public int Columns { get; set; } = 1;

    /// <summary>宫格行列是否已按图片数量自动定过一次（照上游 <c>custom_grid_initialized</c>）。</summary>
    public bool CustomGridInitialized { get; set; }
}
