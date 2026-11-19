using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// PP-OCRv6 模型文件的位置与下载。
/// </summary>
/// <remarks>
/// <para>
/// 超小档（<c>extra_small</c>）随安装包分发（<c>Assets\Ocr\ppocrv6</c>，约 7.4 MB），开箱即用；
/// 小档（29.8 MB）与中档（约 133 MB）按需下载到数据根下的
/// <c>ocr-models\ppocrv6\small</c> / <c>ocr-models\ppocrv6\medium</c>，
/// 与配置同生命周期——升级解压覆盖程序目录时不会被冲掉。
/// </para>
/// <para>
/// 下载源是 ModelScope 上 RapidOCR 官方的 v3.10.0 模型仓，逐个文件校验长度与 SHA-256，
/// 全部通过才整体替换目标目录，半截文件不会留下可用的假象。
/// 中档与小档共用同一份字典（上游 <c>ppocrv6_dict.txt</c>）。
/// </para>
/// </remarks>
internal static class OcrModelStore
{
    private sealed record ModelFile(string Name, string Url, long Size, string Sha256);

    private const string ModelScope = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0";

    /// <summary>小档三个文件（检测 / 识别 / 字典），与 snow-apps 发布的清单一致。</summary>
    private static readonly ModelFile[] SmallFiles =
    {
        new("det_small.onnx",
            ModelScope + "/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx",
            9_929_594, "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f"),
        new("rec_small.onnx",
            ModelScope + "/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx",
            21_234_383, "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884"),
        new("dict_small.txt",
            ModelScope + "/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt",
            74_947, "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d"),
    };

    /// <summary>中档三个文件（检测 / 识别 / 字典），字典与小档同一份。</summary>
    private static readonly ModelFile[] MediumFiles =
    {
        new("det_medium.onnx",
            ModelScope + "/onnx/PP-OCRv6/det/PP-OCRv6_det_medium.onnx",
            62_119_454, "92078b7355007ccfffcd4c8cd441a3afd4538904d06881b29a155e1e679907c2"),
        new("rec_medium.onnx",
            ModelScope + "/onnx/PP-OCRv6/rec/PP-OCRv6_rec_medium.onnx",
            76_629_984, "eef444829dbbe18d7fea59a3f6eb75647518d2b3a9568d27c92e42940204894b"),
        new("dict_medium.txt",
            ModelScope + "/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt",
            74_947, "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d"),
    };

    /// <summary>随包分发的模型目录（安装包内）。</summary>
    public static string BundledDir => Path.Combine(AppPaths.AppDirectory, "Assets", "Ocr", "ppocrv6");

    /// <summary>按需下载的小档目录（数据根下）。</summary>
    public static string SmallDir => Path.Combine(AppPaths.DataRoot, "ocr-models", "ppocrv6", "small");

    /// <summary>按需下载的中档目录（数据根下）。</summary>
    public static string MediumDir => Path.Combine(AppPaths.DataRoot, "ocr-models", "ppocrv6", "medium");

    /// <summary>方向分类模型：RapidOcrNet 初始化时必须给一个路径（识别时按 <c>DoAngle=false</c> 不执行）。</summary>
    public static string ClsModel => Path.Combine(BundledDir, "textline_cls.onnx");

    /// <summary>超小档是否可用（随包分发）。</summary>
    public static bool TinyReady =>
        File.Exists(Path.Combine(BundledDir, "det_tiny.onnx")) &&
        File.Exists(Path.Combine(BundledDir, "rec_tiny.onnx")) &&
        File.Exists(Path.Combine(BundledDir, "dict_tiny.txt")) &&
        File.Exists(ClsModel);

    /// <summary>小档是否可用（已下载）。</summary>
    public static bool SmallReady => Ready(SmallFiles, SmallDir);

    /// <summary>中档是否可用（已下载）。</summary>
    public static bool MediumReady => Ready(MediumFiles, MediumDir);

