using System.Runtime.InteropServices;
using LTools.Core.Actions;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Native;

namespace LTools.Core.Ai;

/// <summary>
/// MCP 工具的实际实现。所有工具共用一个回执形状与审计入口，避免每个工具各写一份 try/catch。
/// </summary>
internal static partial class AiToolHandlers
{
    // ============ 读 ============

    public static Dictionary<string, object?> Health()
    {
        var settings = AiSettings.Load();
        return new Dictionary<string, object?>
        {
            ["version"] = typeof(AiTools).Assembly.GetName().Version?.ToString(3) ?? "",
            ["enabled"] = settings.Enabled,
            ["running"] = McpServer.IsRunning,
            ["port"] = settings.Port,
            ["endpoint"] = $"http://127.0.0.1:{settings.Port}/mcp",
            ["allowWrites"] = settings.AllowWrites,
            ["writesAllowed"] = settings.WritesAllowed,
            ["approvalMode"] = settings.ApprovalMode.ToString(),
            ["tokenPresent"] = settings.Token.Length > 0,
            ["draftCount"] = DraftStore.List().Count,
            ["stagingRoot"] = DraftStore.Root,
            ["knowledgeDir"] = KnowledgePack.Dir,
            ["lastError"] = McpServer.LastError ?? settings.LastError,
            ["environment"] = EnvironmentInfo(),
        };
    }

    /// <summary>
    /// 环境信息：屏幕 / DPI / 光标 / 常用目录。
    /// </summary>
    /// <remarks>
    /// 坐标类动作（「鼠标操作」「屏幕区域」「截图」「图片点击」）写偏最多人抱怨，而 agent 看不见用户的屏幕：
    /// 不告诉它「1920×1080 单屏、100% 缩放」它就只敢猜 <c>0.5, 0.5</c>。这里给的都是**物理像素**，
    /// 与截图 / 找图 / 鼠标注入同一套坐标系；DIP 由 <c>scale</c> 换算。
    /// </remarks>
    private static Dictionary<string, object?> EnvironmentInfo()
    {
        var screens = Screens.All();
        var payload = new Dictionary<string, object?>
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["is64BitProcess"] = Environment.Is64BitProcess,
            ["elevated"] = IsElevated(),
            ["virtualScreen"] = new Dictionary<string, object?>
            {
                ["left"] = ScreenCapture.VirtualLeft,
                ["top"] = ScreenCapture.VirtualTop,
                ["width"] = ScreenCapture.VirtualWidth,
                ["height"] = ScreenCapture.VirtualHeight,
            },
            ["screens"] = screens.Select(screen => (object?)new Dictionary<string, object?>
            {
                ["left"] = screen.Left,
                ["top"] = screen.Top,
                ["width"] = screen.Width,
                ["height"] = screen.Height,
                ["dpi"] = screen.Dpi,
                ["scale"] = screen.Scale,
                ["primary"] = screen.Primary,
                ["workArea"] = new Dictionary<string, object?>
                {
                    ["left"] = screen.WorkLeft,
                    ["top"] = screen.WorkTop,
                    ["width"] = screen.WorkWidth,
                    ["height"] = screen.WorkHeight,
                },
            }).ToList(),
        };

        if (Win32.GetCursorPos(out var cursor))
            payload["cursor"] = new Dictionary<string, object?> { ["x"] = cursor.X, ["y"] = cursor.Y };

        // 常用目录：agent 自己拼路径最容易写错（dev 落仓库根、发布落 exe 同级，还可能被便携目录挪走）。
        var anchor = string.IsNullOrWhiteSpace(ConfigStore.ConfigPath)
            ? ConfigLocator.FindPath()
            : ConfigStore.ConfigPath;
        payload["directories"] = new Dictionary<string, object?>
        {
            ["dataRoot"] = AppPaths.DataRoot,
            ["actions"] = ActionStore.Root(anchor),
            ["staging"] = DraftStore.Root,
            ["images"] = ImageLibrary.Dir,
            ["userResources"] = ImageLibrary.UserDir,
            ["notes"] = AppPaths.NotesDir,
            ["subactions"] = AppPaths.SubActionsDir,
            ["knowledge"] = KnowledgePack.Dir,
            ["logs"] = AppPaths.LogsDir,
        };

        try { payload["defaultOcrEngine"] = OcrEnginePreference.DefaultForNewProbe ?? "windows"; }
        catch (Exception ex) { AppLog.Warn("Ai", "读取默认 OCR 引擎失败：" + ex.Message); }

