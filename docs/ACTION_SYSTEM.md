# 动作系统重构方案：功能单元 · 类型化变量 · 子动作

> 目标：把现在「一个 `ActionDto` 装下所有类型 + 一个 300 行 switch」的结构，换成
> **功能单元（Unit）→ 步骤（Step）→ 动作（Action）** 三层，变量带类型，并引入可复用的子动作（函数）。
>
> 已定方向：**一次性重写** / 子动作 = **全局库 + 动作内** / 变量 = **5 种类型**。
> 本文件是方案与施工图，不含实现。证据见 §2。
>
> **状态（2026-11-07）**：W1 内核 + W2 全量单元已落地（`Core/Actions/` + `Features/Units/`，
> 36 个功能单元 + 9 个流程单元），无头冒烟 395 项断言通过，面板 UI 冒烟无回归。
> 上一代动作系统的说明在 `docs/ACTION_SYSTEM_PLAN.md`，本文档取代它。

---

## 0. 结论先行

1. **值得抄 Quicker 的**：单元声明式元数据（输入/输出规格 → 由它生成 UI 和校验）、
   `步骤.输入参数 = 字面量 | 变量` 的两态、控制流用「嵌套子步骤数组」而不是节点图、
   子程序 = 变量打 `IsInput/IsOutput` 标记、模块粒度停在「一类操作 + operation 参数」。
2. **不要抄的**：19 种变量类型、`$$`/`$=` 两套求值语法并存、表格/窗体/自定义类型、
   网络共享子程序、调试器级观测事件。
3. **路线：一次性重写**，但要有一个「切流点」：新内核、单元、迁移、编辑器全部做完并通过
   等价性验收后，**一个提交切换**，随后删除旧实现。开发期旧代码不动、应用始终可构建。
4. 重写后的模型只有**一个联合**（`Value`），而不是今天的四个
   （`ActionDto` / `FlowNode` / `Probe` / `VarValue`）。

---

## 1. 现状（LTools）

| 层 | 现状 | 问题 |
|---|---|---|
| 数据 | `ActionDto` 是扁平联合：`Type` 枚举 17 种 + 所有类型字段平铺（`Target`/`Url`/`Cmd`/`Text`/`WindowOp`/`Image`…） | 新增一种动作 = 改 DTO + 改编辑器 + 改执行器 + 改摘要，四处散落 |
| 执行 | `ActionExecutor.Run` 一个 `switch (action.Type)` 覆盖 17 个分支 | 一个文件承担全部职责；分支之间共享私有方法，边界模糊 |
| 组合 | `ActionDto.Flow: List<FlowNode>`，`FlowNodeKind` 7 种 | 每个节点又各自带一个扁平对象（`SetNode`/`IfNode`/`LoopNode`），第三套联合 |
| 取值 | `Probe` + `ProbeKind` 16 种，扁平字段 | 第四套联合；与流程节点重复（「文字点击」≈「找文字 + 鼠标点击」） |
| 值 | `VarValue`：`Text` 必填 + 可选 `Number`/`Flag`/`X`/`Y`/`W`/`H`/`Score`/`File`… | 无类型；属性靠 `Property("x")` 字符串 switch；列表/对象无法表达 |
| 复用 | 组合动作步骤是**内嵌动作快照**（`StepDto.Action.Clone()`） | 改一处要改 N 处；没有「函数」概念 |

一个典型动作经过四套联合模型，这是「职责不分明」的根：**同一种能力在四处各写一遍**。

---

## 2. Quicker 的做法（安装产物证据）

分析对象：`C:\Program Files\Quicker`（2026-11-06 构建）。

| 产物 | SHA-256（前 16） | 用途 |
|---|---|---|
| `Quicker.StepEngine.Core.dll` | `a8e7ca905663dce5` | 步骤引擎契约：参与者、参数定义、枚举 |
| `Quicker.StepEngine.dll` | — | 动作/子程序/变量的运行时模型 |
| `Quicker.Headless.Designer.Contracts.dll` | — | 设计器 gRPC 契约（变量、步骤、子程序） |
| `Quicker.ExpressionEngine.dll` | — | 表达式求值（`$=`） |

