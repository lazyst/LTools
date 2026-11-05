using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using LTools.Core;
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
/// 引擎按档位懒加载并跨步骤复用；空闲 5 分钟自动卸载，把 ONNX 的原生缓冲还给系统。
/// 识别加锁串行——流程后台线程与编辑器的「测试」可能同时调用。
/// </para>
/// </remarks>
internal static class PaddleOcr
{
    /// <summary>模型档位。</summary>
    public enum Tier
    {
        /// <summary>tiny：6.1 MB 模型，单张约 0.45 秒。</summary>
        Tiny,

        /// <summary>small：29.8 MB 模型，单张约 1.2 秒，基准里全对。</summary>
        Small,
    }

    /// <summary>把一行识别结果判为「可信」的最低置信度（与 snow-apps 的过滤阈值一致）。</summary>
    private const double ConfidentLineScore = 0.5;

    /// <summary>空闲多久后卸载引擎（毫秒）。</summary>
    private const int IdleUnloadMs = 5 * 60_000;

    /// <summary>
    /// 引擎参数：屏幕文字不会倒置，关掉方向分类（省一次模型推理）；要词框，
    /// 「文字点击」靠它算命中文字的位置。
    /// </summary>
    private static readonly RapidOcrOptions Options = RapidOcrOptions.PPOCRv6 with { DoAngle = false, ReturnWordBox = true };

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim Single = new(1, 1);

    private static RapidOcr? _engine;
    private static Tier _tier;
    private static DateTime _lastUseUtc;
    private static Timer? _idleTimer;

    /// <summary>把配置里的引擎名解析成档位；返回 null 表示用系统 OCR。</summary>
    public static Tier? ParseTier(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "v6tiny" or "tiny" => Tier.Tiny,
        "v6small" or "small" => Tier.Small,
        _ => null,
    };

    /// <summary>档位的显示名（日志 / 提示用）。</summary>
    public static string DisplayName(Tier tier) => tier == Tier.Tiny ? "PP-OCRv6 快速" : "PP-OCRv6 高精度";

    /// <summary>该档位的模型是否就位。</summary>
    public static bool IsReady(Tier tier) => tier == Tier.Tiny ? OcrModelStore.TinyReady : OcrModelStore.SmallReady;

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
            var engine = GetEngine(tier);
            _lastUseUtc = DateTime.UtcNow;

            using SKBitmap image = ToSkia(bmp);
            OcrResult result = engine.Detect(image, Options);
            return BuildLines(result);
        }
        catch (FlowException) { throw; }
        catch (Exception ex)
        {
            throw new FlowException($"{DisplayName(tier)} 识别失败：{ex.Message}");
        }
        finally
        {
            Single.Release();
        }
    }

    /// <summary>模型缺失时给用户可执行的下一步，而不是一句「初始化失败」。</summary>
    private static string MissingModelMessage(Tier tier) => tier == Tier.Small
        ? "PP-OCRv6 高精度模型还没下载：在「文字识别」取值器里点「下载模型」，或把引擎改成「系统 OCR」。"
        : "内置的 PP-OCRv6 快速模型缺失，请重新解压完整安装包（Assets\\Ocr\\ppocrv6）。";

    private static RapidOcr GetEngine(Tier tier)
    {
        lock (Gate)
        {
            if (_engine != null && _tier == tier) return _engine;

            _engine?.Dispose();
            _engine = null;

            string dir = tier == Tier.Tiny ? OcrModelStore.BundledDir : OcrModelStore.SmallDir;
            string prefix = tier == Tier.Tiny ? "tiny" : "small";

            var options = new SessionOptions
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
                    options);
            }
            catch
            {
                engine.Dispose();
                throw;
            }

            _engine = engine;
            _tier = tier;
            _lastUseUtc = DateTime.UtcNow;

            _idleTimer ??= new Timer(_ => UnloadIfIdle(), null, IdleUnloadMs, 30_000);
            AppLog.Info("OCR", $"{DisplayName(tier)} 引擎已加载（{dir}）");
            return engine;
        }
    }

    /// <summary>空闲超时卸载；正在识别时跳过，等下一轮定时器。</summary>
    private static void UnloadIfIdle()
    {
        if (!Single.Wait(0)) return;
        try
        {
            lock (Gate)
            {
                if (_engine == null) return;
                if (DateTime.UtcNow - _lastUseUtc < TimeSpan.FromMilliseconds(IdleUnloadMs)) return;

                _engine.Dispose();
                _engine = null;
                _idleTimer?.Dispose();
                _idleTimer = null;
            }

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