    /// <summary>某个档位的模型目录（超小档在安装包内）。</summary>
    public static string DirOf(PaddleOcr.Tier tier) => tier switch
    {
        PaddleOcr.Tier.Tiny => BundledDir,
        PaddleOcr.Tier.Medium => MediumDir,
        _ => SmallDir,
    };

    /// <summary>文件名前缀（<c>det_&lt;前缀&gt;.onnx</c>）。</summary>
    public static string PrefixOf(PaddleOcr.Tier tier) => tier switch
    {
        PaddleOcr.Tier.Tiny => "tiny",
        PaddleOcr.Tier.Medium => "medium",
        _ => "small",
    };

    /// <summary>该档位的文件清单总字节数（进度条用）；超小档随包分发，返回 0。</summary>
    public static long TotalBytes(PaddleOcr.Tier tier) => tier switch
    {
        PaddleOcr.Tier.Tiny => 0,
        PaddleOcr.Tier.Medium => Sum(MediumFiles),
        _ => Sum(SmallFiles),
    };

    /// <summary>该档位的下载文件数（超小档 0）。</summary>
    public static int FileCount(PaddleOcr.Tier tier) => tier switch
    {
        PaddleOcr.Tier.Tiny => 0,
        PaddleOcr.Tier.Medium => MediumFiles.Length,
        _ => SmallFiles.Length,
    };

    /// <summary>该档位能不能下载（超小档随包，不需要下载）。</summary>
    public static bool CanDownload(PaddleOcr.Tier tier) => tier != PaddleOcr.Tier.Tiny;

    /// <summary>下载小档，进度按 0..1 上报；失败或取消时不留下半截文件。</summary>
    public static Task DownloadSmallAsync(IProgress<double>? progress, CancellationToken ct) =>
        DownloadAsync(PaddleOcr.Tier.Small, progress, ct);

    /// <summary>下载某个档位（超小档随包分发，直接成功）。</summary>
    public static async Task DownloadAsync(PaddleOcr.Tier tier, IProgress<double>? progress,
        CancellationToken ct)
    {
        if (!CanDownload(tier)) return;

        ModelFile[] files = tier == PaddleOcr.Tier.Medium ? MediumFiles : SmallFiles;
        string dir = DirOf(tier);
        string staging = dir + ".partial";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LTools/2.4");

            long done = 0, total = Sum(files);
            foreach (ModelFile file in files)
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.Combine(staging, file.Name);
                await DownloadOneAsync(http, file, path, ct, received =>
                    progress?.Report(total == 0 ? 0 : (done + received) / (double)total));
                done += file.Size;
                progress?.Report(done / (double)total);
            }

            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(staging, dir);
            AppLog.Info("OCR", $"PP-OCRv6 模型已下载：{dir}");
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private static bool Ready(ModelFile[] files, string dir)
    {
        foreach (ModelFile file in files)
        {
            var info = new FileInfo(Path.Combine(dir, file.Name));
            if (!info.Exists || info.Length != file.Size) return false;
        }
        return File.Exists(ClsModel);
    }

    private static async Task DownloadOneAsync(
        HttpClient http, ModelFile file, string path, CancellationToken ct, Action<long> onProgress)
    {
        using var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long received = 0;
        await using (Stream source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                onProgress(received);
            }
        }

        var info = new FileInfo(path);
        if (info.Length != file.Size)
            throw new IOException($"{file.Name} 大小不对（应为 {file.Size} 字节，实际 {info.Length} 字节），下载可能被截断。");

        string actual = await HashAsync(path, ct);
        if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{file.Name} 校验失败（SHA-256 不匹配），请重试。");
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { /* 删除失败不掩盖原始异常 */ }
    }

    private static long Sum(ModelFile[] files)
    {
        long total = 0;
        foreach (ModelFile file in files) total += file.Size;
        return total;
    }
}