### 2.1 数据模型（三层）

```
XAction      { LimitSingleInstance, SummaryExpression, SubPrograms[], Variables[], Steps[] }
SubProgram   { Id, Name, Icon, Description, SummaryExpression,
               Variables[], Steps[], SubPrograms[], SharedId, IsProtected, ... }
ActionStep   { StepRunnerKey, InputParams: map<string, ActionStepParam>,
               OutputParams: map<string, string>,          // 输出名 → 变量名
               IfSteps[], ElseSteps[],                      // 控制流 = 嵌套子数组
               Note, Disabled, Collapsed, DelayMs }
ActionStepParam { VarKey?, Value? }                        // 二选一：取变量 / 字面量
ActionVariable  { Key, Type, DefaultValue(字符串), ParamName,
                  IsInput, IsOutput, InputParamInfo, OutputParamInfo,
                  IsLocked, Desc, SaveState, Group, TableDef, CustomType }
```

关键点：

- **子程序与动作同构**（都是 `Variables + Steps + SubPrograms`），差别只是子程序的变量带
  `IsInput` / `IsOutput` 标记。
- **控制流不是节点图**：`if` / 循环是普通步骤，分支体是步骤自己的 `IfSteps` / `ElseSteps`。
  「步骤列表」是唯一的结构，编辑器和执行器都只需处理一种递归。
- `StepType` 只有 6 种：`Action / If / Loop / Keyboard / Comment / SubProgram`。

### 2.2 步骤的声明式定义

每个模块用特性声明输入/输出（`InputParamAttribute` / `OutputParamAttribute`），字段包括：

`Key, Name, DisplayOrder, Description, Help, Type(VarType), DefaultValue, NewStepDefaultValue,
DefaultVariableName, IsRequired, VariableMode, InputSources, EditorHint, IsMultiLine,
IsControlField, ValidForList, ValidForProvider, SelectionItems, TextTools, ReplaceMode,
IsAdvanced, SkipEval, AllowInput, VisibleExpression, …`

→ **接口只声明一次**，编辑器 UI、参数校验、摘要、AI 说明全部从这份元数据派生。
这是 Quicker 能容纳 119 个模块而不失控的原因。

### 2.3 参数的来源与去向

```
InputType  = Default | Variable | Value | Expression | Interpolation | Clipboard
OutputType = None | Clipboard | Variable | DictKey
```

### 2.4 变量类型（19 种）

`Text, Number, Boolean, Image, List, DateTime, Keyboard, Mouse, Enum, Dict, Form,
Integer, Table, FormForDict, Specified, Object, Any, NA, CreateVar`

你本机的真实动作印证了枚举顺序：`type:0` = 文本，`type:4` = 列表，`type:2` = 布尔
（`_actionBackups\2026\11\06\按后缀名整理文件`）。

### 2.5 模块粒度

`Quicker.dll` 里 `sys:` 开头的键 317 个，去掉本地化变体（`…T`）后 **119 个模块**，
分 16 类。注意它**没有把一个微操作拆成一个模块**：`sys:stringProcess` 一个模块内部用
`operation` 参数区分替换/截取/大小写…，`sys:fileOperation` 同理。粒度 = **一类操作**。

### 2.6 子程序（文档 + 契约）

- 三种：**动作内子程序**（随动作存）、**公共子程序**（本地全局）、网络共享子程序。
- 输入输出：变量上的两个标记 `IsInput` / `IsOutput`，运行时进入时赋值、结束时读出。
- 作用域：子程序内变量独立，每次调用重新初始化；列表/词典按引用传递。
- 调用步骤的输入参数里有 `subProgramKey`；调用方按子程序声明的输入逐个绑定。

