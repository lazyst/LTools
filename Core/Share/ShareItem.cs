using System.IO;
using System.Text.Json.Serialization;

namespace LTools.Core.Share;

/// <summary>
/// 便携清单里的一条：本机发出去的内容。**不复制文件**，只记路径。
/// </summary>
/// <remarks>
/// 原文件被移动 / 删除 / 改名后 <see cref="Exists"/> 为假，界面标「已失效」，
/// 下载返回 404——这是「原地共享」的代价，不写迁移器也不偷偷复制。
/// </remarks>
internal sealed class ShareItem
{
    /// <summary>8 位十六进制，来自 <see cref="Core.IdFactory.ShortId(int)"/>；URL 与清单都用它。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名（清单内去重，可带 <c> (2)</c>）。</summary>
    public string Name { get; set; } = "";

    /// <summary>绝对路径；<see cref="Copied"/> 为真时指向 <c>share/files/</c> 下的副本。</summary>
    public string Path { get; set; } = "";

    public long Size { get; set; }

    public string Mime { get; set; } = "";

    public DateTime AddedAt { get; set; }

    /// <summary>内容是我们写出来的（剪贴板文字 / 图片 / 截图），删除时要连文件一起删。</summary>
    public bool Copied { get; set; }

    /// <summary>原文件还在不在；不落盘，读清单时现算。</summary>
    [JsonIgnore]
    public bool Exists => !string.IsNullOrEmpty(Path) && File.Exists(Path);
}

/// <summary>
/// <c>share/inbox/</c> 里的一个文件（对方发来的）。目录即真相，不写清单。
/// </summary>
internal sealed record ShareInboxFile(string Name, long Size, DateTime Modified, string Mime);
