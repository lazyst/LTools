using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using LTools.Core;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 相机快门音（照 Snow Shot 的 <c>presentation/capture/camerashuttersound.cpp</c>）。
/// </summary>
/// <remarks>
/// <para>
/// Windows 上走 MCI：<c>open "…mp3" type mpegvideo alias …</c> → <c>play … from 0</c> →
/// <see cref="ReleaseMs"/> 毫秒后 <c>close</c>（上游用 <c>QTimer::singleShot(5000)</c>，
/// 这里用 <see cref="System.Threading.Timer"/>）。每次播放一个独立别名，连按几下就有几下声音，
/// 不会「上一声还没放完就吞掉这一声」。
/// </para>
/// <para>
/// <b>播放失败只记一条 warning，绝不挡截图</b>：音频文件被删、MCI 不认识 mp3 编码都只影响声音。
/// 调用点两处（与上游两条开关一一对应）：覆盖层开始截图（<see cref="CaptureSoundSettings.Screenshot"/>，
/// 默认关）、直接截前台窗口 / 当前显示器（<see cref="CaptureSoundSettings.Shutter"/>，默认开）。
/// </para>
/// </remarks>
internal static class ShutterSound
{
    /// <summary>随包音频在输出目录里的相对路径（csproj 从 <c>Assets\Audio</c> 复制过去）。</summary>
    public const string RelativePath = @"Assets\Audio\camera_shutter.mp3";

    /// <summary>播放后多久 close（上游 5000ms）。</summary>
    public const int ReleaseMs = 5000;

    internal const string AliasPrefix = "ltools_camera_shutter";

    private static readonly ConcurrentDictionary<int, Timer> Timers = new();

    private static int _sequence;
    private static int _warned;

    /// <summary>音频文件的绝对路径（<c>&lt;exe 目录&gt;\Assets\Audio\camera_shutter.mp3</c>）。</summary>
    public static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "camera_shutter.mp3");

    /// <summary>随包音频在不在。</summary>
    public static bool Available => File.Exists(FilePath);

    /// <summary>界面上的一句说明（设置页 / 自检共用）。</summary>
    public static string Describe() =>
        Available ? "随包快门音已就绪（Assets\\Audio\\camera_shutter.mp3）" : "缺少随包快门音（Assets\\Audio\\camera_shutter.mp3）";

    /// <summary>第 <paramref name="sequence"/> 次播放用的别名（纯函数，自检直接断言）。</summary>
    public static string AliasOf(int sequence) =>
        AliasPrefix + "_" + sequence.ToString(CultureInfo.InvariantCulture);

    /// <summary>MCI 打开命令（纯函数；路径按上游加引号，兼容空格与中文）。</summary>
    public static string OpenCommand(string path, string alias) =>
        $"open \"{path}\" type mpegvideo alias {alias}";

    /// <summary>MCI 播放命令（从 0 开始，重复播不接上一声的尾巴）。</summary>
    public static string PlayCommand(string alias) => $"play {alias} from 0";

    /// <summary>MCI 关闭命令。</summary>
    public static string CloseCommand(string alias) => $"close {alias}";

    /// <summary>
    /// 播一次随包快门音。返回「是否真的发出了播放命令」——调用方不关心失败（只影响声音）。
    /// </summary>
    public static bool Play() => PlayFile(FilePath);

    /// <summary>
    /// 播指定文件：自检用它验证「文件不在时只返回 false、不抛、不挡截图」，正式路径永远走 <see cref="Play"/>。
    /// </summary>
    public static bool PlayFile(string path)
    {
        if (!File.Exists(path))
        {
            WarnOnce("随包快门音不存在：" + path);
            return false;
        }

        int id = Interlocked.Increment(ref _sequence);
        string alias = AliasOf(id);

        try
        {
            int open = Win32.MciSendString(OpenCommand(path, alias), null, 0, IntPtr.Zero);
            if (open != 0)
            {
                WarnOnce($"MCI 打不开快门音（错误 {open}）：{path}");
                return false;
            }

            int play = Win32.MciSendString(PlayCommand(alias), null, 0, IntPtr.Zero);
            if (play != 0)
            {
                Mci(CloseCommand(alias));
                WarnOnce($"MCI 播不出快门音（错误 {play}）：{path}");
                return false;
            }

            ScheduleRelease(id, alias);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce("播放快门音失败：" + ex.Message);
            return false;
        }
    }

    private static void ScheduleRelease(int id, string alias)
    {
        var timer = new Timer(_ => Release(id, alias), null, ReleaseMs, Timeout.Infinite);
        Timers[id] = timer;
    }

    private static void Release(int id, string alias)
    {
        Mci(CloseCommand(alias));

        if (Timers.TryRemove(id, out var timer))
        {
            try { timer.Dispose(); } catch { /* 已经停了 */ }
        }
    }

    private static void Mci(string command)
    {
        try
        {
            Win32.MciSendString(command, null, 0, IntPtr.Zero);
        }
        catch
        {
            // 关闭失败没有补救手段，也没有用户可见影响
        }
    }

    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
            AppLog.Warn("CapturePro.ShutterSound", message);
    }
}
