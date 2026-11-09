# 架构与数据布局（分阶段改造方案）

> 目标：把「一份大配置 + 共享资源池 + 巨型窗口」改成**按域存放、按动作归属、可单独备份与回收**的结构。
> 本文是执行清单：每个阶段都能独立构建、独立验收、独立回滚。

## 0. 不变量（改造期间始终成立）

1. **动作只能经 `ConfigStore` 改**，写入仍走「读磁盘 → 改 → 写回」，不引入第二套内存真源。
2. **每个持久化文件只有一个属主**：谁能写它、写哪些字段，在本文第 2 节列表里唯一。
3. **所有 JSON 带 `schemaVersion`**（只作标注）；**不做向后兼容**：结构变了直接改读写代码与示例文件，不写迁移器、不认旧字段、不留 `.migrated` 归档。
4. **路径解析只有一个入口**：数据根内相对路径走 `AppPaths` / `ImageLibrary`，不在各处手拼。
5. **引用型资源按引用回收**（宏、模板图、悬浮项、面板槽位），删除动作后立刻清理。
6. **构建 0 错 0 警**，每个阶段真机走一遍相关路径再提交。

## 1. 现状（改造前）

数据根（dev = 仓库根，prod = exe 同级）：

```
LTools.json              动作 + 菜单组 + 面板 + 悬浮 + 宏元数据 + 日志 + 终端路径 + UI 偏好
LTools.example.json      模板（随包分发）
macros/<id>.json         宏事件
subactions/<id>.json     全局子动作（已经是一文件一个）
resources/images/        模板图 + 运行期截图（按 shot- 前缀 + 开始时间界线回收）
resources/user/          用户素材（永不自动删）
staging/<id>/            AI 草稿（program.json + files/）
ai/                      令牌 / 审计 / 知识包
速记/  logs/  ocr-models/
```

问题：

| 问题 | 影响 |
| --- | --- |
| `LTools.json` 一个文件装七种域，15 处调用点各自「整份 load → 改 → 保存」 | 改一个导航页会重写全部动作；写冲突与写放大都集中在这一个文件 |
| 坏一处全丢；diff 不可读 | 无法按域恢复、评审、分享 |
| 模板图与运行期截图混在 `resources/images` | 回收靠文件名前缀 + 时间界线，规则脆弱 |
| 备份清单硬编码（LTools.json / resources / 速记 / macros / staging） | 新增目录不会自动进备份 |
| `SuperPanel` 1458 行、`ActionEditorWindow` 1318 行、`ConfigHelper` 1277 行、`PanelManageView` 1227 行 | 改一处要在大文件里翻，评审与 AI 定位都慢 |
| 旧兼容层散在读写路径（ini 解析 / `LegacyType` / `PanelConfig` 旧页 / `FlowMigration`） | 读路径里藏着一次性转换，删不掉也测不清（已全部移除） |

已经做对的一半：`subactions/` 是一子动作一文件、`staging/<id>/` 是一草稿一目录。
动作目录化只是把同一套做法推进到动作本身。

## 2. 目标布局

```
configs/
  settings.json      全局行为：CapsLock 开关、终端路径、鼠标速度、运行提示、提示位置、日志
  ui.json            界面状态：导航页、展开的分组、最近使用、OCR 偏好、编辑器折叠态
  hotkeys.json       快捷键：预设组 + 「快捷键 → 动作」绑定 + 全局默认白 / 黑名单
  panel.json         超级面板：开关 / 长按阈值 / 场景与分组 / 尺寸 / 上次页
  floating.json      悬浮项偏好（悬浮项本身仍然不落盘）
  macros.json        宏设置 + 宏元数据（事件仍在 macros/<id>.json）
actions/<id>/
  action.json        动作体（名称 / 图标 / 说明 / 变量 / 步骤）
  files/             该动作自己的模板图与脚本（P2）
resources/
  images/            共享 / 历史模板图（P2 起新增的模板图优先放动作目录）
  user/              用户素材（永不自动删）
trash/<时间戳>/<id>/  删除的动作目录（保留 30 天，可手工捞回）
staging/  ai/  macros/  subactions/  速记/  logs/  ocr-models/   不变
```

**属主表**

