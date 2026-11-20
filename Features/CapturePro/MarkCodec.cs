using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 标注的落盘编解码（采集历史「重新编辑截图」要恢复当时的标注 + 撤销 / 重做栈）。
/// </summary>
/// <remarks>
/// <para>
/// 每个 <see cref="Mark"/> / 样式记录都是**不可变对象**，所以只要把它的**构造参数**原样写下来，
/// 读回来就能重建出一模一样的一条（同一个 seed、同一套样式、同一份点列）。
/// 形状 / 箭头 / 文字 / 序号 / 测距 / 测角 / 放大镜 / 气泡都走「带 Spec 的那个构造」——
/// 千万别退回简化构造：那个会重新抽一个抖动 seed，画面就和当初不一样了。
/// </para>
/// <para>
/// 枚举按**名字**写（顺序变了也读得回来）、颜色写 <c>#AARRGGBB</c>、点写 <c>x,y</c>、
/// 矩形写 <c>x,y,w,h</c>，数字用 <c>double</c> 的最短往返写法（<c>0.1</c> 不会变成 <c>0.10000000000000001</c>）。
/// 认不出的类型 / 坏掉的一条只跳过并记日志，绝不让整份历史读不出来。
/// </para>
/// </remarks>
internal static class MarkCodec
{
    /// <summary>能落盘的标注类型（新增标注必须加进这张表，否则历史里会丢掉它）。</summary>
    private static readonly Type[] Known =
    {
        typeof(Mark.Stroke), typeof(Mark.Shape), typeof(Mark.Arrow), typeof(Mark.Polyline),
        typeof(Mark.Text), typeof(Mark.Serial), typeof(Mark.Mosaic), typeof(Mark.Filter),
        typeof(Mark.Spotlight), typeof(Mark.Line), typeof(Mark.Distance), typeof(Mark.Angle),
        typeof(Mark.Bracket), typeof(Mark.SpeechBubble), typeof(Mark.Magnifier), typeof(Mark.Watermark),
        typeof(Mark.Highlight), typeof(Mark.Restore), typeof(Mark.BrushFilter),

        typeof(ShapeSpec), typeof(ArrowSpec), typeof(TextSpec), typeof(SerialSpec), typeof(DistanceSpec),
        typeof(AngleSpec), typeof(MagnifierSpec), typeof(WatermarkSpec),
    };

