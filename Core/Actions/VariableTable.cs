using System.Globalization;

namespace LTools.Core.Actions;

/// <summary>
/// 运行时变量作用域：变量名大小写不敏感，支持父作用域（子动作用）。
/// </summary>
/// <remarks>
/// 路径语法与旧流程保持一致，并扩展到列表 / 对象：
/// <list type="bullet">
/// <item><c>name</c> — 变量本身</item>
/// <item><c>name.member</c> — 对象成员（列表 / 文本另有 <c>count</c> / <c>length</c>）</item>
/// <item><c>name[0]</c> — 列表下标（文本按字符）</item>
/// </list>
/// 先按完整字符串查一次变量（兼容键里本来就带点的极少数情况），再按路径解析。
/// </remarks>
public sealed class VariableScope
{
    private readonly Dictionary<string, Value> _values = new(StringComparer.OrdinalIgnoreCase);

    public VariableScope(VariableScope? parent = null) => Parent = parent;

    /// <summary>父作用域；根作用域为 null。</summary>
    public VariableScope? Parent { get; }

    /// <summary>本作用域内的变量数（不含父作用域）。</summary>
    public int Count => _values.Count;

    /// <summary>按名字查找（含父作用域）。</summary>
    public bool TryGet(string name, out Value value)
    {
        name = (name ?? "").Trim();
        if (_values.TryGetValue(name, out value!)) return true;
        if (Parent != null) return Parent.TryGet(name, out value);
        value = Value.Empty;
        return false;
    }

    /// <summary>只在本作用域查找。</summary>
    public bool TryGetLocal(string name, out Value? value) =>
        _values.TryGetValue((name ?? "").Trim(), out value);

    /// <summary>写入本作用域（已存在则覆盖）。</summary>
    public void Set(string name, Value value)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ActionException("变量名为空");
        _values[name] = value ?? Value.Empty;
    }

    /// <summary>解析 <c>a.b[0].c</c> 形式的变量路径；未定义 / 成员不存在时抛出可读错误。</summary>
    public Value Resolve(string path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) throw new ActionException("模板里的变量名为空");

        // 完整名字优先（变量名理论上可以带点）。
        if (TryGet(path, out var exact)) return exact;

        int i = 0;
        string baseName = ReadName(path, ref i);
        if (baseName.Length == 0)
            throw new ActionException($"变量路径写法无效：{path}");
        if (!TryGet(baseName, out var current))
            throw new ActionException($"未定义变量：{baseName}（先添加变量，或检查拼写。完整路径：{path}）");

        while (i < path.Length)
        {
            char c = path[i];
            if (c == '.')
            {
                i++;
                string member = ReadName(path, ref i);
                if (member.Length == 0)
                    throw new ActionException($"变量路径写法无效：{path}");
                try { current = current.GetMember(member); }
                catch (ActionException ex) { throw new ActionException($"读取「{path}」失败：{ex.Message}", ex); }
            }
            else if (c == '[')
            {
                int end = path.IndexOf(']', i + 1);
                if (end < 0) throw new ActionException($"变量路径缺少右方括号：{path}");
                string indexText = path[(i + 1)..end].Trim();
                if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                    throw new ActionException($"列表下标必须是整数：{indexText}");
                try { current = current.GetIndex(index); }
                catch (ActionException ex) { throw new ActionException($"读取「{path}」失败：{ex.Message}", ex); }
                i = end + 1;
            }
            else
            {
                throw new ActionException($"变量路径写法无效（位置 {i}）：{path}");
            }
        }

        return current;
    }

    /// <summary>把本作用域与所有父作用域摊平（子作用域覆盖父作用域）。</summary>
    public Dictionary<string, Value> Flatten()
    {
        var all = Parent?.Flatten() ?? new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _values) all[kv.Key] = kv.Value;
        return all;
    }

    /// <summary>当前作用域的变量快照（不含父作用域）。</summary>
    public IReadOnlyDictionary<string, Value> Snapshot()
    {
        var copy = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _values) copy[kv.Key] = kv.Value;
        return copy;
    }

    private static string ReadName(string path, ref int index)
    {
        int start = index;
        while (index < path.Length && path[index] != '.' && path[index] != '[')
            index++;
        return path[start..index].Trim();
    }
}
