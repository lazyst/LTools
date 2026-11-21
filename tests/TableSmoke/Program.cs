using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

/// <summary>
/// 表格识别冒烟：GDI+ 画三张表（带框线 / 无线框 / 合并表头）→ 真跑线检测 + OCR，
/// 断言结构（行列数、合并、是不是按线还原）与导出格式。
/// </summary>
/// <remarks>
/// 不比对具体文字：OCR 结果随机器 / 模型略有差异，这里只验「格子分对了没有」。
/// </remarks>
internal static class Program
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private static int _pass, _fail;
    private static Type _reader = null!;

    [STAThread]
    private static int Main()
    {
        var lt = typeof(LTools.Features.Units.Screen.TableUnit).Assembly;
        _reader = lt.GetType("LTools.Features.CapturePro.TableReader", true)!;

        string dir = Path.Combine(Path.GetTempPath(), "ltools-table-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            Run(dir);
        }
        catch (Exception ex)
        {
            Check("未抛异常", false, ex.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录 */ }
        }

        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static void Run(string dir)
    {
        using Bitmap framed = Framed(dir);
        using Bitmap frameless = Frameless(dir);
        using Bitmap merged = Merged(dir);

        // ---- ① 带框线：按线还原 3 行 3 列 ----
        object auto = Read(framed, "auto");
        Check("带框线 + 自动：走线检测", Bool(auto, "FromLines"));
        Check("带框线：3 行", Int(auto, "RowCount") == 3, $"实际 {Int(auto, "RowCount")}");
        Check("带框线：3 列", Int(auto, "ColumnCount") == 3, $"实际 {Int(auto, "ColumnCount")}");
        Check("带框线：认得表格", Bool(auto, "Found"));
        Check("带框线：多数格子有字", Filled(auto) >= 6, $"有字格子 {Filled(auto)}");
        Check("带框线：没有合并", MergeCount(auto) == 0, $"合并 {MergeCount(auto)}");

        object lines = Read(framed, "lines");
        Check("带框线 + 只认线：仍是 3×3", Bool(lines, "FromLines") && Int(lines, "RowCount") == 3);

        object cluster = Read(framed, "cluster");
        Check("带框线 + 只按文字：不走线检测", !Bool(cluster, "FromLines"));
        Check("带框线 + 只按文字：也能聚成 3×3",
            Int(cluster, "RowCount") == 3 && Int(cluster, "ColumnCount") == 3,
            $"实际 {Int(cluster, "RowCount")}×{Int(cluster, "ColumnCount")}");

        // ---- ② 无线框：strict 找不到，auto 退回聚类 ----
        object strict = Read(frameless, "lines");
        Check("无线框 + 只认线：没有表格", !Bool(strict, "Found"));

        object auto2 = Read(frameless, "auto");
        Check("无线框 + 自动：退回聚类", !Bool(auto2, "FromLines"));
        Check("无线框：聚成 3 行 2 列",
            Int(auto2, "RowCount") == 3 && Int(auto2, "ColumnCount") == 2,
            $"实际 {Int(auto2, "RowCount")}×{Int(auto2, "ColumnCount")}");
        Check("无线框：认得表格", Bool(auto2, "Found"));

        // ---- ③ 合并表头：跨两列只填左上角 ----
        object mergedResult = Read(merged, "auto");
        Check("合并表：走线检测", Bool(mergedResult, "FromLines"));
        Check("合并表：2 行 3 列",
            Int(mergedResult, "RowCount") == 2 && Int(mergedResult, "ColumnCount") == 3,
            $"实际 {Int(mergedResult, "RowCount")}×{Int(mergedResult, "ColumnCount")}");
        Check("合并表：一处合并（第 0 行跨 2 列）", MergeCount(mergedResult) == 1 &&
            string.Join(",", Merges(mergedResult)) == "0,0,1,2",
            string.Join(" / ", Merges(mergedResult)));
        Check("合并表：被盖住的格子不是锚点（rows 里留空）", !IsAnchor(mergedResult, 0, 1),
            $"IsAnchor(0,1)={IsAnchor(mergedResult, 0, 1)}");
        Check("合并表：锚点有字", CellText(mergedResult, 0, 0).Length > 0);

        // ---- ④ 导出格式 ----
        object table = Prop(auto, "Table")!;
        string tsv = (string)Call(table, "ToTsv")!;
        Check("TSV：3 行", tsv.Split('\n').Length == 3, $"实际 {tsv.Split('\n').Length}");
        Check("TSV：每行 2 个制表符", tsv.Split('\n').All(line => line.Count(c => c == '\t') == 2));

        string csv = (string)Call(table, "ToCsv")!;
        Check("CSV：CRLF 分行 3 行", csv.Split("\r\n").Length == 3);

        string md = (string)Call(table, "ToMarkdown")!;
        Check("Markdown：表头 + 分隔行 + 2 行数据", md.Split('\n').Length == 4, $"实际 {md.Split('\n').Length}");

        string json = (string)Call(table, "ToJson")!;
        using JsonDocument doc = JsonDocument.Parse(json);
        Check("JSON：3 行 × 3 列",
            doc.RootElement.GetArrayLength() == 3 && doc.RootElement[0].GetArrayLength() == 3);

        // ---- ⑤ 结果留一份 png 到临时目录，方便人眼对照 ----
        framed.Save(Path.Combine(dir, "framed.png"), ImageFormat.Png);
    }

    // ============ 画图 ============

    private static readonly int[] Xs = { 20, 180, 340, 500 };

    private static Font Font() => new("Arial", 18, FontStyle.Regular, GraphicsUnit.Pixel);

    private static Bitmap Framed(string dir)
    {
        int[] ys = { 20, 120, 220, 320 };
        var bmp = new Bitmap(600, 400);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.None;

        using var pen = new Pen(Color.Black, 2);
        foreach (int x in Xs) g.DrawLine(pen, x, ys[0], x, ys[^1]);
        foreach (int y in ys) g.DrawLine(pen, Xs[0], y, Xs[^1], y);

        using Font font = Font();
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
                DrawCell(g, font, Xs[column], ys[row], Xs[column + 1], ys[row + 1], $"R{row + 1}C{column + 1}");
        }

        return bmp;
    }

    private static Bitmap Frameless(string dir)
    {
        var bmp = new Bitmap(600, 400);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);

        using Font font = Font();
        string[] left = { "Apple", "Banana", "Cherry" };
        string[] right = { "10", "20", "30" };
        for (int row = 0; row < 3; row++)
        {
            int y = 60 + row * 100;
            g.DrawString(left[row], font, Brushes.Black, 60, y);
            g.DrawString(right[row], font, Brushes.Black, 320, y);
        }

        return bmp;
    }

    private static Bitmap Merged(string dir)
    {
        int[] ys = { 20, 120, 220 };
        var bmp = new Bitmap(600, 300);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.None;

        using var pen = new Pen(Color.Black, 2);
        foreach (int y in ys) g.DrawLine(pen, Xs[0], y, Xs[^1], y);

        // 表头两列合并：第 0 行那段竖线不画
        g.DrawLine(pen, Xs[0], ys[0], Xs[0], ys[^1]);
        g.DrawLine(pen, Xs[1], ys[1], Xs[1], ys[^1]);
        g.DrawLine(pen, Xs[2], ys[0], Xs[2], ys[^1]);
        g.DrawLine(pen, Xs[3], ys[0], Xs[3], ys[^1]);

        using Font font = Font();
        DrawCell(g, font, Xs[0], ys[0], Xs[2], ys[1], "TOTAL");
        DrawCell(g, font, Xs[2], ys[0], Xs[3], ys[1], "AMOUNT");
        DrawCell(g, font, Xs[0], ys[1], Xs[1], ys[2], "Widget");
        DrawCell(g, font, Xs[1], ys[1], Xs[2], ys[2], "42");
        DrawCell(g, font, Xs[2], ys[1], Xs[3], ys[2], "99");

        return bmp;
    }

    private static void DrawCell(Graphics g, Font font, int left, int top, int right, int bottom, string text)
    {
        var rect = new RectangleF(left + 4, top + 4, right - left - 8, bottom - top - 8);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, font, Brushes.Black, rect, format);
    }

    // ============ 反射小工具 ============

    private static object Read(Bitmap bitmap, string detect) =>
        _reader.GetMethod("Read", Any)!.Invoke(null, new object?[] { bitmap, "v6tiny", detect })!;

    private static object Prop(object target, string name) =>
        target.GetType().GetProperty(name, Any)!.GetValue(target)!;

    private static int Int(object result, string name) => (int)Prop(Prop(result, "Table")!, name)!;

    private static bool Bool(object target, string name) => (bool)target.GetType().GetProperty(name, Any)!.GetValue(target)!;

    private static object? Call(object target, string name) =>
        target.GetType().GetMethod(name, Any, Type.EmptyTypes)?.Invoke(target, null);

    /// <summary>这个格子是不是合并格的锚点（动作单元的 <c>rows</c> 只写锚点，其余留空）。</summary>
    private static bool IsAnchor(object result, int row, int column)
    {
        object table = Prop(result, "Table")!;
        return (bool)table.GetType().GetMethod("IsAnchor", Any)!
            .Invoke(table, new object[] { row, column })!;
    }

    private static string CellText(object result, int row, int column)
    {
        object table = Prop(result, "Table")!;
        return (string)table.GetType().GetMethod("CellText", Any)!
            .Invoke(table, new object[] { row, column })!;
    }

    private static int Filled(object result)
    {
        object table = Prop(result, "Table")!;
        int rows = (int)Prop(table, "RowCount")!, columns = (int)Prop(table, "ColumnCount")!;
        int filled = 0;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
                if (CellText(result, row, column).Trim().Length > 0) filled++;
        }
        return filled;
    }

    private static int MergeCount(object result) => Merges(result).Length;

    /// <summary>合并单元格的 "row,col,rowSpan,colSpan" 列表（从各锚点的 SpanRangeAt 读）。</summary>
    private static string[] Merges(object result)
    {
        object table = Prop(result, "Table")!;
        int rows = (int)Prop(table, "RowCount")!, columns = (int)Prop(table, "ColumnCount")!;
        var found = new System.Collections.Generic.List<string>();
        var spanAt = table.GetType().GetMethod("SpanRangeAt", Any)!;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                object span = spanAt.Invoke(table, new object[] { row, column })!;
                int rowSpan = (int)span.GetType().GetProperty("RowCount", Any)!.GetValue(span)!;
                int columnSpan = (int)span.GetType().GetProperty("ColumnCount", Any)!.GetValue(span)!;
                if (rowSpan <= 1 && columnSpan <= 1) continue;
                int top = (int)span.GetType().GetProperty("Top", Any)!.GetValue(span)!;
                int left = (int)span.GetType().GetProperty("Left", Any)!.GetValue(span)!;
                string key = $"{top},{left},{rowSpan},{columnSpan}";
                if (!found.Contains(key)) found.Add(key);
            }
        }

        return found.ToArray();
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail != null ? "  [" + detail + "]" : ""));
        if (ok) _pass++; else _fail++;
    }
}