| 文件 | 唯一写入者 | 读方 |
| --- | --- | --- |
| `configs/settings.json` | `SettingsStore` | 设置页、托盘、钩子 |
| `configs/ui.json` | `UiStore` | 设置页、选择器、编辑器 |
| `configs/hotkeys.json` | `HotkeyStore`（经 `HotkeyRegistry.Load` / 快捷键页） | `HotkeyRegistry`、`HotkeysView`、备份 |
| `configs/panel.json` | `PanelStore`（经 `PanelLibrary.Save`） | 面板、设置页 |
| `configs/floating.json` | `FloatingStore`（经 `FloatingLibrary.SavePreferences`） | 悬浮窗、设置页 |
| `configs/macros.json` | `MacroFileStore`（经 `MacroStore`） | 宏菜单、编辑器 |
| `actions/<id>/…` | `ActionStore`（经 `ConfigBundle` / `ConfigStore`） | 引擎、面板、编辑器、AI 工具 |

所有单域写入经 `ConfigIO.ModifyDomain`（一把锁 + 严格读）；读仍合并整份聚合，所以首次升级能带上
旧文件的种子，分域完成后每次只碰被点的那个文件。

## 3. 阶段

### P1 · 配置分域（已完成）

- 新增 `Config/ConfigBundle.cs` + `Config/ConfigFiles.cs`：把 `AppConfig` 的字段拆成 6 个域文件，
  每个文件带 `schemaVersion`；`AppConfig.Load/Save` 改为「合并读 / 只写变更的域」。
- 种子：`configs/` 不存在时从同目录的 `LTools.example.json` 读种子；**不做旧文件迁移**——旧 `LTools.json`、
  旧域文件、`.migrated` 归档一律不再读，缺哪个域就用示例默认值补哪个域。
- 原子写：先写 `<file>.tmp` 再 `File.Move(overwrite)`；坏文件按域备份为 `<file>.bad-<时间戳>`（每域只留 3 份）。
- 备份：导出打包 `configs/`；导入只认 `configs/`（旧版单文件备份不再支持）。
- 验收：启动后 6 个文件齐全；改导航页只改 `ui.json`；删 `configs/` 后重启按示例重新播种。

### P2 · 一个动作一个目录（已完成）

- 新增 `Core/Storage/ActionStore.cs`：`actions/<id>/action.json` + `actions/<id>/files/`；
  动作清单不再进配置文件（P4 起菜单组也改成了 `hotkeys.json`）。
- 种子：`actions/` 不存在时按示例动作建目录；**不读旧 `configs/actions.json`**；
  **`actions/` 目录存在（含空）即权威**，所以删光动作不会被示例复活。
- 写回只碰变了的动作：`ActionStore.SaveAll` 逐条比较 `action.json` 内容，没变不写；
  清单里没有的目录移入 `trash/<时间戳>/`（保留 30 天，启动时清理过期项）。
- 采纳草稿 = 把 `staging/<id>/files/*` 复制进 `actions/<id>/files/`，动作里的 `files/x.png` **原样保留**，
  不再改写路径、也不再搬进共享的 `resources/images`。
- 运行期资源根：正式运行（`AutomationRunner.Run`）与编辑器（`ActionEditor.Show`）都传该动作的 `files/`
  （`ActionStore.ResourceRootFor`），解析顺序仍是 `ImageLibrary.Resolve` 的四级回退。
- 备份：导出打包 `actions/`（含动作私有图）；导入旧备份时让现有 `actions/` 退位，
  让恢复出来的 `configs/actions.json` 生效，下次保存再重建目录。
- P5 已完成：运行期截图改落 `cache/screenshots/`（见下），编辑器新截的模板图仍进 `resources/images/`。

### P3 · 存储层收口（已完成）

- 新增 `Config/ConfigDomain.cs` + `Config/DomainStores.cs`：`SettingsStore` / `UiStore` / `HotkeyStore` /
  `PanelStore` / `FloatingStore` / `MacroFileStore`；每个 store 只读改写自己那个域文件
  （`ConfigIO.ModifyDomain` 一把锁 + 严格读，写仍走 `WriteIfChanged`）。
- 15 处 `ConfigIO.Modify` 全部改成域写入；跨域只剩「动作清单 + 快捷键」与「动作清单 + 面板」
  （`ConfigIO.Modify(ConfigDomain.actions | ConfigDomain.hotkeys, …)`，`HotkeySeeding` / `ActionLibrary` 用）。
- 首装兜底：域文件不全时第一次写入自动扩成整份（6 个域 + `actions/`）；不归档、不改名任何旧文件。
  分域完成后改一个开关只动目标 `configs/*.json`，不再扫动作目录、不再重写别的域。
- 删除动作收口到 `Core/Storage/ActionRemoval`：顺序固定「内存清单 → 快捷键绑定 + 面板槽位 →
  动作目录 → 宏 / 模板图 / 悬浮项回收」；启动时按磁盘动作目录幂等清掉快捷键与面板里的
  悬空引用（在 `ActionLibrary` / `PanelLibrary` 加载之前跑，没悬空就不写盘）。