### 2.7 参数求值（文档）

- `$$` 开头 → 插值（结果仍是文本）；`$=` 开头 → 表达式（任意类型）；否则字面量；
- 最后按目标参数类型做一次转换（布尔/数字会再做一次公式解析）。

---

## 3. 目标模型

### 3.1 三层职责

| 层 | 是什么 | 谁负责 |
|---|---|---|
| **Unit（功能单元）** | 一类操作的**定义**：键、名称、分类、图标、输入规格、输出规格、执行函数 | 每个单元一个类 + 一份声明，`Features/Units/<分类>/` |
| **Step（步骤）** | 单元的**一次使用**：`unit` + 参数（字面量/变量/内联取值）+ 输出映射 + 子步骤 | 纯数据，可序列化 |
| **Action（动作）** | 触发器 + 变量表 + 步骤列表（+ 元数据） | 组合而非继承 |

单元内部再分「操作变体」（`window` 的 activate/maximize/resize）——**一个单元 = 一类操作**。

### 3.2 变量与值

```csharp
enum ValueKind { Text, Number, Bool, List, Object }

sealed class Value {
    ValueKind Kind;
    string? Text;  double Number;  bool Flag;
    List<Value>? Items;  Dictionary<string, Value>? Members;
}
```

- **文本 / 数字 / 布尔 / 列表 / 对象** 五种。
- 隐式转换只保留「文本 ⇄ 数字」「文本 ⇄ 布尔」，且只发生在**绑定参数**时；
  运行时不做隐式类型提升，避免「1 + "1" = 11」这类意外。
- 「坐标 / 区域 / 匹配结果」不再是 `VarValue` 的一堆可选字段，而是**对象**：
  `{ x, y, w, h, cx, cy, found, score }`；属性访问统一 `${m.x}`。
- 变量声明带类型 + 默认值；运行期 `RunContext` 里是 `Dictionary<string, Value>`。
- 对象的隐式取用：`AsText()` 取 `text`、`AsNumber()` 取 `number`、`AsBool()` 取 `found` / `flag`，
  所以「有结果」的单元都带 `text` 成员时，`${变量}` 与旧 `VarValue.Text` 一致。
- 表达式只支持**模板 + 成员/下标访问**（`${a.b}`、`${list[0]}`、`${list.count}`），
  不引入完整表达式语言；需要计算结果时用「计算」单元。

### 3.3 单元声明（关键接口）

```csharp
sealed record InputSpec(
    string Key, string Label, ValueKind Type,
    InputEditor Editor = InputEditor.Text,      // Text/Multiline/Number/Bool/Enum/File/Image
    bool Required = false, object? Default = null,
    string? VisibleWhen = null);                // 例如 "operation == 'resize'"

sealed record OutputSpec(string Key, string Label, ValueKind Type, string? DefaultVariable = null);

enum UnitKind { Action, Pure }                  // Pure = 只算值，可在值位置内联

interface IStepUnit {
    UnitSpec Spec { get; }                       // 静态元数据，只声明一次
    StepResult Execute(StepContext ctx, StepArgs args);
}
```

编辑器、参数校验、步骤摘要全部由 `Spec` 生成；**新增一个单元 = 新增一个类 + 一行注册**。

**取值单元（Pure）**：今天的「取值器」并入这套模型，但标记为 `UnitKind.Pure`，
可以出现在任何「值」的位置（参数、条件左右值），不需要先落成一个步骤。
这解决了「条件怎么写」：条件 = `左值(值源) + 比较符 + 右值(值源)`，
值源可以是变量、字面量，或一个内联的 Pure 单元（找图 / 找文字 / 取色…）。

### 3.4 存储格式（schema v2）