    private static readonly Dictionary<string, Type> Types =
        Known.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>构造参数名与属性名不一致的两处（其余按名字大小写不敏感对上）。</summary>
    private static readonly Dictionary<Type, Dictionary<string, string>> Aliases = new()
    {
        [typeof(Mark.Text)] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = nameof(Mark.Text.Content),
        },
        [typeof(Mark.Line)] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["style"] = nameof(Mark.Line.StrokeStyle),
        },
    };

    private static readonly Dictionary<Type, ConstructorInfo> Ctors =
        Known.ToDictionary(t => t, PickCtor);

    // ============ 写 ============

    /// <summary>一份标注列表 → JSON 数组（顺序就是绘制顺序）。</summary>
    public static JsonArray Write(IReadOnlyList<Mark> marks)
    {
        var array = new JsonArray();
        foreach (var mark in marks) array.Add(ToNode(mark));
        return array;
    }

    /// <summary>撤销 / 重做栈 → 「数组的数组」（每份快照一份列表）。</summary>
    public static JsonArray WriteHistory(IEnumerable<Mark[]> snapshots)
    {
        var array = new JsonArray();
        foreach (var snapshot in snapshots) array.Add(Write(snapshot));
        return array;
    }

    private static JsonNode? ToNode(object? value) => ToNode(value?.GetType(), value);

    private static JsonNode? ToNode(Type? type, object? value)
    {
        if (value == null) return null;
        type ??= value.GetType();

        if (value is string text) return JsonValue.Create(text);
        if (value is bool flag) return JsonValue.Create(flag);
        if (value is Enum) return JsonValue.Create(value.ToString());
        if (value is Color color) return JsonValue.Create(ColorOf(color));
        if (value is Point point) return JsonValue.Create(PointOf(point));
        if (value is Rect rect) return JsonValue.Create(RectOf(rect));

        if (value is double number) return JsonValue.Create(number);
        if (value is int integer) return JsonValue.Create(integer);
        if (value is uint unsigned) return JsonValue.Create((long)unsigned);
        if (value is long big) return JsonValue.Create(big);

        if (value is Mark.Magnifier magnifier)
        {
            return new JsonObject
            {
                ["t"] = "Magnifier",
                ["a"] = new JsonObject
                {
                    ["lensCenter"] = PointOf(magnifier.LensCenter),
                    ["sourceWidth"] = magnifier.SourceWidth,
                    ["sourceHeight"] = magnifier.SourceHeight,
                    ["sourceCenter"] = PointOf(magnifier.SourceCenter),
                    ["spec"] = ToNode(typeof(MagnifierSpec), magnifier.Spec),
                },
            };
        }

        if (value is IEnumerable sequence)
        {
            var element = ElementType(type);
            var array = new JsonArray();
            foreach (var item in sequence) array.Add(ToNode(element, item));
            return array;
        }

        if (!Types.TryGetValue(type.Name, out var known))
            throw new InvalidOperationException("标注里出现了没法落盘的类型：" + type.FullName);

        var args = new JsonObject();
        foreach (var parameter in Ctors[known].GetParameters())
            args[parameter.Name!] = ToNode(parameter.ParameterType, PropertyValue(known, value, parameter));

        return new JsonObject { ["t"] = known.Name, ["a"] = args };
    }

    // ============ 读 ============

    /// <summary>JSON 数组 → 标注列表；坏掉的一条跳过（记一条日志），不抛。</summary>
    public static List<Mark> Read(JsonArray? array)
    {
        var marks = new List<Mark>();
        if (array == null) return marks;

        foreach (var node in array)
        {
            try
            {
                if (FromNode(typeof(Mark), node) is Mark mark) marks.Add(mark);
            }
            catch (Exception ex)
            {
                AppLog.Warn("MarkCodec", "读一条标注失败，已跳过：" + ex.Message);
            }
        }

        return marks;
    }

    /// <summary>「数组的数组」→ 撤销 / 重做栈。</summary>
    public static List<Mark[]> ReadHistory(JsonArray? array)
    {
        var snapshots = new List<Mark[]>();
        if (array == null) return snapshots;

        foreach (var node in array)
        {
            if (node is not JsonArray inner) continue;
            snapshots.Add(Read(inner).ToArray());
        }

        return snapshots;
    }

    private static object? FromNode(Type type, JsonNode? node)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null) return node == null ? null : FromNode(underlying, node);

        if (node == null) throw new InvalidOperationException("缺一个参数：" + type.Name);

        if (type == typeof(string)) return Text(node);
        if (type == typeof(bool)) return Bool(node);
        if (type == typeof(double)) return Number(node);
        if (type == typeof(int)) return (int)Math.Round(Number(node));
        if (type == typeof(uint)) return (uint)Math.Max(0, Math.Round(Number(node)));
        if (type == typeof(long)) return (long)Math.Round(Number(node));
        if (type == typeof(Color)) return ParseColor(Text(node));
        if (type == typeof(Point)) return ParsePoint(Text(node));
        if (type == typeof(Rect)) return ParseRect(Text(node));

        if (type.IsEnum)
        {
            string name = Text(node);
            if (Enum.TryParse(type, name, ignoreCase: false, out var parsed)) return parsed!;
            throw new InvalidOperationException($"{type.Name} 没有取值「{name}」");
        }

        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            var element = ElementType(type);
            var items = new List<object?>();
            if (node is JsonArray array)
                foreach (var item in array) items.Add(FromNode(element, item));

            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            foreach (var item in items) list.Add(item);
            return list;
        }

        if (node is not JsonObject obj) throw new InvalidOperationException(type.Name + " 的参数不是一个对象");
        string kind = Text(obj["t"]);
        if (!Types.TryGetValue(kind, out var known))
            throw new InvalidOperationException("不认识的类型：" + kind);

        var args = obj["a"] as JsonObject;
        if (kind == "Magnifier" && args != null)
        {
            return Mark.Magnifier.FromState(ParsePoint(Text(args["lensCenter"])), Number(args["sourceWidth"]),
                Number(args["sourceHeight"]), ParsePoint(Text(args["sourceCenter"])),
                (MagnifierSpec)FromNode(typeof(MagnifierSpec), args["spec"])!);
        }

        var parameters = Ctors[known].GetParameters();
        var values = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
            values[i] = FromNode(parameters[i].ParameterType, Arg(args, parameters[i].Name!));

        return Ctors[known].Invoke(values);
    }

    /// <summary>取一个构造参数的值（键大小写不敏感：手写 / 工具生成的 JSON 常写小驼峰）。</summary>
    private static JsonNode? Arg(JsonObject? args, string name)
    {
        if (args == null) return null;
        if (args.TryGetPropertyValue(name, out var direct)) return direct;

        foreach (var pair in args)
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;

        return null;
    }

    // ============ 构造挑选 ============

    /// <summary>
    /// 优先「带 Spec 的那个构造」（样式 / seed 都在 Spec 里，参数最全也最少），
    /// 没有就取参数最多的那个（例如 <see cref="Line"/> 的 8 参构造，它才带 seed 与线型）。
    /// </summary>
    private static ConstructorInfo PickCtor(Type type)
    {
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctors.Length == 0) throw new InvalidOperationException(type.Name + " 没有公开构造");

        var spec = ctors
            .Where(c => c.GetParameters().Any(p => p.ParameterType.Name.EndsWith("Spec", StringComparison.Ordinal)))
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault();

        return spec ?? ctors.OrderByDescending(c => c.GetParameters().Length).First();
    }

    private static object? PropertyValue(Type type, object instance, ParameterInfo parameter)
    {
        string name = Aliases.TryGetValue(type, out var map) && map.TryGetValue(parameter.Name!, out var alias)
            ? alias
            : parameter.Name!;

        var property = type.GetProperty(name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property == null)
            throw new InvalidOperationException($"{type.Name} 没有属性 {name}（构造参数 {parameter.Name}）");

        return property.GetValue(instance);
    }

    private static Type ElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType() ?? typeof(object);
        if (type.IsGenericType) return type.GetGenericArguments()[0];
        return typeof(object);
    }

    // ============ 文本形式 ============

    private static string ColorOf(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color ParseColor(string text)
    {
        string hex = text.Trim().TrimStart('#');
        static byte Byte(string part) => byte.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return hex.Length switch
        {
            8 => Color.FromArgb(Byte(hex[..2]), Byte(hex[2..4]), Byte(hex[4..6]), Byte(hex[6..8])),
            6 => Color.FromRgb(Byte(hex[..2]), Byte(hex[2..4]), Byte(hex[4..6])),
            _ => throw new InvalidOperationException("色值写法不对：" + text),
        };
    }

    private static string PointOf(Point point) =>
        point.X.ToString("R", CultureInfo.InvariantCulture) + "," +
        point.Y.ToString("R", CultureInfo.InvariantCulture);

    private static Point ParsePoint(string text)
    {
        var parts = text.Split(',');
        if (parts.Length < 2) throw new InvalidOperationException("点写法不对：" + text);
        return new Point(ParseDouble(parts[0]), ParseDouble(parts[1]));
    }

    private static string RectOf(Rect rect) =>
        string.Join(",", new[] { rect.X, rect.Y, rect.Width, rect.Height }
            .Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    private static Rect ParseRect(string text)
    {
        var parts = text.Split(',');
        if (parts.Length < 4) throw new InvalidOperationException("矩形写法不对：" + text);
        return new Rect(ParseDouble(parts[0]), ParseDouble(parts[1]),
            ParseDouble(parts[2]), ParseDouble(parts[3]));
    }

    private static double ParseDouble(string text) =>
        double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Text(JsonNode? node)
    {
        if (node is not JsonValue value) return "";
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<System.Text.Json.JsonElement>(out var element) &&
            element.ValueKind == System.Text.Json.JsonValueKind.String)
            return element.GetString() ?? "";
        return "";
    }

    private static bool Bool(JsonNode? node)
    {
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<bool>(out var flag)) return flag;
        if (value.TryGetValue<System.Text.Json.JsonElement>(out var element) &&
            element.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            return element.GetBoolean();
        return false;
    }

    private static double Number(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<uint>(out var unsigned)) return unsigned;
        if (value.TryGetValue<long>(out var big)) return big;
        if (value.TryGetValue<decimal>(out var exact)) return (double)exact;
        if (value.TryGetValue<string>(out var text)) return ParseDouble(text);

        // 从磁盘读回来的 JsonNode 背后是 JsonElement（不是内存里那个具体类型）
        if (value.TryGetValue<System.Text.Json.JsonElement>(out var element) &&
            element.ValueKind == System.Text.Json.JsonValueKind.Number)
            return element.GetDouble();

        return 0;
    }
}
