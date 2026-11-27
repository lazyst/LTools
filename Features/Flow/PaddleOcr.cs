using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;
using RapidTextBlock = RapidOcrNet.TextBlock;
using RapidWordBox = RapidOcrNet.WordBox;

namespace LTools.Features;

/// <summary>
/// PP-OCRv6 本地识别引擎（RapidOcrNet + ONNX Runtime），用于「文字识别」里要求更准的场景。
/// </summary>
/// <remarks>
/// <para>
/// 与系统 OCR 互补：Windows.Media.Ocr 零依赖、十几毫秒出结果，但 12–14px 中文、数字与标点常认错；
/// PP-OCRv6 在同图基准里错误率低一个数量级，代价是首次加载约 0.2 秒、单张 0.4–1.3 秒、常驻内存几百 MB。
/// </para>
/// <para>
/// 引擎按档位懒加载并跨步骤复用。常驻策略照上游 <c>text_recognition/resident_process</c>：
/// 常驻（默认）时空闲 5 分钟卸载；关掉时每次识别完立刻卸载。检测缩放 / 检测处理照
/// <c>detector_resize_policy</c> / <c>text_detection_processing</c> 换引擎参数，改设置会让引擎按新参数重建。
/// </para>
/// <para>
/// 识别加锁串行——流程后台线程与编辑器的「测试」可能同时调用。
/// </para>
/// </remarks>
internal static class PaddleOcr
{
    /// <summary>模型档位。</summary>
    public enum Tier
    {
        /// <summary>tiny / extra_small：6.1 MB 模型，单张约 0.45 秒（随包分发）。</summary>
        Tiny,

        /// <summary>small：29.8 MB 模型，单张约 1.2 秒，基准里全对（随包分发，也是默认档）。</summary>
        Small,

        /// <summary>medium：约 133 MB 模型，最准、最慢（按需下载）。</summary>
        Medium,
    }

    /// <summary>把一行识别结果判为「可信」的最低置信度（与 snow-apps 的过滤阈值一致）。</summary>
    private const double ConfidentLineScore = 0.5;

    /// <summary>空闲多久后卸载引擎（毫秒）。</summary>
    private const int IdleUnloadMs = 5 * 60_000;

    /// <summary>
    /// 检测缩放的两种口径（照上游 <c>detector_resize_policy</c>）：
    /// <c>max</c> 限制长边（更快，小字可能漏检）；<c>min</c> 放大短边（更细，慢一些）。
    /// </summary>
    private const int LimitLongSidePixels = 1600;
    private const int UpscaleShortSidePixels = 736;

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim Single = new(1, 1);

    private static RapidOcr? _engine;
    private static Tier _tier;
    private static string _optionsSignature = "";
    private static DateTime _lastUseUtc;
    private static Timer? _idleTimer;

    /// <summary>把配置里的引擎名解析成档位；返回 null 表示用系统 OCR。</summary>
    public static Tier? ParseTier(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "v6tiny" or "tiny" or "extra_small" => Tier.Tiny,
        "v6small" or "small" => Tier.Small,
        "v6medium" or "medium" => Tier.Medium,
        _ => null,
    };

    /// <summary>全局模型档（<c>extra_small</c> / <c>small</c> / <c>medium</c>）→ 档位。</summary>
    public static Tier TierOf(string? modelType) => modelType?.Trim().ToLowerInvariant() switch
    {
        TextRecognitionStore.ModelMedium => Tier.Medium,
        TextRecognitionStore.ModelExtraSmall => Tier.Tiny,
        _ => Tier.Small,
    };

    /// <summary>档位 → 配置里的引擎键（与步骤参数 <c>engine</c> 同一套写法）。</summary>
    public static string EngineKey(Tier tier) => tier switch
    {
        Tier.Tiny => "v6tiny",
        Tier.Medium => "v6medium",
        _ => "v6small",
    };

    /// <summary>档位的显示名（日志 / 提示用）。</summary>
    public static string DisplayName(Tier tier) => tier switch
    {
        Tier.Tiny => "PP-OCRv6 快速",
        Tier.Medium => "PP-OCRv6 最高精度",
        _ => "PP-OCRv6 高精度",
    };

    /// <summary>该档位的模型是否就位。</summary>
    public static bool IsReady(Tier tier) => tier switch
    {
        Tier.Tiny => OcrModelStore.TinyReady,
        Tier.Medium => OcrModelStore.MediumReady,
        _ => OcrModelStore.SmallReady,
    };

    /// <summary>识别一张截图，返回文本 + 置信度（<see cref="VarValue.Score"/> 0..1）。</summary>
    public static VarValue Recognize(Bitmap bmp, Tier tier) => Build(ReadLines(bmp, tier));