```jsonc
{
  "id": "a1", "name": "整理下载目录", "icon": "folder",
  "variables": [
    { "key": "files", "type": "list",   "default": [] },
    { "key": "n",     "type": "number", "default": 0 }
  ],
  "steps": [
    { "unit": "file.select", "in": {}, "out": { "path": "files" } },

    { "unit": "flow.forEach", "in": { "items": { "var": "files" } },
      "out": { "item": "file", "index": "i" },
      "body": [
        { "unit": "file.operation",
          "in": { "operation": "copy", "path": { "var": "file" }, "target": "D:\\归档\\${i}" } }
      ] },

    { "unit": "flow.if",
      "in": { "left": { "var": "n" }, "op": "greater", "right": 0 },
      "then": [ { "unit": "ui.toast", "in": { "text": "已整理 ${n} 个文件" } } ] }
  ],
  "subActions": [ /* 动作内子动作，见 §3.5 */ ]
}
```

参数值三态：`"字面量"`（含 `${}` 模板）/ `{ "var": "name" }` / `{ "unit": "...", "in": {…} }`（内联取值）。
比 Quicker 的 `{value}/{varKey}` 多一个「裸字符串即字面量」的写法，手写配置更省事。

### 3.5 子动作（函数）

**两个存放位置，同一个模型**：

```csharp
sealed class SubActionDef {
    string Id;                 // 全局 "s1" / 动作内 "local:xxx"
    string Name, Icon, Description;
    List<VariableDef> Variables;   // 带 Direction: input / output / local
    List<Step> Steps;
    List<SubActionDef> SubActions; // 允许嵌套（可选）
}
```

> 存放位置决定「动作内 / 全局」，模型里不再另设 `IsLocal` 字段。

| 位置 | 存储 | 可见性 |
|---|---|---|
| **全局库** | `<数据根>/subactions/<id>.json`（`SubActionStore`，与 `macros/*.json` 同一套约定） | 所有动作可见 |
| **动作内** | `ActionProgram.SubActions[]` | 仅当前动作可见；可「升级为全局」 |

调用单元 `sub.run`：**参数名就是子动作的输入变量名**（`DynamicInputs`），
输出按步骤的 `out` 映射写回调用方：

```jsonc
{ "unit": "sub.run",
  "in": { "subAction": "s1", "input": { "var": "text" } },   // input = 子动作里 Direction=input 的变量
  "out": { "output": "clean" } }                             // output = Direction=output 的变量
```

- 每次调用新建作用域，内部变量初始化；列表/对象**按引用传递**（与 Quicker 一致）。
- 调用栈 + 递归深度上限（默认 8）；错误信息带 `动作 → 子动作 → 子动作`。
- 编辑全局子动作影响所有调用方（这是「函数」应有的语义）；动作内子动作跟着动作走。
- 删除前列出引用位置；「升级为全局」= 写文件 + 把 `local:` 引用改成全局 id。

### 3.6 单元清单（W2/W3 已实现）

