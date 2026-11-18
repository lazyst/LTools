using System;
using System.IO;
using System.Linq;
using System.Windows;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 贴图的「批量贴出 / 拖放替换 / 拖出临时文件」纯逻辑（P5-f）。
/// </summary>
/// <remarks>
/// <para>
/// 三条都是纯函数：扩展名表与去重（<see cref="PinFiles"/>）、拖放判定与替换几何
/// （<see cref="PinDropRules"/>）、拖出临时文件的预算与过期（<see cref="PinDragRetention"/>）。
/// 真窗口的拖放 / Ctrl 拖动由仓库外 <c>capture-overlay-smoke</c> 端到端验。
/// </para>
/// <para>
/// 预算与过期照上游 <c>screenshotpinneddragexport.cpp</c> 的 <c>FileRetention</c>：64 个 / 512 MB /
/// 30 分钟宽限 / 删不掉推 60 秒再试；时钟与删除动作可注入，这里直接喂边界值。
/// </para>
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void PinFileCases()
    {
        // ============ 扩展名与文档格式 ============
        Check.True(PinFiles.IsImage("a.PNG"), "大写扩展名也认");
        Check.True(PinFiles.IsImage("a.webp"), "webp 认作图片");
        Check.True(PinFiles.IsImage("a.tiff"), "tiff 认作图片");
        Check.False(PinFiles.IsImage("a.svg"), "svg 不在上游表里");
        Check.False(PinFiles.IsImage("a.txt"), "文本不是图片");

        Check.Equal(PinDocumentFormat.PlainText, PinFiles.DocumentFormatOf("a.txt"), "txt 是纯文本");
        Check.Equal(PinDocumentFormat.PlainText, PinFiles.DocumentFormatOf("a.css"), "css 是纯文本");
        Check.Equal(PinDocumentFormat.Html, PinFiles.DocumentFormatOf("a.html"), "html 是 HTML");
        Check.Equal(PinDocumentFormat.Html, PinFiles.DocumentFormatOf("a.htm"), "htm 是 HTML");
        Check.Equal(PinDocumentFormat.Markdown, PinFiles.DocumentFormatOf("a.md"), "md 是 Markdown");
        Check.Equal(PinDocumentFormat.Markdown, PinFiles.DocumentFormatOf("a.markdown"),
            "markdown 是 Markdown");
        Check.True(PinFiles.DocumentFormatOf("a.png") == null, "图片不是文档");
        Check.False(PinFiles.IsSupported("a.exe"), "exe 不支持");
        Check.True(PinFiles.IsSupported("C:/x/a.png"), "完整路径也认");

        Check.Equal(17, PinFiles.SupportedExtensions.Count, "支持 11 种图片 + 6 种文档");
        Check.Contains(PinFiles.DialogFilter, "*.png", "选择框筛选里有 png");
        Check.Contains(PinFiles.DialogFilter, "*.md", "选择框筛选里有 md");

        // ============ 去重与筛选 ============
        string dir = Path.Combine(Path.GetTempPath(),
            "ltools-selftest-pin-files-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            string png = Path.Combine(dir, "a.png");
            string txt = Path.Combine(dir, "a.txt");
            string exe = Path.Combine(dir, "a.exe");
            string empty = Path.Combine(dir, "zero.png");

            File.WriteAllBytes(png, new byte[] { 1, 2, 3 });
            File.WriteAllText(txt, "hello");
            File.WriteAllBytes(exe, new byte[] { 1 });
            File.WriteAllBytes(empty, Array.Empty<byte>());

            var usable = PinFiles.Filter(new[] { png, png, txt, exe, empty, Path.Combine(dir, "nope.png") },
                out var rejected);

            Check.Equal(2, usable.Count, "重复 / 不支持 / 空文件 / 不存在的都被去掉");
            Check.Equal(png, usable[0], "保持原顺序（图片在前）");
            Check.Equal(txt, usable[1], "文本随后");
            Check.Equal(1, rejected.Count, "不支持的扩展名单独报出来");
            Check.True(rejected[0].EndsWith("a.exe", StringComparison.OrdinalIgnoreCase),
                "被拒的是 exe");

            // 同一路径改写大小 → 身份变了（不会被当成重复）
            string first = PinFiles.Identity(png);
            File.WriteAllBytes(png, new byte[] { 1, 2, 3, 4 });
            Check.NotEqual(first, PinFiles.Identity(png), "文件变大后身份改变");
            Check.Equal(PinFiles.Identity(png.ToUpperInvariant()), PinFiles.Identity(png),
                "路径大小写不影响身份");

            // 超过文本上限的直接丢
            string big = Path.Combine(dir, "big.txt");
            using (var stream = File.Create(big))
                stream.SetLength(PinFiles.MaximumTextBytes + 1);
            var bigOnly = PinFiles.Filter(new[] { big }, out _);
            Check.Equal(0, bigOnly.Count, "超过 1 MB 的文本不贴");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清不掉就算了 */ }
        }

        // ============ 拖放判定 ============
        Check.Equal(PinDropVerdict.Live, PinDropRules.Check(true, false, false, true, true, true, true),
            "实时贴图不收拖放");
        Check.Equal(PinDropVerdict.ClickThrough,
            PinDropRules.Check(false, true, false, true, true, true, true), "穿透态收不到鼠标");
        Check.Equal(PinDropVerdict.Exporting,
            PinDropRules.Check(false, false, true, true, true, true, true), "正在拖出时不收别人的");
        Check.Equal(PinDropVerdict.Empty,
            PinDropRules.Check(false, false, false, true, false, false, false), "文件不支持就是空");
        Check.Equal(PinDropVerdict.Accept,
            PinDropRules.Check(false, false, false, true, false, false, true), "支持的文件收");
        Check.Equal(PinDropVerdict.Accept,
            PinDropRules.Check(false, false, false, false, true, false, false), "位图收");
        Check.Equal(PinDropVerdict.Accept,
            PinDropRules.Check(false, false, false, false, false, true, false), "文本收");
        Check.Equal(PinDropVerdict.Empty,
            PinDropRules.Check(false, false, false, false, false, false, false), "什么都没有就是空");

        // ============ 替换几何 ============
        var desktop = new Rect(0, 0, 1920, 1040);

        var same = PinDropRules.Plan(200, 100, 1, 100, 100, 200, 100, false, false, false, desktop);
        Check.Equal(PinReplaceReject.None, same.Reject, "同尺寸能换");
        Check.False(same.Resized, "同尺寸不动窗口");
        Check.Equal(100.0, same.Left, "同尺寸保留 X");
        Check.Equal(100.0, same.Top, "同尺寸保留 Y");
        Check.Equal(200.0, same.Width, "同尺寸保留宽");

        var bigger = PinDropRules.Plan(400, 200, 1, 100, 100, 200, 100, false, false, false, desktop);
        Check.True(bigger.Resized, "新内容更大要改窗口");
        Check.Equal(0.0, bigger.Left, "按中心向两边长：X 左移一半");
        Check.Equal(50.0, bigger.Top, "按中心向两边长：Y 上移一半");
        Check.Equal(400.0, bigger.Width, "宽度是新的 100% 宽 × 缩放");
        Check.Equal(200.0, bigger.Height, "高度是新的 100% 高 × 缩放");

        var zoomed = PinDropRules.Plan(200, 100, 2, 100, 100, 400, 200, false, false, false, desktop);
        Check.True(zoomed.Resized || Math.Abs(zoomed.Width - 400) < 0.01,
            "缩放 2 倍时新尺寸 = 基准 × 2");
        Check.Equal(400.0, zoomed.Width, "宽度含缩放");

        var clamped = PinDropRules.Plan(400, 200, 1, 3000, 100, 200, 100, false, false, false, desktop);
        Check.True(clamped.Left <= desktop.Right - PinMath.KeepVisible,
            "跑出右边的位置被夹回（至少留 KeepVisible）");

        var locked = PinDropRules.Plan(400, 200, 1, 100, 100, 200, 100, true, false, false, desktop);
        Check.Equal(PinReplaceReject.Locked, locked.Reject, "锁着且尺寸变了拒绝");
        Check.Equal(100.0, locked.Left, "拒绝时几何原样");
        Check.False(locked.Resized, "拒绝时不算改了尺寸");

        var lockedSame = PinDropRules.Plan(200, 100, 1, 100, 100, 200, 100, true, false, false, desktop);
        Check.Equal(PinReplaceReject.None, lockedSame.Reject, "锁着但同尺寸照样能换");

        var thumb = PinDropRules.Plan(400, 200, 1, 100, 100, 83, 83, false, false, true, desktop);
        Check.False(thumb.Resized, "缩略图里不跟内容变尺寸");
        Check.Equal(83.0, thumb.Width, "缩略图保留方块边长");
        Check.Equal(400.0, thumb.BaseWidth, "缩略图只换 100% 基准宽");

        var viewport = PinDropRules.Plan(400, 200, 1, 100, 100, 320, 200, false, true, false, desktop);
        Check.False(viewport.Resized, "取景框不跟内容变尺寸");
        Check.Equal(320.0, viewport.Width, "取景框保留窗口尺寸");

        Check.Contains(PinDropRules.Describe(PinReplaceReject.Live), "实时", "实时拒绝有说明");
        Check.Contains(PinDropRules.Describe(PinReplaceReject.Locked), "解锁", "锁定拒绝有说明");
        Check.Equal("", PinDropRules.Describe(PinReplaceReject.None), "成功没有说明");

        // ============ 拖出临时文件：预算与过期 ============
        long now = 1000;
        var fake = Path.Combine(Path.GetTempPath(), "ltools-selftest-drag-" + Guid.NewGuid().ToString("N")[..8]);

        var budget = new PinDragRetention(() => now, _ => true);
        var lease = budget.Reserve(fake, 100);
        Check.True(lease != null, "没超预算能预约");
        Check.Equal(1, budget.Count, "预约后计数 1");
        Check.Equal(100L, budget.Bytes, "预约后字节数 100");

        budget.Complete(lease, dropped: true);
        budget.Sweep();
        Check.Equal(1, budget.Count, "拖完 30 分钟内不删（接收方还在读）");
        now += PinDragRetention.TransferGraceMilliseconds + 1;
        budget.Sweep();
        Check.Equal(0, budget.Count, "过了宽限期就删");
        Check.Equal(0L, budget.Bytes, "删掉后字节数归零");

        var cancelled = new PinDragRetention(() => now, _ => true);
        var cancelledLease = cancelled.Reserve(fake, 10);
        cancelled.Complete(cancelledLease, dropped: false);
        cancelled.Sweep();
        Check.Equal(0, cancelled.Count, "拖拽取消立刻删，不占额度");

        // 数量上限
        var many = new PinDragRetention(() => now, _ => true);
        for (int i = 0; i < PinDragRetention.MaximumFileCount; i++)
            Check.True(many.Reserve(fake + i, 1) != null, $"第 {i + 1} 个还在上限内");
        Check.True(many.Reserve(fake + "over", 1) == null, "第 65 个被拒");
        Check.Equal(PinDragRetention.MaximumFileCount, many.Count, "上限内都留着");

        // 字节上限
        var huge = new PinDragRetention(() => now, _ => true);
        Check.True(huge.Reserve(fake + "big", PinDragRetention.MaximumBytes) != null,
            "正好 512 MB 能预约");
        Check.True(huge.Reserve(fake + "more", 1) == null, "再来 1 字节就超了");

        // 删不掉：额度继续占着，60 秒后再试
        string real = Path.Combine(Path.GetTempPath(),
            "ltools-selftest-drag-real-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(real);
        try
        {
            bool canRemove = false;
            var blocked = new PinDragRetention(() => now, _ => canRemove);
            var blockedLease = blocked.Reserve(real, 5);
            blocked.Complete(blockedLease, dropped: false);
            blocked.Sweep();
            Check.Equal(1, blocked.Count, "删不掉的目录继续占着额度");

            canRemove = true;
            now += PinDragRetention.RetryMilliseconds + 1;
            blocked.Sweep();
            Check.Equal(0, blocked.Count, "60 秒后重试删除成功");
        }
        finally
        {
            try { Directory.Delete(real, recursive: true); } catch { /* 清不掉就算了 */ }
        }
    }
}