    /// <summary>
    /// 识别一张截图，返回带坐标的行 / 词（「文字点击」按词框算点击位置）。
    /// 文本与 <see cref="Recognize"/> 完全一致，只是多带了位置。
    /// </summary>
    public static IReadOnlyList<OcrLineBox> ReadLines(Bitmap bmp, Tier tier)
    {
        if (!IsReady(tier)) throw new FlowException(MissingModelMessage(tier));

        Single.Wait();
        try
        {
            RapidOcrOptions options = BuildOptions();
            var engine = GetEngine(tier, options);
            _lastUseUtc = DateTime.UtcNow;

            using SKBitmap image = ToSkia(bmp);
            OcrResult result = engine.Detect(image, options);
            return BuildLines(result);
        }
        catch (FlowException) { throw; }
        catch (Exception ex)
        {
            throw new FlowException($"{DisplayName(tier)} 识别失败：{ex.Message}");
        }
        finally
        {
            if (!TextRecognitionStore.ResidentProcess) ReleaseIdle();
            Single.Release();
        }
    }

    /// <summary>
    /// 设置变了（模型档 / 检测缩放 / 检测处理 / 常驻 / 热启动）：丢掉旧引擎，下次识别按新参数重建；
    /// 热启动 + 常驻时顺手在后台预热一次。
    /// </summary>
    public static void OnSettingsChanged()
    {
        DisposeEngine();

        if (!TextRecognitionStore.ModelHotStart || !TextRecognitionStore.ResidentProcess) return;

        Tier tier = TierOf(TextRecognitionStore.ModelType);
        if (!IsReady(tier)) return;

        var thread = new Thread(() =>
        {
            try { WarmUp(tier); }
            catch (Exception ex) { AppLog.Warn("OCR", "模型热启动失败：" + ex.Message); }
        })
        { IsBackground = true, Name = "ocr-warmup" };
        thread.Start();
    }

    /// <summary>预加载某个档位的引擎（热启动 / 设置页切档时调）。</summary>
    public static void WarmUp(Tier tier)
    {
        if (!IsReady(tier)) return;

        Single.Wait();
        try
        {
            GetEngine(tier, BuildOptions());
            _lastUseUtc = DateTime.UtcNow;
        }
        finally
        {
            Single.Release();
        }
    }

    /// <summary>引擎参数（照上游两个检测开关）。改设置后由 <see cref="OnSettingsChanged"/> 重建。</summary>
    private static RapidOcrOptions BuildOptions()
    {
        RapidOcrOptions options = RapidOcrOptions.PPOCRv6 with { DoAngle = false, ReturnWordBox = true };

        // 检测缩放：max = 限制长边（ImgResize > 0 走旧式长边封顶）；min = 放大短边（Python 式自适应）
        options = TextRecognitionStore.DetectorResizePolicy == TextRecognitionStore.ResizeMin
            ? options with { ImgResize = 0, LimitSideLen = UpscaleShortSidePixels }
            : options with { ImgResize = LimitLongSidePixels };

        // 检测处理：速度优先关掉宽扁图那层纵向留白（少一次放大重采样，结果可能变）
        if (TextRecognitionStore.DetectionProcessing == TextRecognitionStore.ProcessingSpeedFirst)
            options = options with { WidthHeightRatio = -1, MinHeight = -1 };

        return options;
    }

    /// <summary>引擎参数签名（改了就重建，不必比对每个字段）。</summary>
    private static string OptionsSignature() =>
        TextRecognitionStore.DetectorResizePolicy + "|" + TextRecognitionStore.DetectionProcessing;

    /// <summary>模型缺失时给用户可执行的下一步，而不是一句「初始化失败」。</summary>
    private static string MissingModelMessage(Tier tier) => tier switch
    {
        Tier.Medium =>
            "PP-OCRv6 中档模型还没下载：在「设置 → 通用 → 截图 Pro · 文字识别」里点「下载模型」，或把模型档改成「小 V6」。",
        Tier.Small =>
            "PP-OCRv6 小档模型还没下载：在「设置 → 通用 → 截图 Pro · 文字识别」里点「下载模型」，或把模型档改成「超小 V6」。",
        _ => "内置的 PP-OCRv6 快速模型缺失，请重新解压完整安装包（Assets\\Ocr\\ppocrv6）。",
    };