- 验收：Debug/Release 0/0；用自检程序逐个域写入，只有目标文件 mtime 变化、`actions/*.json` 不动；
  人为塞一个不存在的动作 Id 进快捷键 / 面板，启动修复只重写这两个文件。回滚：文件格式不变，退回旧版本即可。

### P4 · 代码结构（已完成）

- `Views` 里 1000+ 行的窗口拆成「外壳 + 部件」（partial，类名 / XAML 不变，只搬代码）：
  - `SuperPanel.xaml.cs`（1669 → 449 行）= 外壳 + 未配置提示 + 关闭保存；
    `SuperPanel.Build.cs`（渲染 / 场景标签 / 分区 / 分组导航 / 磁贴）、
    `SuperPanel.Navigation.cs`（搜索 / 选中 / 键盘 / 左栏页面切换 / 窗口事件）、
    `SuperPanel.Menus.cs`（右键菜单 + 磁贴 CRUD）。
  - `ActionEditorWindow.xaml.cs`（1568 → 355 行）= 外壳 + 字段 / 构造函数 + 行模型；
    `ActionEditorWindow.Steps.cs`（步骤列表 / 工具箱 / 增删改）、
    `ActionEditorWindow.DragSort.cs`（拖动排序）、
    `ActionEditorWindow.Variables.cs`（变量表）、
    `ActionEditorWindow.Run.cs`（保存 + 试运行）。
- 单元实现按分类分文件（`Features/Units/<分类>/<XxxUnit>.cs`，一个单元一个文件），
  共用的静态小工具留在同目录（`FileText.cs` / `Crc32.cs`）；新增单元仍只在 `BuiltInUnits` 加一行。
- **不做迁移层**：配置 / 动作结构变化时直接改读写代码与 `LTools.example.json`，`SchemaVersion` 只作标注；
  启动时没有 `MigrationRunner` / `ConfigSchema` 这类版本分支（曾经有，已随「不要向后兼容」删除）。

### P5 · 资源与备份（已完成）

五条都按「目录边界 + 显式声明」落地，不再靠文件名前缀或散在各处的特判：

- **运行期截图独立目录**：`Features/Flow/ImageLibrary.CaptureDir` = `<数据根>/cache/screenshots/`，
  「截图」步骤走 `SaveCapture`；下一次运行开始（`AutomationRunner.RunCore`）与程序启动（`App.OnStartup`）
  各调一次 `ClearCaptures()` 整体清空。`resources/images/` 只留模板图 / 剪贴板图，继续按引用 + 时间界线回收；
  `cache/` 不进备份、不进仓库（`.gitignore`）。
- **跨动作素材去重**：采纳草稿复制 `staging/<id>/files/` 时，`ActionStore.CopyFilesFrom` 先按体积建索引、
  再比 SHA-256；命中别的动作已有的同内容文件就用**硬链接**（`Native/FileLinks`）指向它，失败回退复制。
  写目标文件前一律先删旧名字（硬链接共享内容，就地覆盖会连带改掉另一个动作的模板图）。
- **备份目录白名单**：`Core/BackupService` 的 `BackupDomain` 表列出唯一允许进包的目录
  （配置 / `actions` / `resources` / `速记` / `macros` / `staging`），`Export`、`EstimateSize`、
  manifest 的 `includes` 都从同一张表生成；新增一类目录只加一行，`cache/`、`ai/`、`logs/` 永远不进来。
  导出先写 `.part-*` 再替换，失败不留半个 zip。
- **启动孤儿检查**：`Core/Storage/OrphanFiles.Collect`（`ConfigStore.Initialize` 里调用）扫
  `actions/<id>/files/` 的图片，把没有任何 `image` 参数引用的移进 `trash/<时间戳>/orphans/<id>/`（只搬不删）。
  只处理有 `Program` 的动作；出现 `${变量}` / `{"var": …}` / 内联取值这类动态引用时整个动作跳过。
- **导出单个动作**：`BackupService.ExportAction` 打一个 `.ltact`（`manifest.json` + `action.json` + `files/…`），
  入口在面板磁贴右键与「动作管理」磁贴右键（`PanelActionOps.Export`，后台导出 + toast 报路径）。

### P6 · 全局快捷键（已完成）

