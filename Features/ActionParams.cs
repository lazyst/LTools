using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 动作参数：变量表里「方向 = 输入」的变量。面板 / 快捷键 / 悬浮运行前问一次，填过的值记住。
/// </summary>
/// <remarks>
/// <para>
/// 这是「同一个动作换个参数别复制多份」的出路：参数就是**变量表里已有的变量**，
/// 没有新的步骤模型、没有新的动作类型。问到的值经 <see cref="ActionRunOptions.InitialVariables"/>
/// 注入（引擎只覆盖已声明的同名变量，与自动触发的事件变量走同一条路）。
/// </para>
/// <para>
/// 只有**用户手动触发**（面板磁贴 / 快捷键 / 悬浮按钮 / 编辑器试运行）才问；自动触发不弹窗，
/// 事件变量照旧注入、其余输入变量用默认值——半夜被定时任务弹一个框要输入是灾难。
/// </para>
/// </remarks>
internal static class ActionParams
{
    /// <summary>这个动作有没有要问的参数（没有就按原来的路径立刻跑，零开销）。</summary>
    public static bool NeedsAsk(ActionDto? action) => InputsOf(action?.Program).Count > 0;

    /// <summary>
    /// 界面上那句「运行前填参数：文件名、目录」——面板 / 悬浮的悬停提示、动作管理摘要、
    /// 选择器预览都从这里取，别各写一份。没有输入变量时返回空串。
    /// </summary>
    /// <remarks>
    /// 传整个变量表就行（内部按「方向 = 输入 + 名字非空」筛），最多列 3 个名字
    /// （第 3 个之后并成「等 N 个」）：这句要能塞进一行 tooltip / 一行摘要。
    /// 名字优先用变量的显示名（<see cref="VariableDef.Label"/>），没有就用变量名。
    /// </remarks>
    public static string Describe(IReadOnlyList<VariableDef> variables)
    {
        if (variables == null) return "";

        var inputs = variables
            .Where(v => v.Direction == VariableDirection.input && !string.IsNullOrWhiteSpace(v.Key))
            .ToList();
        if (inputs.Count == 0) return "";

        var names = inputs.Select(v =>
            string.IsNullOrWhiteSpace(v.Label) ? (v.Key ?? "").Trim() : v.Label.Trim()).ToList();

        string list = names.Count <= 3
            ? string.Join("、", names)
            : $"{names[0]}、{names[1]} 等 {names.Count} 个";
        return $"运行前填参数：{list}";
    }

    /// <summary>同上，按动作取（没声明输入变量时返回空串）。</summary>
    public static string Describe(ActionDto? action) =>
        action?.Program == null ? "" : Describe(action.Program.Variables);

    /// <summary>变量表里「输入」方向且名字非空的变量，按声明顺序。</summary>
    public static IReadOnlyList<VariableDef> InputsOf(ActionProgram? program)
    {
        if (program == null) return Array.Empty<VariableDef>();

        return program.Variables
            .Where(v => v.Direction == VariableDirection.input && !string.IsNullOrWhiteSpace(v.Key))
            .ToList();
    }

    /// <summary>
    /// 在 UI 线程上问一次（预填上次填过的值）。取消返回 null = 这次不跑。
    /// </summary>
    /// <param name="actionId">记忆用的键（动作 Id）；空表示这次不记（如还没保存的草稿）。</param>
    public static IReadOnlyDictionary<string, Value>? Ask(Window? owner, string actionId, string title,
        IReadOnlyList<VariableDef> inputs, string? hint = null)
    {
        if (inputs.Count == 0) return new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);

        var remembered = ActionParamMemory.For(actionId);
        var values = ParamInputDialog.Ask(owner, title, inputs, remembered, hint);
        if (values == null) return null;

        if (!string.IsNullOrWhiteSpace(actionId)) ActionParamMemory.Remember(actionId, values);
        return values;
    }
}

/// <summary>
/// 「上次填过的参数」：<c>&lt;数据根&gt;/state/action-params.json</c>，按动作 Id 分组。
/// </summary>
/// <remarks>
/// <para>
/// 放 <c>state/</c> 而不是 <c>cache/</c>：这些是用户填的值，属于「跨重启该记得」的东西，
/// 而且 <c>state/</c> 在备份域里——换台机器还接着用同一批参数。
/// </para>
/// <para>
/// 进程内缓存 + 写穿（先写 <c>.tmp</c> 再替换），读坏了按空表继续。变量改名后旧键留着不碍事
/// （只是不会再被读到），删动作时由 <see cref="Forget"/> 整组清掉。
/// </para>
/// </remarks>
internal static class ActionParamMemory
{
    private static readonly object Gate = new();
    private static Dictionary<string, Dictionary<string, Value>>? _all;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>空表的新实例（调用方可能持有 / 修改返回值，别共享同一个）。</summary>
    private static Dictionary<string, Value> Empty() => new(StringComparer.OrdinalIgnoreCase);

    public static string Dir => AppPaths.StateDir;

    public static string FilePath => Path.Combine(Dir, "action-params.json");

    /// <summary>这个动作上次填过的值（没有就空表，不返回 null）。</summary>
    public static IReadOnlyDictionary<string, Value> For(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return Empty();

        lock (Gate)
        {
            return Load().TryGetValue(actionId, out var values)
                ? new Dictionary<string, Value>(values, StringComparer.OrdinalIgnoreCase)
                : Empty();
        }
    }

    /// <summary>记住一个动作这次的参数值（整组替换：这次没问到的键不会残留）。</summary>
    public static void Remember(string actionId, IReadOnlyDictionary<string, Value> values)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return;

        lock (Gate)
        {
            var all = Load();
            all[actionId] = new Dictionary<string, Value>(values, StringComparer.OrdinalIgnoreCase);
            Save(all);
        }
    }

    /// <summary>忘掉一个动作的参数（删动作时调）。</summary>
    public static bool Forget(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return false;

        lock (Gate)
        {
            var all = Load();
            if (!all.Remove(actionId)) return false;
            Save(all);
            return true;
        }
    }

    /// <summary>本类之外改了文件（导入备份 / 冒烟夹具）时调一次。</summary>
    public static void Invalidate()
    {
        lock (Gate) _all = null;
    }

    private static Dictionary<string, Dictionary<string, Value>> Load()
    {
        if (_all != null) return _all;

        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Value>>>(json, Json);
                if (parsed != null)
                {
                    _all = new Dictionary<string, Dictionary<string, Value>>(StringComparer.Ordinal);
                    foreach (var (id, values) in parsed)
                        _all[id] = new Dictionary<string, Value>(values, StringComparer.OrdinalIgnoreCase);
                    return _all;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 坏了就当没记过：参数记忆丢了只是多填一次，别让它挡着动作运行。
            AppLog.Warn("Params", $"参数记忆读取失败，按空表处理：{ex.Message}");
        }

        return _all = new Dictionary<string, Dictionary<string, Value>>(StringComparer.Ordinal);
    }

    private static void Save(Dictionary<string, Dictionary<string, Value>> all)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记忆失败不该拦住运行：写不进去就算了（下次再问一次）。
            AppLog.Warn("Params", $"参数记忆保存失败：{ex.Message}");
        }
    }
}