| 分类 | 单元（W2 已实现） | 对应今天的 |
|---|---|---|
| **文本** | `text.selection` 取选中文字 | Probe.selection |
| | `text.clipboard` 读写剪贴板 | setClipboard / Probe.clipboard |
| | `text.send` 发送文本 | sendText |
| | `text.process` 去空白/大小写/替换/截取/分割/拼接/查找 | 新增（合并零碎操作） |
| **文件** | `file.open` 打开文件/文件夹/网址 | openFile/openFolder/openUrl |
| | `file.select` 选择文件/文件夹 | 新增 |
| | `file.read` / `file.write` 读写文本 | 新增 |
| | `file.operation` 复制/移动/删除/建目录/重命名 | 新增 |
| | `file.exists` 存在/类型/大小/修改时间 | 新增 |
| | `file.explorer` 在资源管理器中定位 | 新增 |
| **程序** | `app.launch` 启动程序 | launchApp |
| | `app.runCommand` 运行命令（→ 退出码/stdout/stderr） | runCommand |
| | `app.process` 进程数量/进程号/结束进程 | Probe.process |
| **输入** | `input.sendKeys` 模拟按键 | sendKeys |
| | `input.mouse` 鼠标操作 | mouse |
| | `input.cursor` 读取光标位置（Pure） | 面板唤起坐标 |
| | `input.clickText` 文字点击 | clickText |
| | `input.clickImage` 图片点击 | imageClick |
| | `input.macro` 回放宏 | macro |
| **窗口** | `window.find` 查找窗口 → 对象 | 新增（复用 WindowFinder） |
| | `window.foreground` 前台窗口信息 | Probe.window |
| | `window.operate` 切前台/最大化/最小化/还原/改尺寸 | window |
| **屏幕** | `screen.capture` 截图 → 对象 | Probe.screenshot |
| | `screen.findImage` 找图 → 对象 | Probe.image |
| | `screen.ocr` 文字识别 → 文本 | Probe.ocr |
| | `screen.findText` 找文字 → 对象 | Probe.textSearch |
| | `screen.pixel` 取色 → 对象 | Probe.color |
| **流程** | `flow.set` 赋值/追加 | FlowNode.set |
| | `flow.if` 条件（then/else） | FlowNode.if |
| | `flow.repeat` / `flow.forEach` / `flow.while` 循环 | FlowNode.loop |
| | `flow.break` / `flow.continue` / `flow.stop` / `flow.delay` | 同名节点 / DelayMs |
| | `flow.lastError` 上一步的错误 | RunContext.LastError |
| **子动作** | `sub.run` 调用子动作（W3） | 新增 |
| **数据** | `data.noteWrite` / `data.noteRead` 速记 | quickNote / Probe.note |
| | `data.list` 列表增删改查/排序/去重 | 新增 |
| | `data.calc` 数值计算 | 新增 |
| | `data.time` 当前时间/格式化/偏移 | 新增 |
| **界面** | `ui.toast` 提示 | toast |
| | `ui.input` 弹输入框 | Probe.user |
| **内部** | `internal.invoke` 内部命令 | internal |

**粒度原则**：能用一个 `operation` 参数区分的微操作，合并在同一个单元里
（`text.process`、`file.operation`、`window.operate`、`data.list`、`data.calc`），
否则工具箱会变成今天 Quicker 的 119 项。`text.format` 已并入模板 + `text.process join`；
`data.json` 留到后期按需要再加。

### 3.7 迁移规则

实现：`Features/Migration/ActionMigrator.cs`（旧 DTO 都在 `Features/`，迁移器放这里，避免 `Core/` 反向依赖）。
入口 `ActionMigrator.Migrate(ActionDto) → MigrationResult{Program, Warnings, Errors}`；
未知字段 / 无法表达的语义进 `Warnings`，**不静默丢**。

| 旧 | 新 |
|---|---|
| `ActionDto { Type=launchApp, Target, Args, Workdir }` | `steps:[{unit:"app.launch", in:{target,args,workdir}}]` |
| `openFile / openFolder / openUrl` | `file.open` + `operation = file / folder / url` |
| `composite.Steps[]`（内嵌快照） | 逐个铺平成 `steps[]`；`OnFail` → 步骤的 `onFail` |
| `composite.Flow[]` | `flow.*` 单元；`Then/Else/Body` → `then/else/body` |
| `Probe`（条件 / set 右值） | 字面量 / `{var}` / 内联 Pure 单元 `{unit, in}` |
| `Condition` | `flow.if` / `flow.while` 的 `left` / `op` / `right` / `negate` |
| `Condition.Bind` | 先 `flow.set` 存进变量，条件改用 `{var}` |
| `ActionDto.OutputVar` | 该步骤 `out: { "value": 变量 }` |
| `VarValue` 的 `X/Y/W/H/Score/File` | 对象成员 `x/y/w/h/score/file` |
| `Node.DelayMs` | 前置 `flow.delay`（旧语义是执行**前**等待） |
| `OnFail` / `CompositeOnFail` 缺省 | 显式写 `onFail: "continue"`（旧默认；新引擎默认 `stop`） |
| `runCommand` 无 `OutputVar` | `capture: false`（旧行为是打开终端窗口） |
| `sendKeys.Items` | 摊平成 `keys[]`：stroke → 原样、text → `text:…`、sleep → `sleep:…` |
| `mouse.WheelDelta`（120/格） | `wheel` = 格数 |
| `ProbeKind.ocr / textSearch` 的 `OcrEngine == null` | `engine: "windows"`（新单元默认 PP-OCRv6） |
| `CompareOp.nearColor` | `equals` + 告警（容差无对应单元） |

