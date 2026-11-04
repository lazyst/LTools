using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LTools.Features;

/// <summary>录制宏中的事件类型。</summary>
public enum MacroEventKind : byte
{
    KeyDown,
    KeyUp,
    MouseMove,
    MouseDown,
    MouseUp,
    Wheel,
    HWheel,
}

/// <summary>
/// 录制宏中的单个事件。为控制文件体积，JSON 字段名使用短名（见 <see cref="JsonPropertyNameAttribute"/>）。
/// </summary>
/// <remarks>
/// 用 struct 而不是 class：录制钩子回调里每个鼠标移动都会追加一个事件，
/// 值类型 + 预分配 List 避免高频分配造成 GC 抖动。
/// </remarks>
public struct MacroEvent
{
    /// <summary>相对录制开始的时间（ms）。</summary>
    [JsonPropertyName("t")] public int TimeMs { get; set; }

    /// <summary>事件类型。</summary>
    [JsonPropertyName("k")] public MacroEventKind Kind { get; set; }

    /// <summary>键盘虚拟键码（仅 KeyDown / KeyUp）。</summary>
    [JsonPropertyName("v")] public ushort Vk { get; set; }

    /// <summary>鼠标屏幕物理坐标 X（仅鼠标事件）。</summary>
    [JsonPropertyName("x")] public int X { get; set; }

    /// <summary>鼠标屏幕物理坐标 Y（仅鼠标事件）。</summary>
    [JsonPropertyName("y")] public int Y { get; set; }

    /// <summary>滚轮 delta 或鼠标键号（1=左 2=右 3=中 4=X1 5=X2）。</summary>
    [JsonPropertyName("d")] public int Data { get; set; }

    /// <summary>键盘标志位（<c>LLKHF_EXTENDED</c> = 0x01）。</summary>
    [JsonPropertyName("f")] public uint Flags { get; set; }
}

/// <summary>一个完整的录制宏（事件序列）。存储在 <c>&lt;数据根&gt;/macros/&lt;id&gt;.json</c>。</summary>
public sealed class MacroDto
{
    /// <summary>格式版本，便于以后升级（v2 = 增加虚拟桌面尺寸，用于回放缩放）。</summary>
    [JsonPropertyName("ver")] public int Version { get; set; } = 2;

    /// <summary>稳定 Id。</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>创建时间（ISO 8601）。</summary>
    [JsonPropertyName("created")] public string CreatedAt { get; set; } = "";

    /// <summary>录制时长（ms）。</summary>
    [JsonPropertyName("dur")] public int DurationMs { get; set; }

    /// <summary>录制时虚拟桌面的左 / 上 / 宽 / 高（用于换分辨率回放时按比例缩放松鼠标坐标）。</summary>
    [JsonPropertyName("dl")] public int DesktopLeft { get; set; }
    [JsonPropertyName("dt")] public int DesktopTop { get; set; }
    [JsonPropertyName("dw")] public int DesktopWidth { get; set; }
    [JsonPropertyName("dh")] public int DesktopHeight { get; set; }

    /// <summary>事件序列（按时间升序）。</summary>
    [JsonPropertyName("events")] public List<MacroEvent> Events { get; set; } = new();
}

/// <summary>
/// 宏的元数据（存于 <c>LTools.json</c> 的 <c>Macros.Items</c>），供动作编辑器显示宏信息与回放参数，
/// 避免每次打开都读取体积较大的事件文件。
/// </summary>
public sealed class MacroMeta
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int DurationMs { get; set; }
    public int EventCount { get; set; }
    public long SizeBytes { get; set; }
    public string UpdatedAt { get; set; } = "";

    /// <summary>该宏的回放速度倍率；0 = 跟随全局默认速度。</summary>
    public double Speed { get; set; }

    /// <summary>回放循环次数：null / 0 = 跟随全局默认；-1 = 无限循环；1–99 = 次数。</summary>
    public int? Loops { get; set; }

    /// <summary>回放前倒计时（ms）：null = 跟随全局默认。</summary>
    public int? CountdownMs { get; set; }

    /// <summary>时长显示文本（仅 UI 使用，不落盘）。</summary>
    [JsonIgnore]
    public string DurationText => DurationMs < 1000
        ? $"{DurationMs} ms"
        : $"{DurationMs / 1000.0:0.0} 秒";
}
