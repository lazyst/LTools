# 架构与数据布局（分阶段改造方案）

> 目标：把「一份大配置 + 共享资源池 + 巨型窗口」改成**按域存放、按动作归属、可单独备份与回收**的结构。
> 本文是执行清单：每个阶段都能独立构建、独立验收、独立回滚。

## 0. 不变量（改造期间始终成立）

1. **动作只能经 `ConfigStore` 改**，写入仍走「读磁盘 → 改 → 写回」，不引入第二套内存真源。
2. **每个持久化文件只有一个属主**：谁能写它、写哪些字段，在本文第 2 节列表里唯一。
3. **所有 JSON 带 `schemaVersion`**；升级只向前迁移，旧文件改名保留，不删除。
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
| 迁移逻辑分散（ini / LegacyType / PanelConfig / FlowMigration） | 没有统一 schema 版本，不可回滚 |

已经做对的一半：`subactions/` 是一子动作一文件、`staging/<id>/` 是一草稿一目录。
动作目录化只是把同一套做法推进到动作本身。

## 2. 目标布局

```
configs/
  settings.json      全局行为：CapsLock 开关、终端路径、鼠标速度、运行提示、提示位置、日志
  ui.json            界面状态：导航页、展开的分组、最近使用、OCR 偏好、编辑器折叠态
  actions.json       动作清单 + 菜单组（P2 起只留菜单组，动作迁到 actions/）
  panel.json         超级面板：开关 / 长按阈值 / 场景与分组 / 尺寸 / 上次页
  floating.json      悬浮项偏好（悬浮项本身仍然不落盘）
  macros.json        宏设置 + 宏元数据（事件仍在 macros/<id>.json）
actions/<id>/
  action.json        动作体（名称 / 图标 / 说明 / 变量 / 步骤）
  files/             该动作自己的模板图与脚本（P2）
resources/
  shared/            跨动作共享素材（P2 起，可选）
  user/              用户素材（永不自动删）
cache/screenshots/   运行期截图（可随时清空，P2/P4）
trash/<时间戳>/       删除的动作目录（保留 N 天，P2）
staging/  ai/  macros/  subactions/  速记/  logs/  ocr-models/   不变
```

**属主表**

| 文件 | 唯一写入者 | 读方 |
| --- | --- | --- |
| `configs/settings.json` | `ConfigBundle`（经 `ConfigIO`） | 设置页、托盘、钩子 |
| `configs/ui.json` | 同上 | 设置页、选择器、编辑器 |
| `configs/actions.json` | `MenuSystem.SaveToConfig` | `MenuSystem`、备份、AI 工具 |
| `configs/panel.json` | `PanelLibrary.Save` | 面板、设置页 |
| `configs/floating.json` | `FloatingLibrary.Save` | 悬浮窗、设置页 |
| `configs/macros.json` | `MacroStore` | 宏菜单、编辑器 |
| `actions/<id>/…` | `ConfigStore`（P2） | 引擎、面板、AI 工具 |

## 3. 阶段

### P1 · 配置分域（本阶段）

- 新增 `configs/ConfigBundle.cs` + `configs/ConfigFiles.cs`：把 `AppConfig` 的字段拆成 6 个域文件，
  每个文件带 `schemaVersion`；`AppConfig.Load/Save` 改为「合并读 / 只写变更的域」。
- 迁移：`configs/` 不存在时从 `LTools.json`（或 `LTools.example.json`）读种子；首次保存写出 6 个文件后，
  旧文件改名 `LTools.json.migrated`（不动内容，随时可回滚）。
- 原子写：先写 `<file>.tmp` 再 `File.Move(overwrite)`；坏文件按域备份为 `<file>.bad-<时间戳>`（每域只留 3 份）。
- 备份：导出改为打包 `configs/`；导入同时兼容「旧备份只有 LTools.json」。
- 验收：启动后 6 个文件齐全；改导航页只改 `ui.json`；删 `configs/` 并把 `LTools.json.migrated` 改回来能完整恢复。

### P2 · 一个动作一个目录

- `actions/<id>/action.json` + `actions/<id>/files/`；`configs/actions.json` 只留菜单组。
- 采纳草稿 = 把 `staging/<id>/` 搬成 `actions/<新id>/`（图片不迁移、不改写路径）。
- 模板图归属动作：动作内 `image` 写 `files/x.png`；共享图放 `resources/shared/`（按内容哈希去重，可选）。
- 删除动作 = 目录移入 `trash/<时间戳>/`（保留 30 天），三条引用（菜单 / 面板 / 悬浮）当场清理。
- 运行期截图迁到 `cache/screenshots/`，不再靠 `shot-` 前缀区分归属。
- 兼容：启动时若 `actions/` 为空且 `configs/actions.json` 有动作 → 逐条写成目录（**不删旧字段**，只标记已迁移）。

### P3 · 存储层收口

- `ConfigIO.Modify` 拆成按域的 store（`SettingsStore` / `UiStore` / `ActionStore` / `PanelStore` / `FloatingStore`），
  UI 只调自己那个；`AppConfig` 退化为启动快照 + 迁移兼容视图。
- 15 处 `ConfigIO.Modify` 逐个改成域写入；跨域操作（删动作要清菜单 / 面板 / 悬浮）做成一个显式的
  `ActionRemoval` 事务（顺序写 + 启动时幂等修复引用，不用分布式事务）。

### P4 · 代码结构

- `Views` 里 1000+ 行的窗口拆成「外壳 + 部件」：
  `SuperPanel` → 区容器 / 吸附导航 / 搜索 / 拖动；`ActionEditorWindow` → 步骤列表 / 工具箱 / 运行面板 / 变量表。
- 单元实现按分类分文件（`Features/Units/<分类>/`），新增单元不需要改注册中心。
- 迁移入口统一到 `Core/Storage/Migrations`：`schemaVersion` 驱动，一次只跑一步。

### P5 · 资源与备份

- 备份清单改为「目录白名单 + manifest 声明」，新增目录自动纳入。
- 启动时做一次孤儿检查（`actions/` 里的图无人引用 → 移入 `trash`，不直接删）。
- 提供「导出单个动作」= 打包一个动作目录。

## 4. 每阶段验收与回滚

| 阶段 | 验收 | 回滚 |
| --- | --- | --- |
| P1 | Debug/Release 0/0；6 个文件生成；只改一个域时其它文件 mtime 不变；旧文件仍可恢复 | 删 `configs/`，把 `LTools.json.migrated` 改回 `LTools.json` |
| P2 | 采纳草稿后 `actions/<id>/` 里直接有图；删动作进 `trash/`；面板 / 菜单 / 悬浮引用清空 | 保留 `configs/actions.json` 的旧动作清单，回退版本直接读它 |
| P3 | 15 处写入点各自只碰一个文件；并发保存不互相覆盖 | 域文件格式不变，退回旧版本仍能读 |
| P4 | 窗口拆完后交互与原版一致（跑一遍手动清单） | 纯代码改动，git revert |

## 5. 明确不做

- 不做跨文件事务 / 数据库：单进程 + 每域单写者 + 启动修复引用，够用且可读。
- 不为兼容旧版本同时写两份配置（迁移期除外）。
- 不把 `速记/`、`logs/`、`ai/` 收进 `configs/`：它们不是一个域，且速记是用户资产。
