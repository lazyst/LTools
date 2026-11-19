using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 采集历史里的一张卡片（照上游 <c>HistoryEntryWidget</c>）：日期 / 来源徽标 / 摘要 / 元数据 +
/// 右侧预览图（宽布局 260 × 156）。
/// </summary>
/// <remarks>
/// 卡片上的每一个字都是**算出来的**（<see cref="SummaryText"/> / <see cref="MetaText"/>），
/// 窗口重建列表时只换数据、不拼文案，自检可以直接断言这些字符串。
/// </remarks>
internal sealed class CaptureHistoryRow : INotifyPropertyChanged
{
    /// <summary>预览图尺寸（照上游 <c>kPreviewWidth</c> / <c>kPreviewHeight</c>）。</summary>
    public const int PreviewWidth = 260;

    public const int PreviewHeight = 156;

    private bool _selected;

    public CaptureHistoryRow(CaptureEntry entry, BitmapSource? thumb, long bytes)
    {
        Entry = entry;
        Thumb = thumb;
        Bytes = bytes;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CaptureEntry Entry { get; }

    public BitmapSource? Thumb { get; }

    /// <summary>成品文件（图片 / 视频）的字节数；读不到是 0。</summary>
    public long Bytes { get; }

    public bool HasThumb => Thumb != null;

    /// <summary>勾选框上的时间（照上游 <c>yyyy-MM-dd  HH:mm:ss</c>，中间两个空格）。</summary>
    public string DateText => CaptureHistoryQuery.TryParseTime(Entry.CreatedAt, out var time)
        ? time.ToString("yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture)
        : Entry.CreatedAt;

    /// <summary>来源徽标。</summary>
    public string SourceLabel => CaptureHistorySource.LabelOf(CaptureHistorySource.KeyOf(Entry));

    /// <summary>摘要：<c>1536 × 864 px  ·  1 块屏</c>（录屏换成时长 + 格式）。</summary>
    public string SummaryText
    {
        get
        {
            string size = $"{Entry.Width} × {Entry.Height} px";
            if (Entry.IsRecord)
                return $"{size}  ·  {Entry.DurationText}  ·  {Entry.FormatText}";

            return $"{size}  ·  {Displays} 块屏";
        }
    }

    /// <summary>元数据：<c>位置 0, 0  ·  1.2 MB</c>；没记过落位的写「未记录位置」。</summary>
    public string MetaText
    {
        get
        {
            string where = Entry.SelectionW > 0
                ? $"位置 {Entry.SelectionX}, {Entry.SelectionY}"
                : "未记录位置";

            string bytes = CaptureCatalog.FormatBytes(Bytes);
            return bytes.Length > 0 ? $"{where}  ·  {bytes}" : where;
        }
    }

    /// <summary>这块截图横跨几块屏（老记录没有该字段时按 1 块算）。</summary>
    public int Displays => Entry.Displays > 0 ? Entry.Displays : 1;

    /// <summary>识别出来的文字（卡片上最多两行；完整内容在查看器里）。</summary>
    public bool HasText => !Entry.IsRecord && Entry.Text.Trim().Length > 0;

    public string TextExcerpt => Excerpt(Entry.Text);

    public bool IsRecord => Entry.IsRecord;

    /// <summary>「复制」只复制结果图：录屏没有结果图。</summary>
    public bool CanCopy => !Entry.IsRecord;

    /// <summary>录屏卡片上的按钮是「播放」。</summary>
    public bool CanPlay => Entry.IsRecord;

    /// <summary>预览区没图时的提示。</summary>
    public string PreviewHint => Entry.IsRecord ? "没有封面帧" : "预览不可用";

    /// <summary>卡片勾选状态（「全选本页 / 取消选择」按它同步）。</summary>
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>把识别文字压成两行（换行 / 连续空白都当分隔）。</summary>
    internal static string Excerpt(string? text)
    {
        string flat = string.Join(" ",
            (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));

        return flat.Length <= 90 ? flat : flat[..90] + "…";
    }
}