        return payload;
    }

    /// <summary>进程是否提权（UIA 读提权窗口需要，普通权限会拿到空树）。</summary>
    private static bool IsElevated() => Elevation.IsElevated;

    public static Dictionary<string, object?> DocsGet(string name)
    {
        name = (name ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0) name = "format";
        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["content"] = KnowledgePack.Read(name),
        };
    }

    /// <summary>
    /// 动作单元索引。<c>in</c> / <c>out</c> 是这个单元的输入 / 输出**类型**（去重、排序，兼作数量），
    /// <c>produces</c> / <c>consumes</c> 按类型反查（「谁会产数字」「谁能吃文本」）。
    /// 类型名与 <c>units_get</c> 的 <c>type</c> 字段同一套（<see cref="KnowledgePack.MachineName"/>），
    /// 类型不明写 <c>any</c>。
    /// </summary>
    /// <remarks>
    /// 反查是 <b>OR</b>：写 <c>produces: "number,list"</c> = 产数字**或**产列表。与 <paramref name="query"/> 是 AND。
    /// 这里刻意不返回参数级细节（名字 / 必填 / 默认值）——那是 <c>ltools_units_get</c> 的活，
    /// 索引只回答「去哪儿找」，回答得越多越像一份要全量读的目录。
    /// </remarks>
    public static Dictionary<string, object?> UnitsList(
        string? query, string? produces = null, string? consumes = null)
    {
        query = (query ?? "").Trim();
        HashSet<string> wantProduced = ParseTypes(produces);
        HashSet<string> wantConsumed = ParseTypes(consumes);

        var units = StepRegistry.Default.Units
            .Where(u => query.Length == 0
                        || u.Spec.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || u.Spec.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || u.Spec.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || u.Spec.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(u => new
            {
                Spec = u.Spec,
                In = Types(u.Spec.Inputs.Select(i => i.Type)),
                Out = Types(u.Spec.Outputs.Select(o => o.Type)),
            })
            .Where(u => wantProduced.Count == 0 || u.Out.Any(wantProduced.Contains))
            .Where(u => wantConsumed.Count == 0 || u.In.Any(wantConsumed.Contains))
            .OrderBy(u => u.Spec.Category, StringComparer.Ordinal)
            .ThenBy(u => u.Spec.Key, StringComparer.OrdinalIgnoreCase)
            .Select(u => new Dictionary<string, object?>
            {
                ["key"] = u.Spec.Key,
                ["name"] = u.Spec.Name,
                ["category"] = u.Spec.Category,
                ["kind"] = u.Spec.Kind.ToString(),
                ["description"] = u.Spec.Description,
                ["in"] = u.In,
                ["out"] = u.Out,
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["count"] = units.Count,
            ["units"] = units,
        };
    }

    /// <summary>把 <c>"number, list"</c> 解析成类型名集合（大小写不敏感，<c>,</c> / <c>|</c> / 空白都认）。</summary>
    private static HashSet<string> ParseTypes(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return set;

        foreach (string part in text.Split(new[] { ',', '|', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            set.Add(part.Trim());
        return set;
    }

    /// <summary>一组声明类型 → 去重排序的类型名（类型不明按 <c>any</c>）。</summary>
    private static List<string> Types(IEnumerable<ValueKind?> kinds) =>
        kinds.Select(KnowledgePack.MachineName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    public static Dictionary<string, object?> UnitsGet(string key)
    {
        key = (key ?? "").Trim();
        if (!StepRegistry.Default.TryGet(key, out var unit))
            throw new AiToolException("unknown_unit", $"未知的动作单元：{key}（先用 ltools_units_list 查）");

        var spec = unit.Spec;
        var payload = new Dictionary<string, object?>
        {
            ["key"] = spec.Key,
            ["name"] = spec.Name,
            ["category"] = spec.Category,
            ["kind"] = spec.Kind.ToString(),
            ["body"] = spec.Body.ToString(),
            ["dynamicInputs"] = spec.DynamicInputs,
            ["description"] = spec.Description,
            ["inputs"] = spec.Inputs.Select(input => new Dictionary<string, object?>
            {
                ["key"] = input.Key,
                ["label"] = input.Label,
                ["type"] = input.Type?.ToString(),
                ["editor"] = input.Editor.ToString(),
                ["required"] = input.Required,
                ["default"] = input.Default,
                ["visibleWhen"] = input.VisibleWhen,
                ["options"] = input.Options,
                ["labels"] = input.Labels,
                ["raw"] = input.Raw,
                ["description"] = input.Description,
                ["hint"] = InputHint.Of(input),
                // 选项参数：驱动键本身给出「选项 → 启用了哪些参数」，其余参数给出「我由哪些选项启用」。
                // 以前这里一个字段都没有，agent 只能猜「单击要不要写 dx」。
                ["optionParams"] = input.OptionParams is { Count: > 0 }
                    ? input.OptionParams.ToDictionary(p => p.Key, p => (object?)p.Value.ToList())
                    : null,
                ["enabledWhen"] = OptionScope(input, spec),
            }).ToList(),
            ["outputs"] = spec.Outputs.Select(output => new Dictionary<string, object?>
            {
                ["key"] = output.Key,
                ["label"] = output.Label,
                ["type"] = output.Type?.ToString(),
                ["defaultVariable"] = OutputNaming.Suggest(spec.Key, output),
                ["description"] = output.Description,
            }).ToList(),
        };

        // 「调用子动作」的参数名来自用户的子动作库（参数名 = 输入变量名），
        // 动作单元声明里只有一个 subAction，不一起给出签名就只能瞎猜。
        if (string.Equals(spec.Key, "sub.run", StringComparison.OrdinalIgnoreCase))
            payload["subActions"] = SubActionStore.LoadAll().Select(Signature).ToList();

        return payload;
    }

    /// <summary>
    /// 这个参数由「哪个选项参数的哪些值」启用（<c>null</c> = 不受选项管，任何选项下都可见）。
    /// 与 <c>units.md</c> 的选项表同源（<see cref="KnowledgePack.EnabledOptions"/>），别各算一遍。
    /// </summary>
    private static Dictionary<string, object?>? OptionScope(InputSpec input, UnitSpec spec)
    {
        var options = KnowledgePack.EnabledOptions(input, spec);
        if (options.Count == 0) return null;

        return new Dictionary<string, object?>
        {
            ["key"] = KnowledgePack.OptionDriver(spec)?.Key,
            ["in"] = options,
        };
    }

    public static Dictionary<string, object?> ActionsList(string? query, int limit)
    {
        query = (query ?? "").Trim();
        limit = Math.Clamp(limit, 1, 500);

        var rows = AiUi.Run(() => ActionRegistry.All
            .Where(a => query.Length == 0
                        || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || a.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .Take(limit)
            .Select(a => new Dictionary<string, object?>
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["icon"] = a.Icon,
                ["type"] = MacroAction.MacroIdOf(a) != null ? "macro" : "action",
                ["stepCount"] = a.Program?.Steps.Count,
            })
            .ToList());

        return new Dictionary<string, object?>
        {
            ["count"] = rows.Count,
            ["actions"] = rows,
        };
    }

    public static Dictionary<string, object?> ActionsGet(string id)
    {
        id = (id ?? "").Trim();
        var action = AiUi.Run(() => ActionRegistry.FindById(id))
            ?? throw new AiToolException("not_found", $"正式动作不存在：{id}");

        var program = AiUi.Run(() => action.Program?.Clone());
        return new Dictionary<string, object?>
        {
            ["id"] = action.Id,
            ["name"] = action.Name,
            ["icon"] = action.Icon,
            ["type"] = MacroAction.MacroIdOf(action) != null ? "macro" : "action",
            ["program"] = program,
        };
    }

    public static Dictionary<string, object?> SubActionsList(string? query, int limit)
    {
        query = (query ?? "").Trim();
        limit = Math.Clamp(limit, 1, 200);

        var rows = SubActionStore.LoadAll()
            .Where(d => query.Length == 0
                        || d.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (d.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                        || d.Variables.Any(v => (v.Key ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .Select(d =>
            {
                var row = Signature(d);
                row["icon"] = d.Icon;
                row["description"] = d.Description;
                row["stepCount"] = d.Steps.Count;
                return row;
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["count"] = rows.Count,
            ["subActions"] = rows,
        };
    }

    public static Dictionary<string, object?> SubActionsGet(string id)
    {
        id = (id ?? "").Trim();
        var def = SubActionStore.Load(id)
            ?? throw new AiToolException("not_found", $"子动作不存在：{id}");

        return new Dictionary<string, object?>
        {
            ["id"] = def.Id,
            ["name"] = def.Name,
            ["icon"] = def.Icon,
            ["description"] = def.Description,
            ["revision"] = SubActionStore.Revision(def.Id),
            ["program"] = def.Clone(),
        };
    }

}
