using System.Globalization;
using LTools.Core.Actions;

namespace LTools.Features.Units;

/// <summary>
/// 新值模型（<see cref="Value"/>）与旧流程值（<see cref="VarValue"/>）之间的桥。
/// </summary>
/// <remarks>
/// W2 期间大量能力（找图 / OCR / 截图 / 窗口 / 速记）仍是旧实现，接收 <see cref="VarValue"/>。
/// 这里把两边的结构化字段对齐：坐标、区域、匹配分数、命令输出、速记元数据都装进对象的同名成员，
/// 于是 <c>${变量.x}</c>、<c>${变量.file}</c> 这类写法在旧流程和新流程里表现一致。
/// </remarks>
internal static class UnitValues
{
    /// <summary>旧值 → 新值（对象里一定带 <c>text</c> 成员，因此直接当文本用仍然正确）。</summary>
    public static Value FromLegacy(VarValue value)
    {
        var members = new List<KeyValuePair<string, Value>>
        {
            new("text", Value.FromText(value.Text)),
        };

        void Add(string key, Value member) => members.Add(new(key, member));

        if (value.Number is double number) Add("number", Value.FromNumber(number));
        if (value.Flag is bool flag) Add("found", Value.FromBool(flag));
        if (value.X is int x) Add("x", Value.FromNumber(x));
        if (value.Y is int y) Add("y", Value.FromNumber(y));
        if (value.W is int w) Add("w", Value.FromNumber(w));
        if (value.H is int h) Add("h", Value.FromNumber(h));

        if (value.X is int rx && value.Y is int ry && value.W is int rw && value.H is int rh)
        {
            Add("rect", Value.FromText($"{rx},{ry},{rw},{rh}"));
            Add("cx", Value.FromNumber(rx + rw / 2));
            Add("cy", Value.FromNumber(ry + rh / 2));
        }

        if (value.File.Length > 0)
        {
            Add("file", Value.FromText(value.File));
            Add("path", Value.FromText(value.File));
        }

        if (value.Score is double score) Add("score", Value.FromNumber(score));
        if (value.Error.Length > 0)
        {
            Add("err", Value.FromText(value.Error));
            Add("error", Value.FromText(value.Error));
        }

        if (value.Title.Length > 0) Add("title", Value.FromText(value.Title));
        if (value.Time.Length > 0) Add("time", Value.FromText(value.Time));

        return Value.FromObject(members);
    }

    /// <summary>新值 → 旧值（只取旧实现认识的字段；缺什么就留空）。</summary>
    public static VarValue ToLegacy(Value value)
    {
        string text = value.AsText();
        double? number = null;
        bool? flag = null;
        int? x = null, y = null, w = null, h = null;
        double? score = null;
        string file = "", error = "", title = "", time = "";

        if (value.Kind == ValueKind.number) number = value.NumberValue;
        if (value.Kind == ValueKind.@bool) flag = value.BoolValue;

        if (value.Kind == ValueKind.@object)
        {
            var members = value.Members;
            if (members.TryGetValue("number", out var numberValue)) number = SafeNumber(numberValue);
            if (members.TryGetValue("found", out var foundValue)) flag = SafeBool(foundValue);
            if (members.TryGetValue("x", out var xValue)) x = SafeInt(xValue);
            if (members.TryGetValue("y", out var yValue)) y = SafeInt(yValue);
            if (members.TryGetValue("w", out var wValue)) w = SafeInt(wValue);
            if (members.TryGetValue("h", out var hValue)) h = SafeInt(hValue);
            if (members.TryGetValue("score", out var scoreValue)) score = SafeNumber(scoreValue);
            if (members.TryGetValue("file", out var fileValue)) file = fileValue.AsText();
            if (members.TryGetValue("err", out var errValue)) error = errValue.AsText();
            if (members.TryGetValue("title", out var titleValue)) title = titleValue.AsText();
            if (members.TryGetValue("time", out var timeValue)) time = timeValue.AsText();
        }

        return new VarValue
        {
            Text = text,
            Number = number,
            Flag = flag,
            X = x,
            Y = y,
            W = w,
            H = h,
            File = file,
            Score = score,
            Error = error,
            Title = title,
            Time = time,
        };
    }

    // —— 常用结构化结果 ——

    /// <summary>一个点（<c>text</c> = "x,y"）。</summary>
    public static Value Point(int x, int y) => Value.Object(
        ("text", Value.FromText($"{x},{y}")),
        ("x", Value.FromNumber(x)),
        ("y", Value.FromNumber(y)),
        ("found", Value.FromBool(true)));

