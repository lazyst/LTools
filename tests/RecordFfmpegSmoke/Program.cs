using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;

/// <summary>
/// 录屏用 ffmpeg 的冒烟：真跑一遍产品里拼出来的那些命令。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖「录屏主路径 + 后期处理」：rawvideo 管道 → libx264 → mp4、收尾重封装、
/// 两路 WAV → aresample/aformat/amix → AAC、GIF 两遍调色板、APNG / WebP 动图、
/// 进度条（drawbox）、裁剪 + 倍速（setpts / atempo）。
/// </para>
/// <para>
/// 断言只看「命令跑不跑得通、产物在不在」，不比画面：换 ffmpeg 构建、升级版本时
/// 真正要防的是漏了某个编码器 / 封装 / 滤镜（表现为 "Unknown encoder" / "No such filter"）。
/// </para>
/// </remarks>
internal static class Program
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private static Assembly _asm = null!;
    private static string _ff = string.Empty;
    private static int _pass, _fail;

    [STAThread]
    private static int Main()
    {
        _asm = typeof(LTools.Features.Units.Screen.TableUnit).Assembly;

        string dir = Path.Combine(Path.GetTempPath(),
            "ltools-record-ffmpeg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);

        try
        {
            FindFfmpeg();
            if (_ff.Length == 0)
            {
                Console.WriteLine("找不到 ffmpeg：先跑 tools/build-ffmpeg.ps1，或用 LTOOLS_FFMPEG 指定。");
                return 2;
            }

            Console.WriteLine("ffmpeg：" + _ff);
            Console.WriteLine();
            Run(dir);
        }
        catch (Exception ex)
        {
            Check("没有未捕获的异常", false, ex.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录删不掉不影响结论 */ }
        }

        Console.WriteLine();
        Console.WriteLine($"通过 {_pass} / 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ============ 定位与环境 ============

    private static void FindFfmpeg()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("LTOOLS_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) { _ff = fromEnv!; return; }

        string? found = Call("LTools.Features.CapturePro.RecordPlan", "FindFfmpeg") as string;
        if (!string.IsNullOrWhiteSpace(found) && File.Exists(found)) { _ff = found!; return; }

        // 测试工程在 tests\<名字>\bin\Debug\…：往上找仓库根的 runtime\ffmpeg
        string? probe = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && probe != null; i++)
        {
            string candidate = Path.Combine(probe, "runtime", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(candidate)) { _ff = candidate; return; }
            probe = Path.GetDirectoryName(probe.TrimEnd(Path.DirectorySeparatorChar));
        }
    }

    // ============ 用例 ============

    private static void Run(string dir)
    {
        string mp4 = Path.Combine(dir, "capture.mp4");

        // 1) 版本能跑 + 文件不至于又是 100 MB 的完整构建（自编才有意义的那条）
        (int code, string log) = Ffmpeg("-hide_banner -version");
        Check("ffmpeg -version 能跑", code == 0, log);

        long sizeMb = new FileInfo(_ff).Length / (1024 * 1024);
        (_, string conf) = Ffmpeg("-hide_banner -buildconf");
        bool compact = (conf + log).Contains("--disable-everything", StringComparison.Ordinal);
        if (compact) Check($"精简构建体积（{sizeMb} MB < 60 MB）", sizeMb < 60, conf);
        else Console.WriteLine($"提示：这份 ffmpeg 不是精简构建（{sizeMb} MB），跳过体积断言（-buildconf 里没有 --disable-everything）。");

        // 2) 编码器探测：软件两档必须有；硬件那几档只报告，不断言（CI 机器没独显）
        bool x264 = (bool)Call("LTools.Features.CapturePro.RecordEncoders", "CanEncode", _ff, "libx264")!;
        bool x265 = (bool)Call("LTools.Features.CapturePro.RecordEncoders", "CanEncode", _ff, "libx265")!;
        Check("libx264 能编码", x264);
        Check("libx265 能编码", x265);

        object? hw = Call("LTools.Features.CapturePro.RecordEncoders", "Probe", _ff);
        string hwList = hw is System.Collections.IEnumerable items
            ? string.Join(", ", items.Cast<object>().Select(x => x?.ToString()))
            : "?";
        Console.WriteLine($"可用的硬件编码器：{(hwList.Length == 0 ? "（无，按软件编码走）" : hwList)}");
        Check("硬件探测命令本身不报错（-f lavfi … -f null）",
            Ffmpeg("-hide_banner -loglevel error -f lavfi -i color=c=black:s=64x64:r=10:d=0.2 -frames:v 2 -c:v libx264 -f null -").Code == 0);

        // 3) 录屏主路径：rawvideo 管道喂 30 帧 BGRA
        var mp4Args = (IReadOnlyList<string>)Call("LTools.Features.CapturePro.RecordPlan", "Mp4Args",
            new Int32Rect(0, 0, 160, 120), 15, mp4, "libx264", 0, 80, "veryfast")!;
        // 每帧换个颜色：动图（GIF / APNG / WebP）才会真有内容，
        // 纯色源会被编码器去重成单帧，体积断言就变成误报
        byte[] frames = Enumerable.Range(0, 30).SelectMany(i => RawFrame(160, 120, i)).ToArray();
        (code, log) = FfmpegArgs(mp4Args, frames);
        Check("rawvideo 管道 → libx264 → mp4", code == 0 && Bytes(mp4) > 1024, log);

        // 4) 收尾：纯容器搬运（faststart）
        string plain = Path.Combine(dir, "plain.mp4");
        var remux = (IReadOnlyList<string>)Call("LTools.Features.CapturePro.RecordPlan", "FinalizeArgs", mp4, plain)!;
        (code, log) = FfmpegArgs(remux);
        Check("收尾重封装（无音轨）", code == 0 && Bytes(plain) > 1024, log);

        // 5) 两路 WAV → 重采样 + 混音 + AAC
        string wavA = Path.Combine(dir, "system.wav");
        string wavB = Path.Combine(dir, "mic.wav");
        File.WriteAllBytes(wavA, Wav(440, 48000, 1, 500));
        File.WriteAllBytes(wavB, Wav(660, 44100, 1, 500));
        string mixed = Path.Combine(dir, "mixed.mp4");
        var mixArgs = (IReadOnlyList<string>)Call("LTools.Features.CapturePro.RecordPlan", "FinalizeArgs",
            mp4, mixed, new List<string> { wavA, wavB }, null, false)!;
        (code, log) = FfmpegArgs(mixArgs);
        Check("两路 WAV → aresample / aformat / amix → AAC", code == 0 && Bytes(mixed) > 1024, log);
        Check("成品里真有音轨", FfmpegArgs(new[] { "-hide_banner", "-loglevel", "error", "-i", mixed, "-map", "0:a", "-c", "copy", "-f", "null", "-" }).Code == 0);

        // 6) GIF 两遍调色板
        string palette = Path.Combine(dir, "palette.png");
        string gif = Path.Combine(dir, "out.gif");
        var pal = (IReadOnlyList<string>)Call("LTools.Features.CapturePro.RecordPlan", "GifPaletteArgs",
            mp4, palette, 10, 0)!;
        (code, log) = FfmpegArgs(pal);
        Check("palettegen 出调色板", code == 0 && Bytes(palette) > 0, log);

        var gifArgs = (IReadOnlyList<string>)Call("LTools.Features.CapturePro.RecordPlan", "GifEncodeArgs",
            mp4, palette, gif, 10, 0, true)!;
        (code, log) = FfmpegArgs(gifArgs);
        Check("paletteuse → gif", code == 0 && Bytes(gif) > 1024, log);

        // 7) APNG / WebP 动图
        object apng = Enum.Parse(_asm.GetType("LTools.Features.CapturePro.RecordFormat", true)!, "Apng");
        object webp = Enum.Parse(_asm.GetType("LTools.Features.CapturePro.RecordFormat", true)!, "Webp");

        string apngPath = Path.Combine(dir, "out.apng");
        var apngArgs = (IReadOnlyList<string>)Call("LTools.Features.CapturePro.RecordPlan", "AnimatedArgs",
            mp4, apngPath, apng, 10, 0, true)!;
        (code, log) = FfmpegArgs(apngArgs);
        Check("APNG 编码", code == 0 && Bytes(apngPath) > 1024, log);

        string webpPath = Path.Combine(dir, "out.webp");
        var webpArgs = (IReadOnlyList<string>)Call("LTools.Features.CapturePro.RecordPlan", "AnimatedArgs",
            mp4, webpPath, webp, 10, 0, true)!;
        (code, log) = FfmpegArgs(webpArgs);
        Check("WebP 动图编码", code == 0 && Bytes(webpPath) > 1024, log);
        // 不靠 ffmpeg 读回来判（那要额外的 webp 解封装 + 定向解码器，产品用不到）：
        // 直接看容器，RIFF/WEBP 里有 ANIM 块才是真动图，不是一张静态图。
        Check("WebP 是真动图（容器里有 ANIM 块）", IsAnimatedWebp(webpPath), log);

        // 8) 进度条（drawbox，边录边画那一档）
        string bar = Path.Combine(dir, "bar.mp4");
        var barArgs = (IReadOnlyList<string>)Call("LTools.Features.CapturePro.RecordPlan", "ProgressBarArgs",
            mp4, bar, 2000, "#1677FFFF", 80, "veryfast")!;
        (code, log) = FfmpegArgs(barArgs);
        Check("进度条 drawbox", code == 0 && Bytes(bar) > 1024, log);

        // 9) 裁剪 + 倍速：画面 setpts、声音 atempo 串联
        object range = Activator.CreateInstance(
            _asm.GetType("LTools.Features.CapturePro.TrimRange", true)!, 0, 800, 2.0)!;
        string trimmed = Path.Combine(dir, "trim.mp4");
        var trimArgs = (IReadOnlyList<string>)Call("LTools.Features.CapturePro.RecordTrim", "ExportArgs",
            mixed, trimmed, range, true)!;
        (code, log) = FfmpegArgs(trimArgs);
        Check("裁剪 + 2 倍速（setpts / atempo）", code == 0 && Bytes(trimmed) > 1024, log);

        // 10) 残片恢复走的探测：截断一个 mp4，ffmpeg 仍要能读出来（RecordRecovery 就是这么判的）
        string cut = Path.Combine(dir, "cut.mp4");
        byte[] bytes = File.ReadAllBytes(mp4);
        File.WriteAllBytes(cut, bytes.Take(bytes.Length / 2).ToArray());
        (code, log) = FfmpegArgs(new[] { "-hide_banner", "-i", cut });
        Check("残片能被 ffmpeg 识别（恢复链路的前提）", !log.Contains("Permission denied") && log.Contains("Duration", StringComparison.OrdinalIgnoreCase), log);
    }

    // ============ 工具 ============

    private static object? Call(string type, string method, params object?[] args)
    {
        Type? t = _asm.GetType(type, throwOnError: false);
        if (t == null) throw new InvalidOperationException("找不到类型：" + type);

        MethodInfo? m = t.GetMethods(Any)
            .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
        if (m == null) throw new InvalidOperationException($"找不到 {type}.{method}（{args.Length} 参）");

        return m.Invoke(null, args);
    }

    private static (int Code, string Log) Ffmpeg(string line) => FfmpegArgs(SplitArgs(line));

    private static (int Code, string Log) FfmpegArgs(IEnumerable<string> arguments, byte[]? stdin = null)
    {
        var info = new ProcessStartInfo(_ff)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = stdin != null,
        };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);

        using var process = Process.Start(info)!;
        if (stdin != null)
        {
            using Stream input = process.StandardInput.BaseStream;
            input.Write(stdin, 0, stdin.Length);
        }

        string err = process.StandardError.ReadToEnd();
        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(true); } catch { /* 已经退了 */ }
            return (-1, "超时（120 秒）：" + err);
        }

        // 两个流都说不准去哪（-version / -buildconf 的配置行在 stderr，清单在 stdout），一起返回
        return (process.ExitCode, err + output);
    }

    private static IEnumerable<string> SplitArgs(string line)
    {
        var current = new StringBuilder();
        bool quoted = false;

        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }

        if (current.Length > 0) yield return current.ToString();
    }

    private static byte[] RawFrame(int width, int height, int index)
    {
        var frame = new byte[width * height * 4];
        byte b = (byte)(40 + index * 6);
        byte g = (byte)(90 + index * 4);
        byte r = (byte)(160 - index * 3);
        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = b;       // B
            frame[i + 1] = g;   // G
            frame[i + 2] = r;   // R
            frame[i + 3] = 255; // A
        }
        return frame;
    }

    /// <summary>NAudio 那条路产出的是普通 PCM WAV，这里照样造一份（不依赖 ffmpeg 会写 wav）。</summary>
    private static byte[] Wav(double hz, int sampleRate, short channels, int milliseconds)
    {
        int frames = sampleRate * milliseconds / 1000;
        int dataSize = frames * channels * 2;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);           // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        for (int i = 0; i < frames; i++)
        {
            short value = (short)(Math.Sin(2 * Math.PI * hz * i / sampleRate) * 8000);
            for (int c = 0; c < channels; c++) writer.Write(value);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static long Bytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>WebP 是「动图」还是「一张静态图」：看容器里有没有 ANIM / ANMF 块。</summary>
    private static bool IsAnimatedWebp(string path)
    {
        if (!File.Exists(path)) return false;

        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 16) return false;
        if (Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF") return false;
        if (Encoding.ASCII.GetString(bytes, 8, 4) != "WEBP") return false;

        int index = 12;
        while (index + 8 <= bytes.Length)
        {
            string fourcc = Encoding.ASCII.GetString(bytes, index, 4);
            int size = BitConverter.ToInt32(bytes, index + 4);
            if (fourcc is "ANIM" or "ANMF") return true;
            if (size < 0) return false;
            index += 8 + size + (size & 1);
        }

        return false;
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + name); return; }

        _fail++;
        Console.WriteLine("  FAIL  " + name);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            foreach (string line in detail!.Replace("\r", string.Empty).Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6))
                Console.WriteLine("        " + line.Trim());
        }
    }
}