**结果对象约定**：有结果的单元除具名输出外还写一个 `value` 对象输出（`{text, found, x, y, …}`），
迁移的 `OutputVar` 统一映射到它；内联求值时优先返回 `value`，因此
`${变量}` = 旧 `VarValue.Text`（PID / stdout / 路径 / 坐标串），`${变量.x}` 等照旧可用。

**变量**：旧流程的变量没有声明，迁移时从 `flow.set`、步骤 `out`、模板 `${a.b}` 里收集名字，
一律声明为 `text`（对象 / 数字在运行期按实际值覆盖声明类型）。

加载时迁移一次，写回新格式；旧 JSON 保留 `.bak-*` 备份（沿用现有惯例，最多 3 份）。

---

## 4. 三条路线（对比留档）

| | A 分阶段双轨 | **B 一次性重写（已选）** | C 薄重构 |
|---|---|---|---|
| 概念干净度 | 过渡期两套 DTO | 一次到位 | 只拆类，模型不变 |
| 交付节奏 | 每阶段可发布 | 单个切流点 | 1～2 天 |
| 回归面 | 小，逐页替换 | 大，一次全量 | 小 |
| 目标达成 | 是 | 是 | 否（无类型/子动作） |

选 B 的**前置条件**（写进施工要求）：

1. 开发在独立分支 / worktree 上做，**每一步提交都能构建**（新代码先不接入 UI）。
2. 切流前必须完成：全量迁移器 + 等价性冒烟 + 旧配置回放。
3. 切流那一个提交只做「换装配」，不改业务逻辑；切流后旧实现立即删除，不留双轨。

---

## 5. 施工图（W1～W5）

### W1 内核（不动 UI）

- `Core/Actions/Value.cs`：`Value` / `ValueKind` / 转换 / `ToText()`。
- `Core/Actions/Template.cs`：`${a.b}`、`${list[0]}`、`${list.count}`，失败给出可读错误。
- `Core/Actions/VariableTable.cs`：声明 + 运行值 + 默认值。
- `Core/Actions/UnitSpec.cs`、`IStepUnit.cs`、`StepRegistry.cs`。
- `Core/Actions/Step.cs`、`ActionProgram.cs`：schema v2 数据模型 + 序列化。
- `Core/Actions/ActionEngine.cs`：执行步骤、作用域、子步骤递归、停止信号、失败策略、步骤超时。

**验收**：无头测试跑通「取值 → 赋值 → 条件 → 循环 → 提示」五步动作。

### W2 全量单元 ✅

- `Features/Units/<分类>/*.cs`，每类一个文件，每个单元一个类 + `IStepUnit` 实现。
- 逻辑从 `ActionExecutor` / `ProbeEvaluator` / `FlowRunner` **搬**过来（不重写行为），
  共享代码下沉到 `Features/`（`TextSender`、`CommandRunner`、`ProcessLauncher`）与
  既有工具类（`WindowFinder`、`MouseOps`、`TextLocator`、`ImageProbe`、`ScreenshotProbe`）。
- 旧实现过渡：`Features/Units/UnitValues.cs`（`Value` ↔ `VarValue`）+
  `LegacyFlow.cs`（把新上下文包装成 `RunContext`）；旧实现只读变量，不回写。
- `Value.AsText()` 对对象优先取 `text` 成员，于是 `${截图}` 仍是 `"x,y,w,h"`、
  `${匹配.x}` 才拿坐标。