    private static RapidOcr GetEngine(Tier tier, RapidOcrOptions options)
    {
        string signature = OptionsSignature();

        lock (Gate)
        {
            if (_engine != null && _tier == tier && _optionsSignature == signature) return _engine;

            _engine?.Dispose();
            _engine = null;

            string dir = OcrModelStore.DirOf(tier);
            string prefix = OcrModelStore.PrefixOf(tier);

            var session = new SessionOptions
            {
                // 关掉内存池与预分配：ONNX Runtime 默认会按最大张量长期占着几百 MB，
                // 对常驻托盘程序太重；代价是多 5%–15% 的推理时间（实测 tiny 445→522 ms）。
                EnableCpuMemArena = false,
                EnableMemoryPattern = false,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            var engine = new RapidOcr();
            try
            {
                engine.InitModels(
                    Path.Combine(dir, "det_" + prefix + ".onnx"),
                    OcrModelStore.ClsModel,
                    Path.Combine(dir, "rec_" + prefix + ".onnx"),
                    Path.Combine(dir, "dict_" + prefix + ".txt"),
                    session);
            }
            catch
            {
                engine.Dispose();
                throw;
            }

            _engine = engine;
            _tier = tier;
            _optionsSignature = signature;
            _lastUseUtc = DateTime.UtcNow;

            if (TextRecognitionStore.ResidentProcess)
                _idleTimer ??= new Timer(_ => UnloadIfIdle(), null, IdleUnloadMs, 30_000);

            AppLog.Info("OCR", $"{DisplayName(tier)} 引擎已加载（{dir}，{signature}，选项 {options.GetType().Name}）");
            return engine;
        }
    }

    /// <summary>丢掉引擎（设置变化 / 非常驻识别完）。调用方必须已经拿着 <see cref="Single"/>。</summary>
    private static void ReleaseIdle()
    {
        lock (Gate)
        {
            if (_engine == null) return;
            _engine.Dispose();
            _engine = null;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    private static void DisposeEngine()
    {
        Single.Wait();
        try
        {
            ReleaseIdle();
        }
        finally
        {
            Single.Release();
        }

        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
    }

    /// <summary>空闲超时卸载；正在识别时跳过，等下一轮定时器。</summary>
    private static void UnloadIfIdle()
    {
        if (!Single.Wait(0)) return;
        try
        {
            if (!TextRecognitionStore.ResidentProcess)
            {
                ReleaseIdle();
                return;
            }

            lock (Gate)
            {
                if (_engine == null) return;
                if (DateTime.UtcNow - _lastUseUtc < TimeSpan.FromMilliseconds(IdleUnloadMs)) return;
            }

            ReleaseIdle();
            AppLog.Info("OCR", "OCR 引擎空闲卸载，释放内存");
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }
        finally
        {
            Single.Release();
        }
    }

    /// <summary>把识别结果按行拼成文本，并给出整体置信度。</summary>
    private static VarValue Build(IReadOnlyList<OcrLineBox> lines)
    {
        if (lines.Count == 0) return new VarValue { Text = "", Flag = false, Score = 0 };

        var sb = new StringBuilder();
        double sum = 0;
        bool confident = false;

        foreach (OcrLineBox line in lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.Text);

            sum += line.Score;
            if (line.Confident) confident = true;
        }

        return new VarValue
        {
            Text = sb.ToString(),
            // found 的语义：PP-OCRv6 下指「至少一行识别得够可信」，避免把噪声当成识别成功
            Flag = confident,
            Score = sum / lines.Count,
        };
    }

    /// <summary>把引擎结果转成带坐标的行 / 词；空行丢掉，与文本输出的过滤保持一致。</summary>
    private static List<OcrLineBox> BuildLines(OcrResult result)
    {
        var lines = new List<OcrLineBox>(result.TextBlocks.Length);

        foreach (RapidTextBlock block in result.TextBlocks)
        {
            string text = OcrText.Clean(block.Text ?? "");
            if (text.Length == 0) continue;

            var wordBoxes = new List<OcrWordBox>();
            RapidWordBox[]? boxes = block.WordResults;
            if (boxes != null)
            {
                foreach (RapidWordBox word in boxes)
                {
                    string wt = word.Text ?? "";
                    if (wt.Length == 0) continue;

                    var (wx, wy, ww, wh) = Bounds(word.BoxPoints);
                    if (ww <= 0 || wh <= 0) continue;
                    wordBoxes.Add(new OcrWordBox
                    {
                        Text = wt,
                        X = wx, Y = wy, W = ww, H = wh,
                        Score = word.Score,
                    });
                }
            }

            var (x, y, w, h) = Bounds(block.BoxPoints);
            lines.Add(new OcrLineBox
            {
                Text = text,
                X = x, Y = y, W = w, H = h,
                Score = block.BoxScore,
                Confident = block.BoxScore >= ConfidentLineScore,
                Words = wordBoxes,
            });
        }

        return lines;
    }

    /// <summary>四点框 → 轴对齐矩形（检测框可能带一点旋转，取外接矩形）。</summary>
    private static (int X, int Y, int W, int H) Bounds(SKPointI[]? points)
    {
        if (points == null || points.Length == 0) return (0, 0, 0, 0);

        int left = int.MaxValue, top = int.MaxValue;
        int right = int.MinValue, bottom = int.MinValue;
        foreach (SKPointI p in points)
        {
            left = Math.Min(left, p.X);
            top = Math.Min(top, p.Y);
            right = Math.Max(right, p.X);
            bottom = Math.Max(bottom, p.Y);
        }

        return (left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Bitmap → SkiaSharp。走 BMP 内存流：无损、编码开销远小于 PNG（整屏 1200×800 只要几毫秒），
    /// 也避免自己处理 stride 与像素格式转换。
    /// </summary>
    private static SKBitmap ToSkia(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;
        return SKBitmap.Decode(ms) ?? throw new FlowException("截图数据无法解码。");
    }
}