    /// <summary>一个区域（<c>text</c> = "x,y,w,h"）。</summary>
    public static Value Region(int x, int y, int w, int h, string? file = null)
    {
        var members = new List<(string, Value)>
        {
            ("text", Value.FromText($"{x},{y},{w},{h}")),
            ("x", Value.FromNumber(x)),
            ("y", Value.FromNumber(y)),
            ("w", Value.FromNumber(w)),
            ("h", Value.FromNumber(h)),
            ("cx", Value.FromNumber(x + w / 2)),
            ("cy", Value.FromNumber(y + h / 2)),
            ("rect", Value.FromText($"{x},{y},{w},{h}")),
            ("found", Value.FromBool(true)),
        };

        if (!string.IsNullOrEmpty(file))
        {
            members.Add(("file", Value.FromText(file)));
            members.Add(("path", Value.FromText(file)));
        }

        return Value.Object(members.ToArray());
    }

    /// <summary>图像匹配结果（<c>text</c> = "x,y"）。</summary>
    public static Value Match(int x, int y, double score) => Value.Object(
        ("text", Value.FromText($"{x},{y}")),
        ("x", Value.FromNumber(x)),
        ("y", Value.FromNumber(y)),
        ("score", Value.FromNumber(score)),
        ("found", Value.FromBool(true)));

    /// <summary>没找到（结构化字段齐全，便于统一取 <c>.found</c> / <c>.count</c>）。</summary>
    public static Value NotFound(int count = 0) => Value.Object(
        ("text", Value.FromText("")),
        ("found", Value.FromBool(false)),
        ("count", Value.FromNumber(count)));

    /// <summary>命令执行结果。</summary>
    public static Value CommandResult(string stdout, string stderr, int exitCode) => Value.Object(
        ("text", Value.FromText(stdout)),
        ("out", Value.FromText(stdout)),
        ("err", Value.FromText(stderr)),
        ("error", Value.FromText(stderr)),
        ("exit", Value.FromNumber(exitCode)),
        ("code", Value.FromNumber(exitCode)),
        ("number", Value.FromNumber(exitCode)),
        ("found", Value.FromBool(exitCode == 0)));

    /// <summary>窗口读取结果。</summary>
    public static Value Window(string title, bool found, bool active, long handle = 0) => Value.Object(
        ("text", Value.FromText(title)),
        ("title", Value.FromText(title)),
        ("found", Value.FromBool(found)),
        ("active", Value.FromBool(active)),
        ("handle", Value.FromNumber(handle)));

    /// <summary>按名字取对象成员；不是对象 / 没这个成员时返回 null。</summary>
    public static Value? Member(Value value, string name) =>
        value.Kind == ValueKind.@object && value.Members.TryGetValue(name, out var member)
            ? member
            : null;

    /// <summary>把 <c>x,y,w,h</c> 文本或对象解析成矩形；解析失败返回 null。</summary>
    public static int[]? ParseRect(Value value)
    {
        if (value.Kind == ValueKind.@object)
        {
            int? x = SafeInt(Member(value, "x"));
            int? y = SafeInt(Member(value, "y"));
            int? w = SafeInt(Member(value, "w"));
            int? h = SafeInt(Member(value, "h"));
            if (x != null && y != null && w != null && h != null)
                return new[] { x.Value, y.Value, w.Value, h.Value };
            return null;
        }

        if (value.Kind == ValueKind.list)
        {
            var items = value.Items;
            if (items.Count >= 4 &&
                SafeInt(items[0]) is int lx && SafeInt(items[1]) is int ly &&
                SafeInt(items[2]) is int lw && SafeInt(items[3]) is int lh)
                return new[] { lx, ly, lw, lh };
            return null;
        }

        string text = value.AsText().Trim();
        if (text.Length == 0) return null;

        string[] parts = text.Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return null;

        if (int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int px) &&
            int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int py) &&
            int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pw) &&
            int.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ph))
            return new[] { px, py, pw, ph };

        return null;
    }

    private static double? SafeNumber(Value? value)
    {
        if (value == null) return null;
        try { return value.AsNumber(); }
        catch (ActionException) { return null; }
    }

    private static int? SafeInt(Value? value)
    {
        double? number = SafeNumber(value);
        return number == null ? null : (int)Math.Round(number.Value);
    }

    private static bool? SafeBool(Value? value)
    {
        if (value == null) return null;
        try { return value.AsBool(); }
        catch (ActionException) { return null; }
    }
}
