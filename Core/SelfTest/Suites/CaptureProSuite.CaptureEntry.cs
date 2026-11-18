using System.Diagnostics;
using System.Threading;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 采集入口（P7-a，照 Snow Shot 的 quick 动作清单）：延时秒数规则、
/// 「延时截图」的可取消等待，以及八个内置采集功能是否都挂上了。
/// </summary>
/// <remarks>
/// 覆盖层里的「选完就做」（<c>PendingAction</c>）要真窗口才看得出工具条摆不摆，
/// 归仓库外 <c>capture-overlay-smoke</c> 的 <c>captureEntry</c> 段；这里只碰纯函数与目录。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void CaptureEntryCases()
    {
        // 秒数：空 / 0 / 负数 = 默认 3；其余夹到 1–10
        Check.Equal(3, CaptureDelay.Normalize(null), "没设置 = 默认 3 秒");
        Check.Equal(3, CaptureDelay.Normalize(0), "0 秒退回默认");
        Check.Equal(3, CaptureDelay.Normalize(-5), "负数退回默认");
        Check.Equal(1, CaptureDelay.Normalize(1), "下限 1 秒");
        Check.Equal(7, CaptureDelay.Normalize(7), "中间值原样");
        Check.Equal(10, CaptureDelay.Normalize(99), "上限 10 秒");
        Check.Equal("5 秒后截图", CaptureDelay.Describe(5), "条目文案");

        // 取消：已经取消的令牌立刻返回 false，绝不能睡满秒数
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            var watch = Stopwatch.StartNew();
            Check.False(CaptureDelay.Wait(10, cts.Token), "取消的令牌 = 不等待");
            Check.True(watch.ElapsedMilliseconds < 500, "取消要立刻返回");
        }

        // 八个采集入口：面板 / 快捷键 / 悬浮里都要能搜到，缺一个就是「功能没挂上」
        var entries = new[]
        {
            ("ltools.captureDelay", "延时截图"),
            ("ltools.captureCopy", "截图并复制"),
            ("ltools.capturePin", "截图并贴图"),
            ("ltools.captureSave", "截图并另存为"),
            ("ltools.captureQuickSave", "截图并快速保存"),
            ("ltools.captureRecognize", "截图并识别"),
            ("ltools.captureScreen", "截取当前显示器"),
            ("ltools.captureWindow", "截取前台窗口"),
        };

        foreach (var (id, name) in entries)
        {
            var def = FeatureCatalog.Find(id);
            Check.True(def != null, $"{id} 已注册");
            if (def == null) continue;

            Check.Equal(name, def.Name, $"{id} 的名称");
            Check.Equal(FeatureGroup.Ltools, def.Group, $"{id} 归「LTools 功能」");
            Check.True(IconCatalog.TryGet(def.Icon, out _), $"{id} 的图标键在目录里");
            Check.True(def.Description.Length > 0, $"{id} 有说明");
        }

        // 引用编码：面板 / 快捷键存的是同一份 id，名字能反查回来
        Check.Equal("截图并贴图", FeatureCatalog.Name("ltools.capturePin"), "按引用反查名称");
    }
}
