using System;
using System.Globalization;
using System.IO;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴到重复内容时做什么（照上游 <c>pin_to_screen/duplicate_content_action</c>）。
/// </summary>
internal enum PinDuplicateAction
{
    /// <summary>什么都不贴（已有的那张就是用户要看的）。</summary>
    None,

    /// <summary>抖一下已经贴着的那张（没有就贴回最近关掉的那张再抖）。</summary>
    ShakeWindow,

    /// <summary>贴回最近关掉的那张（不新建）。</summary>
    RestoreLastClosedWindow,

    /// <summary>照常再贴一张（允许同一内容贴很多张）。</summary>
    RepeatAction,
}

/// <summary>一次「贴图请求」被重复内容规则处理掉没有。</summary>
internal enum PinDuplicateOutcome
{
    /// <summary>没有重复（或规则允许再贴）：调用方照常建窗口。</summary>
    Proceed,

    /// <summary>已经被规则处理（抖动 / 贴回 / 不贴）：调用方不要再建窗口。</summary>
    Handled,
}

/// <summary>
/// 一条贴图内容的身份（照上游 <c>PinnedSourceIdentity</c>）：同一个文件 / 同一次复制。
/// </summary>
/// <remarks>
/// <para>
/// 上游的键只有一种形状：**文件**是规范化后的原始路径（不是仓库里的副本），
/// **剪贴板**是「进程会话令牌 + 剪贴板序号」。本类照这两条生成同样的键，
/// 用来判断「这次要贴的内容是不是已经贴过了」（<see cref="CapturePinManager.HandleDuplicate"/>）。
/// </para>
/// <para>
/// 会话令牌是进程内的一个随机串：剪贴板身份只在这一次运行里成立——重启后剪贴板里还是同一份内容，
/// 也不该把上次运行贴的那张当成「重复」（那些窗口早就没了）。
/// 剪贴板序号来自 <c>GetClipboardSequenceNumber</c>：复制一次（哪怕内容一样）序号就变，
/// 所以「再复制一次同样的图再贴」是允许的。
/// </para>
/// </remarks>
internal static class PinContentIdentity
{
    public const string FilePrefix = "file:";
    public const string ClipboardPrefix = "clip:";

    /// <summary>本进程的会话令牌（8 位十六进制，够区分这一次运行）。</summary>
    private static readonly string Session = Guid.NewGuid().ToString("N")[..8];

    /// <summary>这个键算不算「有身份」（老记录 / 截图 / 历史贴图都是空串）。</summary>
    public static bool IsValid(string? key) => !string.IsNullOrWhiteSpace(key);

    /// <summary>
    /// 文件身份：规范化后的绝对路径，Windows 下大小写不敏感。
    /// 空路径 / 非法路径返回空串（调用方按「没有身份」处理）。
    /// </summary>
    public static string FileKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        try
        {
            string full = Path.GetFullPath(path.Trim());
            return FilePrefix + full.Replace('/', '\\').ToLowerInvariant();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>剪贴板身份：会话令牌 + 剪贴板序号（<c>GetClipboardSequenceNumber</c>）。</summary>
    public static string ClipboardKey(uint sequence) =>
        ClipboardPrefix + Session + ":" + sequence.ToString(CultureInfo.InvariantCulture);

    /// <summary>这个键是不是剪贴板身份（调试 / 冒烟用）。</summary>
    public static bool IsClipboardKey(string? key) =>
        key != null && key.StartsWith(ClipboardPrefix, StringComparison.Ordinal);

    /// <summary>这个键是不是文件身份（调试 / 冒烟用）。</summary>
    public static bool IsFileKey(string? key) =>
        key != null && key.StartsWith(FilePrefix, StringComparison.Ordinal);
}