- CapsLock 从「写死的手势表」变成「动作 + 快捷键」：`Config/Hotkeys.cs` 定义 `HotkeyFile` /
  `HotkeyGroup` / `HotkeyBinding` / `AppFilter` / `HotkeyScope`，域文件 `configs/hotkeys.json`
  （域文件里的槽位就是 `hotkeys`，没有旧 `menus` 概念，也不做旧文件合并）。
- 匹配在 `Hooks/KeyboardHook` 里、录制透明分支之后、`CapsLockStateMachine.TryHandle` 之前：
  `HotkeyRegistry.TryFire` 按「组顺序 → 绑定顺序」精确比较修饰键（`RegisterHotKey` 不支持
  CapsLock 修饰键，所以不用它）；命中就吞掉并投递动作，没命中的键照常下传。
- 白 / 黑名单：钩子回调里只做字符串比较，前台 HWND 由 `ForegroundAppCache`
  （`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` + 后台线程解析进程名 / 标题 / 类名）维护；
  取不到时放行，LTools 自己的窗口不触发。
- 预设：`Features/HotkeySeeding` 首次启动播种「CapsLock 经典」（SeedVersion = 3），
  20 条**全部是内置功能引用**（5 个入口 + 光标移动 / 文本选择 / 删除与撤销 15 条）；
  不再生成预置动作，用户能把同一批条目放进面板 / 悬浮 / 换键。
  `HotkeyFile.SeedVersion` 保证删掉的条目不会复活；功能 Id 没有别名表，改名就同步改种子与用户配置。
- **内置功能目录**（P7）：`Features/Builtin/FeatureCatalog` 提供「常用功能 `common.*`」与
  「LTools 功能 `ltools.*`」两套只读入口，参数声明在 `FeatureDef`，引用编码成 `id#key=value&…`
  （`FeatureRef`），直接存进面板 / 快捷键 / 悬浮原有的字符串字段，**不升 schema**；存活判断与
  显示名统一走 `BindingRef`。它们不是步骤单元（`internal.*` 七个单元已删），动作编辑器左栏不再有
  「LTools 内置功能」分类；`CapsLock+Space`（旧鼠标模式）留空。
- 旧的 10 个数字菜单组、帮助面板、独立剪贴板、符号定位、词跳词选等手势与实现一并删除，
  不再有第二套解释器。

## 4. 每阶段验收与回滚

| 阶段 | 验收 | 回滚 |
| --- | --- | --- |
| P1 | Debug/Release 0/0；6 个文件生成；只改一个域时其它文件 mtime 不变；删 `configs/` 后按示例重新播种 | 删 `configs/`，重启（示例即默认值） |
| P2 | 采纳草稿后 `actions/<id>/files/` 里直接有图、引用仍是 `files/x.png`；删动作进 `trash/`；改一个动作只重写它自己的 `action.json`；删光动作后重启不复活 | 删 `actions/`（下次启动按示例重建）+ git revert |
| P3 | 逐个域写入只有目标 `configs/*.json` 的 mtime 变化、`actions/*.json` 不动；并发保存不互相覆盖；菜单 / 面板里的悬空动作 Id 在启动时被清掉 | 域文件格式不变，退回旧版本仍能读 |
| P4 | 窗口拆完后交互与原版一致（面板建窗 / 切暂存区 / 编辑器载入动作均正常）；单元清单数量不变；启动时没有任何迁移扫描 | 纯代码改动，git revert |
| P5 | 截图落在 `cache/screenshots/` 且下次运行 / 重启后被清空；同内容图采纳到第二个动作时 `fsutil hardlink list` 显示两份名字、改内容只影响一个动作；孤儿图移入 `trash/orphans/`、动态引用动作不动；备份 manifest 的 `includes` 与 zip 条目一致、`cache/` 不出现；`.ltact` 含 manifest + action.json + files/ | 纯代码 + 目录调整，退回旧版本仍能读写（`cache/` 只是多了个目录） |
| P6 | 删掉绑定重启不复活；`CapsLock+S` 不在 `CapsLock+Ctrl+S` 时触发；名单里的程序与 LTools 自己的窗口不触发；快捷键页改完立即生效（不必重启） | 删 `configs/hotkeys.json`，重启按示例 / 播种重建 |

## 5. 明确不做

- 不做跨文件事务 / 数据库：单进程 + 每域单写者 + 启动修复引用，够用且可读。
- 不做向后兼容：不写配置 / 动作迁移器、不认旧字段与旧文件名、不留 `.migrated` 归档、不维护功能 Id 别名表。
- 不把 `速记/`、`logs/`、`ai/` 收进 `configs/`：它们不是一个域，且速记是用户资产。