**验收**：`ActionSmoke` 遍历注册表，校验 Key 唯一、`Spec` 完整、枚举选项齐全，
并跑通文本/数据/文件/命令/内联取值/错误文本；共 395 项断言通过。

### W3 迁移器 ✅

- `Features/Migration/ActionMigrator.cs`：旧 `ActionDto` → v2（旧 DTO 都在 `Features/`，不放 `Core/`）。
- 覆盖：17 种动作、16 种取值、7 种流程节点、`StepDto` 快照、`OutputVar`，以及 `sub.run` 调用。
- 未知字段 / 无法表达的语义：告警列表 + 原 JSON 备份，不静默丢。
- 顺带补齐：`input.cursor` 取值单元、`flow.if` / `flow.while` 的 `negate`、
  条件比较新增 `found` / `notFound` / `active` / `notActive`、`SubActionStore`（全局子动作库）、
  结构化结果统一的 `value` 输出（内联求值优先返回它）。

**验收**：`ActionSmoke` 迁移 `LTools.example.json` + 手工构造的旧动作，逐项断言结构，
并**执行**迁移结果（赋值 / 条件 / 两种循环 / 命令 / 子动作输出回写）；共 574 项断言通过。

### W4 编辑器 ✅

- `Views/ActionEditor/ActionEditorWindow`：三栏（工具箱 / 步骤 / 变量 + 动作属性）。
- 工具箱完全由 `StepRegistry` 的 `UnitSpec` 生成（分类 + 搜索）；点一下插到选中行之后。
  新增单元**不用改 UI 代码**即可出现并能编辑。
- 步骤行：图标 + 名称 + 一行摘要 + 输出变量 chip；缩进表示嵌套，
  「否则 / 结束如果 / 结束循环」是标记行，空分支有一行可选的占位行。
  增删改、上下移（`↑` / `↓`，或 `Alt+↑ / Alt+↓`）。
- **拖动排序**：拖起来后出现幽灵 + 插入线，松手才落数据（不做实时重排——
  步骤行是「重建出来」的扁平行，实时插入会与容器回收打架）。
  可落在任意步骤的上 / 下方（换成另一条分支就是「移入该分支」），
  也可落在空分支占位行上；标记行不是落点；**不能把一步拖进它自己的分支**。
- 参数表单 `Views/ActionEditor/StepEditorDialog`：按 `InputSpec.Editor` 生成
  文本 / 多行 / 数字 / 开关 / 枚举 / 文件 / 文件夹 / 图片 / 变量 / 条件表单；
  `VisibleWhen`（`key == 'value'`）实时收起 / 展开；输出按 `OutputSpec` 勾选映射到变量；
  `sub.run` 的按名参数由子动作变量表（`DynamicInputs`）生成。
- 值源 `Views/ActionEditor/ValueSourceEditor`：字面量 / 变量 / 内联取值单元三态，
  内联单元用同一个步骤表单继续编辑（递归到任意深度）。
- 变量面板：名称 / 类型 / 默认值；步骤输出用到的新变量自动登记。
- **子动作库**（设置 → 子动作库）：列表 + 新建 / 编辑 / 复制 / 删除 / 打开目录。
  编辑复用 `ActionEditorWindow.ForSubAction`——子动作与动作同构，只多一列「方向」
  （输入 / 本地 / 输出），id 自动生成且不可手改（它是对外调用名）。
- 执行桥：`ActionDto.Program` + `Features/Migration/ActionPrograms.For`（懒迁移并缓存）；
  `ActionExecutor` 在 `Program != null` 时改走 `ActionEngine`，旧格式到 W5 再说。
- 动作管理列表：`Program` 动作显示「动作 · N 步」（旧格式仍按原类型显示）。

暂缓（后续补）：

- 行内直接改关键参数：现在双击进对话框。要做成「点一下就地改」得先把步骤行从
  「一行摘要」改成「每个参数一个小 chip」，否则整行都变成输入框，选中 / 拖动全废。
  对话框已经默认聚焦第一个参数、Enter 即确定，所以暂不为此改行结构。

**验收**：新增一个单元**不改任何 UI 代码**即可出现在工具箱并能编辑。
`PanelSmoke` 真实构造三栏窗口 + 生成的参数表单 + 子动作编辑器 / 子动作库页并截图，
还验证了跨分支拖动与「自嵌套拦截」；`ActionSmoke` 覆盖
迁移缓存 / 深拷贝 / JSON 往返 / `Program` 执行桥（共 601 项断言）。

### W5 切流与删除

- 装配切换：`ActionRegistry` / `ActionExecutor` / `FlowRunner` 指向新引擎。
- 删除：`FlowNode` 系列、`Probe` 系列、`ActionExecutor` 的 switch、`FlowNodeDialog`、
  `ProbeEditor`、`ActionEditorDialog` 的按类型分支。
- 文档：`README.md`、`AGENTS.md`、本文件同步。

**验收**：全量冒烟通过；`git grep FlowNodeKind` 只剩迁移器里的历史映射。

---

## 6. UI / 交互

```
┌ 工具箱 ─────────┬ 步骤 ────────────────────┬ 变量 ─────────┐
│ 搜索框           │ ① 获取选中文字      → text │ 名   类型  默认 │
│ ▸ 文本           │ ② 文本处理 · 去空格 → clean│ text 文本  ""   │
│ ▸ 文件           │ ③ 如果 clean 为空         │ clean 文本  ""  │
│ ▸ 屏幕           │     └ 提示「没有内容」     │                │
│ ▸ 流程           │ ④ 写入剪贴板 ← clean      │ + 新建变量     │
└─────────────────┴───────────────────────────┴───────────────┘
```

- **步骤行**：图标 + 单元名 + 一行摘要（关键参数内联可改）+ 输出变量 chip。
- **参数编辑器**：由 `InputSpec` 生成；每个参数右侧是「取值」按钮（选变量 / 选取值单元）。
- **条件**：保留「值 + 比较 + 值」的表单形态（易用），底层存 `flow.if` 的输入；
  高级用户可切表达式。**不要**把裸表达式丢给普通用户。
- **变量面板**：类型 + 默认值；子动作里额外有「输入 / 输出」勾选。
- **子动作库**：与动作编辑器同构（步骤 + 变量 + 输入输出标记），入口在设置里。

---

## 7. 风险与取舍

| 风险 | 处理 |
|---|---|
| 一次切换回归面大 | 切流前完成等价性冒烟；切流提交只换装配；旧代码保留到验收通过再删 |
| 迁移丢字段 | 显式映射表 + 未知字段告警 + `.bak-*` 备份 |
| 单元过细导致工具箱爆炸 | 粒度定在「一类操作 + operation 参数」，目标 ~40 个 |
| 类型化后旧用法报错 | 文本是万能输入：绑定参数时按需解析，失败才报错 |
| 子动作改动影响面大 | 删除前列引用；全局改动只改行为，不改调用方数据 |
| 组合动作旧语义（快照） | 明确取舍：改为**引用**；需要独立副本时用「复制为子动作」 |

---

## 8. 冒烟

沿用 `%TEMP%\opencode\lpanel-smoke` 的 `PanelSmoke.exe` 结构，新增：

- `ValuesTest`：类型转换矩阵（text↔number/bool，list/object 成员与下标）。
- `UnitsTest`：反射遍历 `IStepUnit`，断言 Key 唯一、Spec 完整、默认值类型匹配。
- `MigrateTest`：旧动作迁移后执行结果与旧执行器逐项比对。
- `SubActionTest`：全局 + 动作内两种调用、三层嵌套、递归保护、删除引用提示。
- `EditorTest`：由 Spec 生成的表单能打开并保存每个单元。
