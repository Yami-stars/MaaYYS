# MaaYys

基于 [MaaFramework](https://github.com/MaaXYZ/MaaFramework) 的《阴阳师》Windows 官方桌面端自动化助手。

- 功能设计基准：MaaYYs-win32 MXU v3.13.6
- UI / 设置体系基准：MAA v6.20.1
- 当前阶段：**P6 UI 与设置体系**（P1–P5 已全部完成；P5 验收见 `docs/MaaYys-开发流程表.md` §16–§19，P6.1 见 `docs/P6.1-主界面布局.md`）

> 本项目仅用于学习与交流，请自行承担使用风险。

## 目录结构

```
MaaYYS/
├── MaaYys.sln              # 解决方案
├── src/
│   ├── MaaYys.App/          # WPF GUI：Views / ViewModels / Themes / Services（InstanceWorkspace、位图转换）
│   ├── MaaYys.Engine/       # 引擎封装：Native（P/Invoke）、Devices（探测/帧泵）、Config（实例清单）、Session（状态机）
│   └── MaaYys.P1Probe/      # P1 链路验证控制台（已冻结，仅用于回归验证）
├── tests/                  # xUnit 测试（MaaYys.Engine.Tests，118 个用例）
├── config/                 # 运行期配置（instances.json 等，便携优先；不入库）
├── agent/                  # Go agent（自定义 action/recognition，maa-framework-go）
├── third_party/maafw/      # MaaFramework 运行库副本（不入库，来源见下）
├── environment/            # Go SDK 与 GOPATH（不入库）
└── docs/                   # 规划文档 + reference/（官方 JSON Schema）
```

## 构建

前置：.NET SDK 9+、Go 1.25+（Go 位于 `environment/go`，无需系统安装；
若 SDK 低于 1.25，`go build` 会通过 toolchain 机制自动下载所需版本）。

```powershell
# C#
dotnet build MaaYys.sln

# Go agent（GOPROXY 用国内镜像可显著提速）
$env:GOPATH  = "D:\WorkBuddy-Work\MaaYYS\environment\gopath"
$env:GOCACHE = "D:\WorkBuddy-Work\MaaYYS\environment\gocache"
$env:GOPROXY = "https://goproxy.cn,direct"
& "D:\WorkBuddy-Work\MaaYYS\environment\go\bin\go.exe" build -o agent.exe ./agent
```

运行各阶段验证：

```powershell
# GUI（P2.1 起）
dotnet run --project src/MaaYys.App

# 单元测试（P2.2 起）
dotnet test tests/MaaYys.Engine.Tests

# P1 链路回归
dotnet run --project src/MaaYys.P1Probe -- engine   # P1.2
dotnet run --project src/MaaYys.P1Probe -- pi       # P1.3
dotnet run --project src/MaaYys.P1Probe -- win32    # P1.4（--click/--resize/--screencap-method 等）
dotnet run --project src/MaaYys.P1Probe -- agent    # P1.5（自动拉起 Go agent 子进程）
```

## 运行库来源

`third_party/maafw/` 复制自 [MaaFramework 官方 Release](https://github.com/MaaXYZ/MaaFramework/releases/tag/v5.15.0-beta.1)
的 `MAA-win-x86_64-v5.15.0-beta.1.zip`（`bin/` 目录），内置 MaaFramework **v5.15.0-beta.1**。
原始 zip 备份于 `D:\WorkBuddy-Work\MAA-win-x86_64-v5.15.0-beta.1.zip`，解压副本在 `environment/`。
该目录已加入 `.gitignore`，换环境时需重新复制。

> 版本迁移记录：v5.13.0-beta.2（源自 MaaYYs-win32 分发包）→ v5.15.0-beta.1（官方 Release）。
> 两版核心头文件（MaaTasker / MaaResource / MaaCustomController / MaaMsg / MaaBuffer）完全一致，
> P/Invoke 层无需改动即完成升级；新版新增 WebGPU 推理支持（dxcompiler / dxil / webgpu_dawn_maa）。

## P1 进度

| # | 任务 | 状态 |
|---|---|---|
| 1.1 | 开发环境搭建与脚手架 | ✅ 完成 |
| 1.2 | 引擎集成验证 | ✅ 完成 |
| 1.3 | PI V2 协议解析验证 | ✅ 完成 |
| 1.4 | Win32 控制链路验证 | ✅ 完成（含 4 项超出预期的架构结论） |
| 1.5 | Agent 链路验证 | ✅ 完成 |
| 1.6 | 技术方案评审 | ✅ 完成（结论：`docs/P1.6-技术方案评审.md`） |

## P1.6 评审结论（2026-10-26，冻结为 P2 基线）

| 决策项 | 结论 |
|---|---|
| 绑定方式 | **自研 P/Invoke** 固化；全部 `DllImport` 集中在 `MaaYys.Engine/Native/`，上层只见托管包装，禁止暴露 `IntPtr` |
| agent 语言与版本 | **Go 1.25 + `github.com/MaaXYZ/maa-framework-go/v4` v4.0.0**（v1.x 无 AgentServer） |
| 截图 | 默认 **FramePool**，`CreateWithFallback` 在连接失败/空图/全黑/黑边>10% 时回退 PrintWindow |
| 输入 | **混合输入**：打开面板时 `SetForegroundWindow` 一次，面板内全程后台消息；默认鼠标方式 SendMessage，设置项可切换 |
| DPI | 进程必须声明 PerMonitorV2（WPF 工程在清单层声明） |
| 配置位置 | 程序目录 `config/gui.json`（便携优先），缺失回退 `%LOCALAPPDATA%/MaaYys/` |
| 目录规范 | 仓库根 `interface.json`；`resource/base` + `resource/<渠道>` 覆盖层；`tasks/` 29 个任务定义 |

agent 侧硬性约束（违反即崩溃）：`BindResource` 必须先于 `Connect`；`Resource` 必须在 `Disconnect` 之后销毁；
`MaaStringListBufferAt` 返回借用指针需再经 `MaaStringBufferGet` 解引用。

## P1.5 验证结果（2026-10-26）

`dotnet run --project src/MaaYys.P1Probe -- agent`：探针（C#，AgentClient）自动拉起 Go agent 子进程并完成一次完整调用。

- 握手：`AgentClient.CreateTcp(7788)` → `identifier=7788` → 启动 `agent.exe 7788 --libdir <maafw>` → `Connected=True Alive=True`
- 注册清单回读：`custom_action=[MaaYysProbeAction]`、`custom_recognition=[MaaYysProbeRecognition]`
- pipeline 调用：`recognition` 收到 `param={"probe":"p15"}`、`roi=[0 0 720 720]`、图像非空；`action` 收到 `param={"hello":"maayys"}`、`box=[0 0 10 10]`（即 recognition 返回的框）
- 结果：`PostTask -> Succeeded`，agent 侧痕迹文件校验通过，子进程 `exit=0` 优雅退出

关键结论（P4 架构依据）：
1. **Go 绑定必须走 v4 模块路径**：`github.com/MaaXYZ/maa-framework-go/v4`（MaaYYs 的 agent 亦用 `v4.0.0-beta.12`，已用 `go version -m` 对其 exe 实证）。
   v1.x 线（最高 v1.7.0）**不含 AgentServer 绑定**，无法做 agent。
2. **v4.0.0 要求 Go 1.25.0**；本地 SDK 为 1.24.12，但 Go 的 toolchain 机制会自动下载 go1.25.0，无需手工升级 SDK。
3. **调用顺序有硬约束**：`BindResource` 必须在 `Connect` 之前（否则框架报 `resource is not bound` 拒绝握手）。
4. **销毁顺序有硬约束**：`Resource` 必须在 `AgentClient.Disconnect` 之后销毁，否则 client 内部持有悬垂指针，Disconnect 触发 `0xC0000005`。
5. `MaaStringListBufferAt` 返回的是借用的 `MaaStringBuffer*`，需再经 `MaaStringBufferGet` 取 `char*`（直接当字符串读会得到乱码）。

## P2 进度

| # | 任务 | 状态 |
|---|---|---|
| 2.1 | GUI 骨架（WPF） | ✅ 完成（骨架） |
| 2.2 | 引擎封装层 | ✅ 完成（22/22 单元测试通过） |
| 2.3 | 设备管理 | ✅ 完成（桌面端实测；ADB 待模拟器环境） |
| 2.4 | 实例管理 | ✅ 完成（55/55 测试；增删改切换实测） |
| 2.5 | 资源加载与切换 | ✅ 完成（60/60 测试；资源随 exe 部署；官服 ↔ B站服 热切换实测 219ms / 217ms） |
| 2.6 | 配置系统 | ✅ 完成（82/82 测试；命名组 + Current + ConfigVersion + 导入导出 + P2.4 迁移实测） |

## P3 进度（**8/8 全部完成**）

| # | 任务 | 状态 |
|---|---|---|
| 3.1 | pipeline 移植与整理 | ✅ 完成（50/50 文件可解析、0 条悬空引用；框架侧 1511 节点一一对应；删 `test.json`、修 `寮3042`） |
| 3.1+ | 缓存清理与资源整理 | ✅ 完成（设置页「清理图片缓存」按钮；过期资源在资源版本更新时自动整理，直接删除；`probe audit` / `probe cache`） |
| 3.2 | 模板图移植 | ✅ 完成（710 张全量迁入；**604 处 template 引用 / 431 张，缺失 0**；去重与压缩评估后不做） |
| 3.3 | ONNX 模型移植 | ✅ 完成（15 个 `NeuralNetworkDetect` 节点用的 4 个模型 100% 有文件；OCR 已受保护；推理待实机） |
| 3.4 | 任务定义与选项 | ✅ 完成（任务页渲染 29 任务 / 94 选项 / 75 行面板；四种类型可编辑；`pipeline_override` 编译 + 联动子选项 + 占位符注入） |
| 3.5 | 渠道服分层重构 | ✅ 完成（渠道层只写差异；修华为/应用宝不跳开场动画、vivo 关不掉游戏；`StartApp/StopApp` 绑定 + `probe channel`） |
| 3.6 | interface.json 编写 | ✅ 完成（PI schema 校验 0 错误；官方 MaaPiCli 全流程读通、与 MXU 结构 0 差异；Win32 参数可配置化） |

## P4 进度（**4/4 全部完成** — M3.5 Agent 能力就绪里程碑达成）

详见 [`docs/P4-agent功能移植.md`](docs/P4-agent功能移植.md)、[`docs/MaaYys-开发流程表.md`](docs/MaaYys-开发流程表.md) §15。

| # | 任务 | 状态 |
|---|---|---|
| 4.1 | agent 框架搭建 | ✅ 完成（`agent/` Go 工程，maa-framework-go **v4**；`MaaAgentSession` 封装 拉起→TCP 握手→BindResource→退出回收，接入 `MaaYysEngine.ConnectAsync`，best-effort 降级） |
| 4.2 | 核心动作移植（8） | ✅ 完成（RandomWait / SwitchSoul / ClearHitCount / RepeatChallengeNTimes / BonusToggleAction / TimeCheck / RandomSwipe / ReStartGame 全部注册；`RandomWait` 经 pipeline 跑通 Succeeded） |
| 4.3 | 高级动作移植（9） | ✅ 完成（HumanTouch / ChallengeDungeonBoss / BattleTeam / RandomTouch / RandomTask / QuestionMatcher / Guess / AutoFoster / AutoBattle 全部注册；游戏 UI 类动作标注「待真机校准」） |
| 4.4 | 自定义识别移植（2） | ✅ 完成（`TaskCounterRecognition` 调用计数近似、`CoBattleTargetRecognition` 组队目标识别，均注册可用） |

**验证**：`probe agent` 连接成功、19 能力清单齐全；173/173 测试（含 3 个 agent 集成测试）、0 警告 0 错误。

**遗留**：MXU 的 `agent/` 仅含 `agent.exe` 无源码，游戏交互类动作坐标/流程按 param schema 重建，需真机微调。

## P5 进度（**5/5，已完成**）

详见 [`docs/MaaYys-开发流程表.md`](docs/MaaYys-开发流程表.md) §6.2。

| # | 任务 | 状态 |
|---|---|---|
| 5.1 | 任务队列系统 | ✅ 完成（多实例唯一 Id / 拖拽重排 / 别名 / 启用禁用 / 运行态色条 / 右键「从此处运行」；`TaskConfigurationTests` 8 例） |
| 5.2 | 选项系统 | ✅ 完成（全局级选项 MXU `setting` 扩展；`Compile` 全局层合并 + `CompileGlobal`；任务页「任务参数 / 全局选项」双页；190/190 测试） |
| 5.3 | 任务执行引擎 | ✅ 完成（`Engine/Execution/`：`QueueExecutor` 顺序投递 + 停止/暂停 + 重试 + 停滞超时 + 时长上限；`ExecutionPlan` 编译投递契约；任务页运行控制条；209/209 测试） |
| 5.4 | 定时任务 | ✅ 完成（`Engine/Scheduling/`：`TimerRules` 周计划位掩码 + `TimerScheduler` 20 秒轮询 / 同分钟去重 / 重启不补跑；`QueueRunService` 统一执行入口，手动与定时共用；`TimerRunner` 强制定时启动；「定时」页行内编辑 + 试跑；229/229 测试。开机自启改归 P6.2 设置中心-系统类） |
| 5.5 | 日志系统 | ✅ 完成（`Engine/Logging/`：`LogBuffer` 环形缓冲 + 原生事件翻译 + agent stdout/stderr 并入 + 截图缩略卡片 + 文本/CSV 导出；`LogOptions` 时间格式持久化；「日志」页过滤/滚动/导出；266/266 测试。顺带修掉暗色主题 CheckBox 标签不可见的全局渲染问题） |

## P6 进度（**2/5**）

详见 [`docs/MaaYys-开发流程表.md`](docs/MaaYys-开发流程表.md) §20 / §22、[`docs/P6.1-主界面布局.md`](docs/P6.1-主界面布局.md) 与 [`docs/P6.2-设置中心.md`](docs/P6.2-设置中心.md)。

| # | 任务 | 状态 |
|---|---|---|
| 6.1 | 主界面布局 | ✅ 完成（**MAA 式顶部四页签**：一键长草 / 多实例 / 小工具 / 设置；一键长草 = 左任务卡（勾选 + 齿轮 + ⊕/全选/清空）+ 完成后 + `Link Start!`，右「多任务共用 + 常规/高级设置」；截图移到多实例，定时/日志/设备探测进小工具；1280×760 / 1040×660 / 明暗双主题截图无截断，266/266 测试） |
| 6.2 | 设置中心-系统类 | ✅ 完成（设置页改**左侧五分类导航**：连接 / 启动 / 运行 / 日志与缓存 / 关于；`config/maayys.json` 新增 `settings` 四组持久化 + 即时生效（无保存按钮）；文件日志落盘、开机自启（HKCU Run）、前置程序自动启动、防休眠、前后置脚本、完成后动作统一由 `QueueRunService` 执行；顺带修好 TextBox/ComboBox/CheckBox 暗色白底与 `DisplayMemberPath` 失效；291/291 测试） |
| 6.3 | 设置中心-应用类 | 待开始 |
| 6.4 | 界面细节打磨 | 待开始 |
| 6.5 | 任务设置面板 | 待开始 |

### 缓存清理与资源整理（2026-10-26）

详见 [`docs/3.1-缓存清理与资源整理.md`](docs/3.1-缓存清理与资源整理.md)。

- **设置 → 图片缓存 → 清理图片缓存**：一个按钮，弹确认后直接删 `debug/`、`cache/` 下的图片（日志保留），不进回收站。
- **资源整理自动做**：`interface.json` 的 `version` 或资源指纹变化时，启动后台扫一遍、直接删掉旧版本残留（不可达节点、未引用模板图/模型、`*.bak`），结果写 `debug/resource-prune.log`；用户不需要点。
- **三个未接线的任务保持隐藏**（`tasks/限时活动.json`、`活动-小超鬼王.json`、`自动业原火.json`）：
  import 不动、72 个节点保留，登记成「受保护根」，整理永远不碰；启用只需在 import 里加一行。
- 判定门槛：**8 个资源集取交集**（任一集还用到就保留），受保护项（隐藏任务 / 活动轮换包 / `debug.json` / OCR 模型）永不处理。

```powershell
MaaYys.P1Probe.exe audit              # 只扫描（只读）
MaaYys.P1Probe.exe audit --prune      # 执行整理（多资源集取交集，直接删除）
MaaYys.P1Probe.exe cache --clear      # 清空图片缓存（= 界面按钮）
```

实测 v3.13.6：1511 节点（不可达 373）→ **删 278 个文件 + 重写 9 个文件 = 清掉 214 个节点，释放 23.2 MB**；
剩下 154 个不可达**全部是受保护项**，0 悬空引用，资源体积 99 MB → 76 MB。

### 任务定义与选项（2026-10-26）

任务页（左侧「任务」）就是 P3.4 的交付物：左边勾选任务、右边编辑参数、底部看编译结果。
详见 [`docs/P3.4-任务定义与选项.md`](docs/P3.4-任务定义与选项.md)。

### interface.json 与 PI 校验（2026-10-27）

详见 [`docs/P3.6-interface.json.md`](docs/P3.6-interface.json.md)。

- **PI V2 schema 校验器**：`MaaYys.P1Probe.exe pi --validate resource/interface.json` →
  错误 0 · 警告 1（Adb 段缺省，预期内）。覆盖必填/类型、跨段引用、路径存在性、Win32 参数名；
  键鼠/截图方式写错名字直接报 Error——框架对未知名字是静默回退，不拦会悄悄改变行为。
- **Win32 参数可配置化**：清单 `controller[].win32` = 默认值，实例字段 = 覆盖；
  新建实例用清单默认值预填。顺手把 `win32.screencap` 从 `PrintWindow` 修正为 `FramePool`
  （P1.4 实测 PrintWindow 在 DPI 缩放下黑边、点击偏移；原清单与程序行为不一致）。
- **交叉兼容**：官方 `MaaPiCli.exe` 用我们的清单走通「清单 → 控制器 → 找到游戏窗口 → 选资源集」；
  逐字段对照 MXU 原版 `interface.json`：顶层 21 个字段完全一致，0 丢失 0 新增。

### 模板图与模型（2026-10-26）

- **模板图**（[`docs/P3.2-模板图移植.md`](docs/P3.2-模板图移植.md)）：MXU 710 张全量迁入 0 漏项，
  当前 435 张 / 6.9 MB；**604 处 template 引用、去重 431 张、缺失 0**。
  去重与压缩**评估后不做**（重复仅 1 组 2 张，总体积不到 7 MB，改图反而有断链风险）。
- **ONNX 模型**（[`docs/P3.3-ONNX模型移植.md`](docs/P3.3-ONNX模型移植.md)）：10 个文件 / 80.9 MB；
  15 个 `NeuralNetworkDetect` 节点用 4 个模型，引用 100% 有文件；推理待实机。
  两个坑：`model` 字段只写文件名但文件在 `model/detect/` 下（校验要递归匹配）；
  `model/ocr/*` 无任何 pipeline 引用却是框架内置 OCR 的隐式依赖。
  **`model/` 整个目录已受保护**——模型不可再生（模板图能重截、模型不能），
  所以 `Courtyard-v5.0` / `special-icon-v0.1.0` 这类当前无引用的版本候补也保留。

### 渠道服分层（2026-10-26）

详见 [`docs/P3.5-渠道服分层.md`](docs/P3.5-渠道服分层.md)。

- **分层契约**：`resource[].path` 第一个 bundle 是 base（完整定义），后面的渠道层**只写差异字段**；
- **修复两个真实缺陷**：华为/应用宝覆盖 `启动游戏.next` 导致**永不跳开场动画**；
  vivo 的 `关闭阴阳师.package` 带 activity 导致 `am force-stop` **关不掉游戏**；
- **package 由引擎直读文件**（框架会把 DirectHit 节点上的 package 当未知字段丢弃），
  Adb 控制器下显式 `MaaControllerPostStartApp/StopApp`（新增绑定，Null/Win32 优雅降级）；
- 实测 8 资源集全部独立加载成功，华为 1 个/应用宝 2 个专属节点真正进入启动链，0 条分层规范问题。

```powershell
MaaYys.P1Probe.exe channel              # 静态拆出渠道层差异
MaaYys.P1Probe.exe channel --verify     # 逐个资源集真实加载 + 框架侧核对
```

- **29 个任务 / 94 个选项 / 75 行参数面板**，`select`（联动子选项）、`switch`、`checkbox`、`input` 四种类型全部可编辑；
- 取值存 `config/maayys.json`：`select/switch` 存 case 名、`checkbox` 存逗号连接、`input` 存 `选项/字段`，**默认值不入库**；
- `TaskOptions.Compile` 产出 P5 要用的 `pipeline_override`：任务级 → 选项级 → case 级按字段深合并，
  `{占位符}` 保留类型（`"{n}"` → 数字 10）；
- 实测：改目标 `八岐大蛇→业原火` 后层数子选项自动从 `悲鸣…` 切成 `痴之阵/嗔之阵/贪之阵`，重启后取值保持。

```powershell
MaaYys.P1Probe.exe tasks                                                  # 29 个任务的参数面板结构
MaaYys.P1Probe.exe tasks --task 自动御魂 --value "御魂挑战目标=业原火"      # 看编译后的 override
```

### P3.1 pipeline 自检（2026-10-26）

静态自检器（`PipelineInventory`）已产品化，可反复跑：

```powershell
MaaYys.P1Probe.exe pipeline            # 静态自检
MaaYys.P1Probe.exe pipeline --layer    # 逐层看 base / 渠道层
MaaYys.P1Probe.exe pipeline --verify   # 再用真实资源加载，逐节点核对框架是否认到
```

**结论**：50 个文件全部可解析、0 条悬空引用、框架可见 1511 节点 = 静态扫描 1511 节点。
完整报告见 [`docs/P3.1-pipeline自检报告.md`](docs/P3.1-pipeline自检报告.md)。

**三个必须记住的框架语义**（都已实测）：

1. **跨 bundle 是字段级合并**，不是整节点替换。渠道包只写差异字段即可（如 official 的
   `启动游戏` 只写 `package`，base 的 `recognition`/`next` 仍生效）→ 决定 P3.5 的做法。
2. **`default_pipeline.json` 的 `Default` 段对所有节点生效**，MaaYYs 在里面挂了
   `on_error: ["[JumpBack]悬赏封印_协助战斗1"]`。所以 **`debug.json` 不能删**——它承载全局兜底错误处理链。
3. **`pipeline_override` 的键是节点名**，也是「活节点」的来源（本包 123 个覆盖目标）。
   只按 pipeline 内部引用判断死节点会误删。

### P2.6 配置系统（2026-10-26）

**目标**：强类型 JSON 配置，对标 MAA `gui.new.json` —— `Configurations` 命名组 + `Current` 指针 +
`ConfigVersion` + 导入导出/迁移。验收：**配置改动重启后保持**、**迁移旧配置不丢**。

**与实例清单的分工**（一张表说清，避免两个文件职责混淆）：

| 文件 | 管什么 | 谁在用 |
|---|---|---|
| `config/instances.json` | 连着哪个设备：窗口正则 / ADB 地址 / 截图方式 / 资源集 | P2.4 实例管理 |
| `config/maayys.json` | 跑哪些任务、选项怎么选（**可被多个实例共用**） | P2.6 配置系统 |

实例上的 `configuration_name` 是「用哪一组」的指针：**空串 = 跟随 Current**，
填了名字就锁定到那一组。删除/重命名配置组时，指向它的实例会自动解绑/改绑，不会静默指向不存在的组。

**文件结构**（`configurations` 是对象，顺序由内部 Entry 列表保证——`Dictionary` 不保证顺序）：

```json
{
  "config_version": 1,
  "current": "日常",
  "configurations": {
    "日常": {
      "name": "日常",
      "tasks": [
        { "name": "自动御魂", "enabled": true, "options": { "层数": "10" } },
        { "name": "结界突破", "enabled": false, "options": {} }
      ],
      "created_at": "2026-10-26T20:47:57+08:00",
      "updated_at": "2026-10-26T20:47:57+08:00"
    }
  }
}
```

选项值统一存**字符串**：PI V2 的 Select/Switch 取值就是 case 名，Input 用字符串承载数字；
投递到 pipeline override 前再按 option 类型转换，避免 JSON 数字/布尔来回转换漂移。

| 文件 | 内容 |
|---|---|
| `Engine/Config/TaskSelection.cs` | 任务条目：名称 + 启用位 + 选项值；`SetOption(null)` 即清除回默认 |
| `Engine/Config/TaskConfiguration.cs` | 配置组：任务队列 + 摘要 + 校验 + 深拷贝 |
| `Engine/Config/ConfigurationStore.cs` | 持久化 + 增删改 + Current + 导入导出 + 迁移 |
| `InstanceDefinition.ConfigurationName` | 实例 → 配置组绑定（空 = 跟随 Current） |
| `InstanceWorkspace` | 加载时执行迁移；`RebindConfiguration` / `DetachConfiguration` 维护绑定 |
| `InstancesViewModel` + `InstancesView` | 配置组下拉 + 新建/复制/重命名/删除/导入/导出；任务勾选直接写进配置组 |
| `P1Probe/ProbeConfig.cs` | `config` 子命令：真实文件上验证持久化、迁移、导入导出 |

**迁移策略（P2.4 → P2.6，自动、幂等）**

读到旧 `instances.json` 且实例还有 `task_entries`、同时**尚未有配置组**时：
为每个有任务的实例建一个**同名配置组**（任务去重、全部启用），把组名写回 `configuration_name`，
再落盘两个文件。已有配置组时**不迁移**，避免覆盖用户手工配置。

**实测**

| 项目 | 结果 |
|---|---|
| 单元测试 | **82/82**（新增 22 个：CRUD、Current 回退、损坏备份、版本过高、去重、迁移、幂等、导入导出往返、MAA 格式） |
| 旧清单迁移（探针，2 实例 4+1 任务） | 生成 2 组、重复任务去重为 3 个、写回 `configuration_name`、**二次迁移改写 0 个实例** |
| 跨进程持久化（连续 4 个进程） | 新建→加任务→再新建→重启读取→重命名→导出，内容与 Current 全部保持 |
| 导入 MAA `gui.new.json`（真实 54KB 文件） | 3 个配置组、任务名 + 启用位正确，MAA 专有参数按提示忽略 |
| 导入自有无损导出 | 3 组，同名自动加后缀（`默认配置 2`）不覆盖已有 |
| 真机启动（注入旧 `task_entries` 后） | 自动生成 `maayys.json`，实例页显示「3/3 个任务启用 · 存于 maayys.json」 |
| 构建 | 0 警告 0 错误 |

**已知取舍**

- 配置组**允许删空**（不像实例那样保留至少一个）：删空后 UI 提示新建，不会出现无处置身。
  上一版内容在 `maayys.json.bak` 里可手工找回。
- 选项**值**目前只做存取与保留，编辑入口留给 P6.2 设置中心（P2.6 只保证「改了不丢」）。

### P2.5 资源加载与切换（2026-10-26）

**目标**：按 `interface.json` 的 `resource[].path` 顺序加载 bundle（同名覆盖），渠道服切换**不重启程序**。
验收：官服 ↔ B站服 热切换。

**动手前发现的坑**：连接流程此前**完全没有加载资源** —— `LoadResourceAsync` 写好了但没人调用，
`InstanceDefinition.ResourceName` 只是编辑器里的一个摆设。所以 P2.5 不只是"切换"，先把加载链路建起来。

**实现**

| 层 | 内容 |
|---|---|
| `Engine/Resources/ResourceChain.cs` | `ResourceChainResolver`：把 `resource[].path`（相对 interface.json）解析成绝对路径链，区分「可用」`IsUsable` /「目录齐全」`IsComplete` |
| `MaaYysEngine` | `_resource` 改为可替换；新增 `LoadResourceChainAsync(paths)`：**新建 MaaResource → 按序 PostBundle → 全部成功后 BindResource → 释放旧资源**。任何一包失败都整体放弃，不动当前资源 |
| `App/Services/ResourceProvider.cs` | interface.json 解析一次，各实例按名取链；找不到时降级为空表（不影响连接与预览） |
| `InstanceViewModel` | 连接成功后 `LoadResourceAsync`；新增 `SwitchResourceAsync(name)` 热切换；`ResourceStatus` 实时文案 |
| `InstancesViewModel` + `InstancesView` | 资源集下拉旁加「热切换资源」按钮 + 状态提示 |
| `App.xaml.cs` | `--switch-resource "A|B|A"` 命令行验证入口（用 `|` 分隔，因资源集名里本身含逗号） |

**为什么是「换资源」而不是「卸 bundle」**：MaaFramework 只有 `MaaResourcePostBundle`，没有卸载 API。
所以切换 = 新开一个 `MaaResource` 加载完整链，再 `MaaTaskerBindResource` 替换 —— 这也是官方支持的用法。

**实测（真实 MaaYYs 资源包，8 个渠道全部解析通过）**

| 项 | 结果 |
|---|---|
| 探针 `resource --root D:\YYS\MaaYYs-... ` | 8 个资源集全部 ✓，官服 = base+official，B站服 = base+bilibili |
| 首次加载（base+official） | 834ms |
| 热切换 官服 → B站服 | **157ms** |
| 回切 B站服 → 官服 | **163ms** |
| 端到端（游戏已连接，`--switch-resource` 连续三次） | 官服→B站服→官服 全部成功（369/441/331ms），`ResourceLoaded` 全程 true |
| `dotnet test` | **60/60**（新增 5 个资源链解析测试） |

```
20:09:51.299 状态=Connected | ... | 资源=资源已加载：B站服（2 个包）
20:09:51.618 热切换 → 「官服(雷电,mumu下载的选这个)」：成功 | 已切换到 官服（2 个包）
20:09:52.059 热切换 → 「bilibili服」：成功 | 已切换到 B站服（2 个包）
20:09:52.390 热切换 → 「官服(雷电,mumu下载的选这个)」：成功 | 已切换到 官服（2 个包）
```

**怎么用（资源从哪来）**

程序按这个优先级找 interface.json：

1. 环境变量 `MAAYYS_RESOURCE_ROOT` → 指向含 interface.json 的目录（临时接管别的资源包时用）
2. 程序目录下的 `interface.json` ← **默认走这条**
3. 程序目录下的 `resource\interface.json`（P3.6 产出的标准位置）
4. 程序目录向上最多 6 层

### 资源部署（方式二：随 exe 同目录，2026-10-26 定）

资源不再依赖环境变量。磁盘布局：

```
MaaYYS\resource\                     ← 唯一来源（git 忽略，约 99MB）
├── interface.json
├── resource_pack\{base,official,bilibili,huawei,tencent,OPPO,vivo,taptap}
├── tasks\      32 个 JSON（interface.json 的 import 全部指向这里）
├── assets\     logo.png + 答案.csv
└── agent\      agent.exe
        ↓  构建时自动增量同步（MaaYys.App.csproj → SyncMaaResourceToOutput）
src\MaaYys.App\bin\Debug\net9.0-windows\   ← exe 同目录，直接可用
```

`dotnet clean` 或重建后资源会自动回来，不会丢；`resource\` 不存在时整个 Target 自动跳过，不影响纯编译。

重建来源（原始安装包）：

```bash
SRC="D:/YYS/MaaYYs-win32-win-x86_64-v3.13.6-2608192116-MXU"
# interface.json / resource_pack / tasks / assets / agent / LICENSE / README.md → MaaYYS/resource/
```

- **`resource_pack\test`（33MB）刻意没复制**：它不在 `interface.json` 任何 `resource[].path` 里，
  不会被任何资源集加载。需要时单独 `cp -r` 过去即可。
- **7 个渠道目录各只有 1 个文件**：`pipeline\启动游戏.json`，覆盖 base 里的同名文件
  （如 `bilibili` 版把 `启动游戏.package` 改成 `com.netease.onmyoji.bili/...`）。
  这就是「同名覆盖」的唯一实际落点，也是热切换验收的证据来源。

实测（部署到 exe 同目录后，探针在输出目录直接跑）：

| 项目 | 结果 |
|---|---|
| 程序目录 interface.json 自动发现 | ✅ 无需任何环境变量 |
| 8 个资源集解析 | 全部 ✓ |
| import 缺失错误 | **0 条**（tasks\ 32 个文件齐全，合并出 29 个任务） |
| 首次加载 | 1164ms（base + official） |
| 热切换 官服 → B站服 | **219ms** |
| 回切 B站服 → 官服 | **217ms** |
| GUI 端到端 `--autoconnect` | `资源=资源已加载：官服（2 个包）` |

实例默认的 `ResourceName = "base"` 在真实清单里不存在，连接时会**自动回退到第一个可用资源集并写回配置**，不会卡住。

### 同名覆盖验证：机制正常，但渠道包在这版资源里是「空转」（2026-10-26）

用 `MaaResourceGetNodeData` 读节点合并后的最终定义（`resource --switch "bilibili服" --node 启动游戏`）：

```
官服链   / 启动游戏 = {"action":{"type":"DoNothing"},...,"post_delay":1000,...}
B站服链  / 启动游戏 = {"action":{"type":"DoNothing"},...,"post_delay":1000,...}   ← 一字不差
```

**先证明机制没问题**（`resource --proof`，构造临时覆盖包改 `post_delay`）：

```
✓ 覆盖生效：`启动游戏` 的 post_delay 由 1000 变为 12345
链：base + official + maayys-override-proof-xxxx
```

**那为什么渠道包看不出差异**——根因在资源包本身，不在我们的加载逻辑：

| 事实 | 后果 |
|---|---|
| 7 个渠道目录只覆盖 `package` 一个字段 | 唯一的差异点集中在这个字段上 |
| MaaFramework 规定 `package` **只在 `action` 为 `StartApp`/`StopApp` 时有效** | 顶层裸 `package` 按未知字段丢弃 |
| base 的 `启动游戏` 节点是 `recognition: DirectHit`，无 `action` | 渠道写入的 `package` 被框架忽略 → 节点定义与 base 完全相同 |
| base 的 `关闭阴阳师` 是 `action: StopApp` + `package` | 这个字段有效，但渠道包也只改 `package` 值、不改结构 |

结论：**bundle 顺序覆盖语义 100% 正常**（已由 `--proof` 证明），但「官服 ↔ B站服」在这版资源包里
**对 Win32 桌面端没有任何行为差异**——桌面端本来就不靠 `package` 启动应用。
将来接 ADB 渠道服时，需由我们自己读取渠道包的 `package` 并显式传给 `MaaControllerPostStartApp`，
不能指望 pipeline 里的裸 `package` 字段生效。

**坑位**

- 资源集名可能自带逗号（`官服(雷电,mumu下载的选这个)`），任何分隔拼接都不能用逗号
- 切换时若有任务正在跑，`BindResource` 会失败——此时保持旧资源并提示「请先停止任务」，不会把会话搞挂
- 加载失败只改 `ResourceStatus`，**不影响连接与预览**（没资源仍可调设备/截图）

### P2.5 缺陷修复：最小化后窗口恢复迟滞（2026-10-26）

**现象**（用户反馈）：连接到实例后点最小化，再点任务栏图标——**窗口框架回来了，但内容区看不到**
（实例页里的预览画面本身是正常的），要把别的窗口置顶到最前、再切回来内容才显示出来。
游戏端当时是**窗口模式**（非全屏）。

**定位过程**（新增 `window` 诊断子命令，`dotnet run --project src/MaaYys.P1Probe -- window --process MaaYys --hold 45`）：

| 观测项 | 修复前 | 修复后 |
|---|---|---|
| 最小化挂后台 CPU | **100%~260%**（持续空转） | **0ms/1s** |
| 最小化期间 UI 线程响应 | 1~3ms | 1~2ms |
| 恢复后 `IsIconic` / `IsWindowVisible` | False / True（已恢复） | False / True |
| 恢复后 z-order | #1（最上层，上面没有 TopMost 窗口） | #1 |
| 进程内隐藏/幽灵窗口 | 无（仅 WPF 内部辅助窗口与输入法窗口） | 无 |

即：窗口在 Win32 层面确实恢复了、也在最上层，但**最小化期间预览采集仍在全速空转**——
每帧新建 2.7MB 的 `BitmapSource`（1280×720 Bgr24），5~15fps 下相当于每秒数十 MB 的大对象堆分配，
触发高频 Gen2 GC；GC 暂停 + UI 线程上的位图写入把界面响应反复打断，
点任务栏恢复窗口时就会明显迟滞，观感是"点了没反应 / 弹不出来"。这条链路只在**连接后（有预览采集）**存在，
与用户"连上实例才出现"的描述一致。

**修复**

1. **最小化 / 失焦即停预览采集**：新增 `Services/PreviewSuspension`（全局暂停开关），
   `MainWindow.StateChanged` 置位，`InstanceViewModel` / `ConnectionViewModel` 的 `UpdatePumpState()`
   统一按「当前激活标签 + 已连接 + 窗口未最小化」判定是否跑泵；还原时自动恢复采集。
   实测后台 CPU **100~260% → 0**。
2. **预览位图原地复用**：新增 `PreviewBitmap`（`WriteableBitmap` + `WritePixels`），
   不再每帧 `BitmapSource.Create`——LOH 大对象分配降到 0，`Image.Source` 也不再反复换实例。
3. **恢复时两段式强制重绘**：`StateChanged` 非最小化分支立刻 `InvalidateVisual()` + `UpdateLayout()`，
   并在 180ms 后再执行一次——WPF 最小化期间停渲染，恢复时 D3D 渲染目标是异步重建的，
   只失效一次仍可能呈现空白（正是"窗口出来了但内容看不到、切窗口才画出来"的成因）。
   > 该条已被下方「P2.5 二次修复」的 Win32 强制重绘 + 自愈守卫取代（托管层失效标记在渲染线程挂起时不生效）。
4. **`FramePump.Stop` 等待上限 2s → 300ms**：该调用可能发生在 UI 线程（最小化暂停预览），
   截图本身是阻塞调用，等太久会直接卡住界面。

**验证**：`dotnet build` 0 警告；`dotnet test` **55/55** 通过。

`MaaYys.exe --page 3 --autoconnect` + `dotnet run --project src/MaaYys.P1Probe -- window --hold 10 --after 3 --shot out.bmp`：

- 最小化 10s 全程 **CPU 0ms/1s**、UI 线程响应 1~2ms
- 恢复后 `IsIconic=False` / `IsWindowVisible=True` / 前台为自身 / z-order #1
- 恢复后 CPU 回升到 1.2~2.0s/1s（说明采集自动恢复）
- **恢复后窗口内容抓图正常**：平均亮度 72.7、亮度方差 5801、非暗像素 97.9%，
  界面完整、预览区有游戏画面（第 705 帧 · 21fps · 非黑 100%）→ 见 `docs/P2.5-最小化恢复后内容正常.png`

**新增调试参数**

- `MaaYys.exe --autoconnect`：自动连接当前实例并保持运行（无人值守交互测试）
- `window [--hold N] [--after N] [--watch N] [--shot out.bmp]`：进程内窗口清单、
  最小化/恢复状态与前台采样、恢复后 z-order（含置顶标记）、每秒 CPU/内存/UI 响应、恢复后窗口内容抓图

### P2.5 二次修复：恢复后「只剩一个空框」（2026-10-26）

**现象**（用户反馈，偶发）：最小化后点任务栏把窗口拉回最前，窗口的**位置和轮廓都在**（占位正确），
但客户区**没有任何像素**，直接透过去看到桌面壁纸和图标。必须改变窗口大小或切一下别的窗口才画出来。

**根因**：WPF 的像素由 DWM 上的重定向表面（redirection surface）承载，最小化时该表面被释放、
渲染线程挂起，恢复时是**异步重建**的。首次修复用的 `InvalidateVisual()` 属于托管层「标记脏区」，
当渲染线程仍处于挂起态时**不会唤醒设备重建**——一击不中就一直空着。竞态决定了它偶发。

**修复（三层递进，`Services/WindowRenderRecovery`）**

1. **Win32 强制重绘**：`SetWindowPos(SWP_FRAMECHANGED)` + `RedrawWindow(RDW_INVALIDATE|RDW_ALLCHILDREN|RDW_UPDATENOW|RDW_FRAME)`
   走 Win32 绘制路径，能真正驱动 `HwndSource` 重新提交一帧；恢复时立即执行，并在第 2、3 个 tick 各补一次。
2. **内容自愈守卫**：恢复后每 240ms 用 `PrintWindow(PW_RENDERFULLCONTENT)` 抓窗口左上角 240×180，
   统计非暗像素占比与亮度方差判定「到底画出来没有」；判定空白则做一次 **1px 尺寸抖动**强制 DWM 重建表面
   （最大化状态下改用 `Topmost` 翻转）。确认有内容即停止，最长守卫 1.9s。
3. **关闭 Windows 效率模式**（`Services/ProcessPowerMode`，`SetProcessInformation(ProcessPowerThrottling)`）：
   避免最小化期间进程被降频/挂起，导致恢复时表面重建慢。

**顺带补上全局异常日志**：GUI 子系统进程无控制台，崩溃后无迹可查。现在
`AppDomain.UnhandledException` / `DispatcherUnhandledException` / `TaskScheduler.UnobservedTaskException`
统一落 `debug/crash.log`。

**验证**

| 项目 | 结果 |
|---|---|
| `dotnet build` | 0 警告 0 错误 |
| `dotnet test` | **55/55** 通过 |
| 最小化/恢复压力测试（20 轮，恢复等待压到 300ms） | **20/20 正常**，0 空白、0 未恢复、0 前台丢失 |
| 自愈守卫日志 | 20 次恢复全部在第 4 次 tick（约 480ms）确认内容已绘制，**0 次误判抖动** |
| 1px 抖动自检（`--diag-nudge`） | 抖动前 1475×900 → 后 1475×900 **尺寸完全复原**，`ActualWidth/Height` 1180×720 不变 |

> 说明：测试机上 20 轮未复现偶发空白（表面重建在本机 < 300ms），因此修复价值来自
> 「守卫会在空白时自动补救」这条可控路径，而非压测复现率。设备丢失/效率模式挂起这类
> 慢重建场景正是守卫覆盖的范围。

**新增工具与参数**

- `minimize-loop [--process MaaYys] [--cycles 20] [--gap 900] [--settle 300] [--recheck 1500] [--shot-dir 目录]`：
  连续最小化/恢复压力测试，逐轮用 `PrintWindow` 判定客户区是否真的画出来了，区分「恢复慢」「空白且复检仍空」「未恢复」三类异常，异常帧存 BMP
- `MaaYys.exe --diag-nudge`：启动 3s 后触发一次 1px 抖动自检，验证兜底路径不破坏窗口尺寸
- `debug/render-recovery.log`：每次恢复的重绘/检测/抖动记录（GUI 无控制台时的唯一观测口）

**坑位**

- 用 `PrintWindow` 判定内容空白是可靠的（WPF 走 DWM 合成结果），但**统计值完全不变**说明画面是静态的——
  压测时要先确认预览真的在跑（`debug/autoconnect.log` 里应有「第 N 帧」），否则测的是静态界面，没有代表性
- 恢复等待别设 1s 以上：那等于给 WPF 留了一整秒恢复时间，会把真实用户「点完立刻看」的空白现象掩盖掉
- `Start-Process` 启动的 GUI 进程在同一次工具调用结束后可能被回收，长时间压测要与启动放在同一条命令里

### P2.5 三次修复：游戏窗口「在正确位置却完全透明」（2026-10-26）

**现象**（用户反馈，必须先最小化游戏才复现）：MaaYys 预览里游戏画面完全正常，但**桌面上看不到游戏窗口**，
原位置只能看到它后面的桌面壁纸。窗口没有消失、也没有被遮挡。

**根因（用 `inspect` 只读探针采样三个时点）**

| 时点 | 几何 | 最小化 | 扩展样式 | alpha |
|---|---|---|---|---|
| 最小化时 | (-40000,-40000) 249×43 | 是 | `0x00080120` LAYERED+TRANSPARENT | 0 |
| 控制器恢复后 | (240,101)-(1680,950) 1440×849 | **否** | `0x00080120`（**没变**） | **0** |
| 修复后 | 同上 | 否 | `0x00080100` 只剩 LAYERED | **255** |

链路：最小化时窗口被设上 `WS_EX_LAYERED` + `WS_EX_TRANSPARENT` + **alpha=0** 并挪出屏幕 →
MaaFramework 为了截图 `SW_RESTORE` 回原位（几何、`IsIconic`、`IsWindowVisible` 全部正确）→
**但没人还原分层属性**，alpha 停在 0。窗口在正常渲染、WGC 抓得到（所以预览正常），
DWM 合成时 alpha=0 完全透明（所以桌面上看不到）。`WS_EX_TRANSPARENT` 还让鼠标点击穿透。

**修复**（`src/MaaYys.Engine/Devices/GameWindowVisibility.cs`）

- `EnsureVisible(hwnd)`：`alpha=0 → 255` + 移除 `WS_EX_TRANSPARENT` + `SetWindowPos(SWP_FRAMECHANGED)` + `RedrawWindow` 强制重绘一帧
- **最小化状态一律不碰**：恢复窗口是 MaaFramework 的职责，擅自 `SW_RESTORE` 会把游戏从后台弹出来打扰用户；
  只处理「已非最小化、却仍 alpha=0 / 点击穿透」这种确定异常
- 两处接入：`InstanceViewModel` / `ConnectionViewModel` 连接成功时各查一次
- **2 秒独立巡检**（`DispatcherTimer`）：用户中途最小化游戏、控制器再次恢复后可能又留下透明外壳。
  巡检不能挂在帧回调上——窗口最小化时抓不到帧，帧回调不跑，挂在帧上的检查永远不会触发
- **巡检有显式连接判断**：tick 内先校验「窗口句柄非空 + 引擎非空 + `State == Connected`」，
  未连接一律跳过。不依赖「定时器只在连接期间存在」这种隐式约束，异常路径残留状态也不会误修
- 修复记录落 `debug/window-fix.log`；界面 `Diagnostic` 同步提示「已修复游戏窗口可见性：alpha 0→255」

**验证**

| 场景 | 结果 |
|---|---|
| 连接时窗口已是幽灵态（alpha=0） | 连接后自动变 `alpha=255`、TRANSPARENT 已移除 |
| 运行中注入幽灵态（`inspect --break`） | **5 秒内被巡检自动修回** `alpha=255` |
| **MaaYys 启动但未连接**（负向对照） | 注入幽灵态后 **7 秒仍保持 alpha=0**，确认不连接就不巡检 |
| `dotnet build` / `dotnet test` | 0 警告 0 错误 / **55/55** 通过 |

**新增诊断命令**

- `inspect [--process onmyoji] [--shot out.bmp] [--fix] [--all]`：只读采样（几何 / 可见性 / EXSTYLE 位 /
  DWM cloak / 屏幕内占比 / 中心点命中 / z-order），`--fix` 修可见性
- 调试辅助：`--minimize`（最小化复现）、`--restore`（模拟点任务栏恢复）、`--break`（注入 alpha=0 幽灵态）

**坑位**

- `GetWindowTextW` / `GetClassNameW` 的 `DllImport` **必须显式 `CharSet = CharSet.Unicode`**，
  否则默认按 Ansi 封送宽字符缓冲区，中文标题被截成 `4??^-`、类名 `Win32Window` 只剩 `W`，会误判窗口种类
- 判断「窗口看不见」不能只看 `IsWindowVisible`：幽灵窗口的 `Visible=True`、`Iconic=False`、几何全对，
  真正的判据是 **`alpha=0` 或 `WS_EX_TRANSPARENT`**
- 巡检必须用独立定时器，不能依赖帧回调（最小化时无帧）

### P2.4 实例管理（2026-10-26）

新增 `src/MaaYys.Engine/Config/`：`InstanceDefinition`、`InstanceStore`、`DeviceLease`、`InstanceCatalog`；
App 侧新增 `Services/InstanceWorkspace`、`ViewModels/InstanceViewModel`、`ViewModels/InstancesViewModel`、`Views/Pages/InstancesView`，主窗口顶部加实例标签条。

- **实例 = 控制器绑定 + 资源集 + 任务集**（对标 MXU instances / MAA 多配置）。`InstanceDefinition` 为纯数据，
  可序列化/克隆；`InstanceStore` 持久化到 `config/instances.json`（`schema_version` + `current_id` + `instances[]`），
  **原子写**（tmp → Move，上一版留 `.bak`）；解析失败先备份为 `*.bad-时间戳` 再重建，绝不静默丢配置
- **多开（标签条）**：顶部标签条一实例一标签，圆点显示连接状态（灰=未连接 / 黄=连接中 / 绿=已连接 / 红=异常），
  标签副标题实时显示帧率；关闭按钮删除实例（至少保留 1 个，删除当前实例时 Current 指针迁到相邻项）
- **设备独占 `DeviceLease`**：同一台设备（Win32 句柄 / ADB 地址）只允许一个实例连接，避免两个实例的输入互相踩；
  断开 / 删除实例 / 退出时自动释放
- **只有激活标签抓帧**：切到后台的实例保持引擎连接（任务可继续跑），停止预览采集，多开不抢 CPU
- **实例页**：左侧实例列表 + 运行控制 + 实时预览，右侧**草稿式**编辑器（改 Draft → 保存才落盘，未保存标"未保存"），
  含设备类型/正则/ADB 地址、截图与输入方式、预览帧率、资源集、任务勾选、备注；保存前做完整校验（空名/超长/非法正则/fps 越界等）
- **`InstanceCatalog`**：从 interface.json 读取资源集与任务清单；文件缺失时降级为默认项并提示（P3.6 产出 interface.json 后自动生效）

实测（`MaaYys.exe --page 3 ...`）：

| 场景 | 结果 |
|---|---|
| 新建 | 3 → 5 个实例，自动避开重名（"实例 1"/"实例 2"），Current 指向新实例 |
| 删除 | 5 → 4 → 3 个，删除当前实例后 Current 正确迁到相邻项；删除**已连接**实例不崩溃、设备占用释放 |
| 切换 | 标签条 / 列表 / 编辑器三者同步，状态栏显示当前实例连接态 |
| 连接 | 当前实例连接成功：标签绿点 + `已连接 · 5fps` + 左侧实时预览（庭院画面 1278×720、非黑 100%） |
| 独占拦截 | 一次连 4 个实例：只有首个成功，其余红点（设备被占用 / 无 ADB 设备），引擎不崩 |

调试参数（P2.4 新增，全部走与 UI 按钮相同的命令，便于无人工点击验证）：

```powershell
MaaYys.exe --page 3 --screenshot out.png                      # 实例页
MaaYys.exe --page 3 --add-instance --add-instance --screenshot out.png
MaaYys.exe --page 3 --instance 官服-大号 --connect --screenshot out.png
MaaYys.exe --page 3 --connect-all --screenshot out.png        # 逐实例连接，验证设备独占
MaaYys.exe --page 3 --close-instance 官服-大号 --screenshot out.png
MaaYys.exe --page 3 --scale 2 --screenshot out.png            # 2 倍分辨率，检查小字号/图标
```

坑位记录：
1. **WPF 默认 `ListBoxItem` 模板用系统前景色**，暗色主题下未选中项是黑字、几乎不可读；
   必须自定 `ItemContainerStyle` 显式给 `Foreground`（P2.3 连接页设备列表同样踩过，已一并修）。
2. 标签关闭按钮原来用文本叉号，缩略截图上看像方块，改为 `Path` 画叉后无字体依赖。
3. **状态栏不跟随实例状态变化**：`StatusText` 转发的是 `InstanceWorkspace.StatusSummary`，只在 `CurrentChanged` 时通知，
   实例「连接成功」不发这个事件 → 标签显示已连接、状态栏仍写未连接。补 `PropertyChanged` 转发后修复。
4. 按钮显式设 `Background` 后，WPF 禁用态几乎无视觉变化（用户不知道为何点不动）→ 主题里加隐式 `Button` 样式，
   `IsEnabled=false` 时 `Opacity=0.45`。
5. `InstanceStore` 每次变更立即落盘（文件仅几 KB），不给 UI 留"忘记保存"的坑；但编辑器仍走草稿，
   避免边打字边污染持久化文件。

### P2.3 设备管理（2026-10-26）

新增 `src/MaaYys.Engine/Devices/`：`DeviceScanner`、`AdbDevices`、`MaaAdbController`、`ControllerCapture`、`FrameData`、`FramePump`。

- **自动探测**：`DeviceScanner.ScanAll(titleRegex, classRegex, includeAdb)` 合并 Win32 窗口（Toolkit 正则）与 ADB 设备（`MaaToolkitAdbDevice*`，含模拟器预设）；无 adb 环境降级为空列表，不影响 Win32
- **ADB 控制器**：`MaaAdbControllerCreate` P/Invoke + `CreateAdb(AdbDeviceInfo)` 会话工厂，方法位掩码优先取设备自报能力
- **截图复用**：`ControllerCapture` 抽出抓图/黑边/亮度统计，Win32 与 ADB 共用；统计改为**每 4 像素采样**（原全量统计是预览帧率瓶颈）
- **帧采集泵 `FramePump`**：目标帧率 1–60 可运行时调整，`Frames` 事件流推送含原始像素的 `FrameData`，实际帧率按最近 1 秒滑动窗口计算，可选回派 UI 线程
- **连接页 UI**：设备列表 + 标题正则 + 刷新、截图/输入方式与预览帧率选择、连接/断开/保存截图、实时预览与帧信息（分辨率/帧号/非黑比例/黑边告警）

实测（`dotnet run --project src/MaaYys.P1Probe -- devices --fps 10`）：
- 探测命中 `阴阳师-网易游戏 | class=[Win32Window]`，FramePool 截图**黑边 0px**，非黑 100%
- 帧率：目标 5/10/20 → 实际 5.0/9.1/15.9（20fps 上的上限由 FramePool 抓帧本身决定，非统计开销）
- GUI 端到端（`MaaYys.exe --page 2 --connect --screenshot out.png`）：设备列表填充 → 连接成功 → 实时预览庭院画面 → `实际帧率 5.0 fps · 第 6 帧 · 非黑 100.0%`

**新增调试能力**（P6 界面阶段复用）：`MaaYys.exe --page N --screenshot <path> [--connect]`，
切换页签、等首轮异步加载（必要时自动连接）后把窗口渲染为 PNG 并退出，无需人工点击即可校验布局。

坑位记录：
1. `Run.Text` 的默认绑定模式是 **TwoWay**，绑定只读 VM 属性（`private set`）会在窗口显示时抛
   `InvalidOperationException: 无法对只读属性进行 TwoWay 绑定`——必须显式 `Mode=OneWay`。
   此类错误只在运行时暴露，`--screenshot` 自检正是为此而加。
2. 逐像素全量统计（亮度/非黑/黑边）在 1280×720 下单帧约 20ms，直接拖垮预览帧率 → 改采样统计。
3. `FramePump` 用 1 秒分桶统计时，启动后前 1 秒恒显示 0 fps → 改滑动窗口逐帧更新。

### P2.2 引擎封装层（2026-10-26）

新增文件（`src/MaaYys.Engine/`）：`Session/EventStream.cs`、`Session/TaskStateMachine.cs`、`MaaYysEngine.cs`（重写为会话类）、`AssemblyInfo.cs`。

- **会话宿主 `MaaYysEngine`**：`CreateWin32(hWnd, options)` / `CreateNull()` 创建；`ConnectAsync()` 内部完成「控制器连接 → BindResource → BindController」；
  `LoadResourceAsync()`（可多次调用实现 base ← 渠道层覆盖）；`RunTaskAsync(entry, override, ct)` 返回 `TaskRun`；`Stop()`；释放顺序固定 Tasker → Controller → Resource
- **原生回调转 Observable**：`Events`（Resource.Loading.* / Tasker.Task.* / Node.*）与 `TaskStateChanges`（经状态机校验）；
  构造时捕获 `SynchronizationContext`，事件自动回派 UI 线程（`CreateNull(marshalToSynchronizationContext: false)` 可关闭，供控制台/测试）
- **任务状态机**：`Idle → Pending → Running → Succeeded/Failed/Stopped`，终态不可变，非法迁移写入 `LastTransitionError`；`EnsureFinal()` 兜底事件缺失
- **句柄收敛**：`MaaWin32Controller.Handle` / `MaaNullController.Handle` / `MaaTasker.BindController(IntPtr)` 改为 `internal`，
  仅 `InternalsVisibleTo` 的探针与测试可访问（P1.6 评审 R6）

验证：`tests/MaaYys.Engine.Tests`（xUnit）**22/22 通过**，覆盖状态机/事件流纯逻辑 + 空控制器全链路集成
（连接 → 资源加载 → 投递 `TestTask` → Succeeded、事件流收到 `Resource.Loading.Succeeded`、断开后 `Ready=false`、未连接投递抛异常、释放后调用抛异常）。
`dotnet run --project src/MaaYys.P1Probe -- engine` 回归仍通过（P1.2 链路无退化）。

坑位记录：
1. **状态事件参数必须存快照**——`TaskStateChangedEventArgs.Current` 若实时读 `Run.State`，
   回调执行时任务可能已推进到下一态，订阅端会「跳过」Pending 这类短命状态（实测丢了 Pending，只收到两个 Running）。
2. 空控制器的 `connected` 回调恒真，引擎 `Connected` 不能直接透传底层状态，须叠加自身会话标志位。
3. 事件回调来自框架线程，测试里收集状态必须加锁（`List<T>` 并发写会丢数据）。
4. 集成测试需串行（native 有全局状态）：`[assembly: CollectionBehavior(DisableTestParallelization = true)]`。

### P2.1 GUI 骨架（2026-10-26）

新增 `src/MaaYys.App`（`net9.0-windows` + WPF，输出 `MaaYys.exe`）：

- **导航框架**：**顶部页签**（一键长草 / 多实例 / 小工具 / 设置），`ContentControl` + `DataTemplate` 按 ViewModel 类型自动套用页面，页面懒创建并缓存（切换不丢状态）。小工具页内再一层左侧二级导航（定时任务 / 运行日志 / 设备探测）。P6.1 起左侧导航整体移除，实例切换并入顶栏下拉
- **明暗主题**：`Themes/Theme.Dark.xaml` / `Theme.Light.xaml`（键一一对应，暗色对标 MAA 观感），`ThemeManager` 整体替换 Application 级资源字典；顶栏按钮实时切换
- **MVVM 基础**：`ViewModelBase`（SetProperty / OnPropertyChanged）、`RelayCommand`（无第三方 MVVM 框架依赖）
- **DPI**：`app.manifest` 声明 `PerMonitorV2`（P1.6 评审要求，避免截图/点击坐标被 DPI 虚拟化带偏）
- **引擎联动**：主界面连接面板读取 `MaaFramework.Version`，缺库降级为提示而非崩溃

验证：`dotnet build` 0 警告 0 错误；`MaaYys.exe` 启动存活、无异常抛出。

坑位记录：程序集名设为 `MaaYys`（`<AssemblyName>`），主题 pack URI 必须写成
`pack://application:,,,/MaaYys;component/Themes/...`——用项目名 `MaaYys.App` 会抛 `FileNotFoundException`。


## P1.2 验证结果（2026-10-25，v5.15.0-beta.1 回归通过）

`dotnet run --project src/MaaYys.P1Probe -- engine` 输出：MaaFramework v5.15.0-beta.1 → 资源 PostBundle 成功（hash `d2d7ae0ec19aaf9`）→ PostTask → `Tasker.Task.Starting` → `Tasker.Task.Succeeded`。

实现要点：
- 引擎层：`src/MaaYys.Engine/Native/`（P/Invoke + DllImportResolver 定位 `third_party/maafw`）、`MaaResource` / `MaaTasker` / `MaaNullController` 托管封装（SafeHandle + UTF-8 marshalling）
- 自定义控制器 `MaaNullController`：官方运行库未分发 `MaaDbgControlUnit.dll`，改用 `MaaCustomControllerCreate` 扩展点实现 1x1 静态截图控制器（无额外 DLL 依赖）
- 注意：v5.13 起 API 为 Tasker 中心化（无 MaaInstance）；`pipeline_override` 必须传合法 JSON 字符串（官方绑定默认 `{}`，传 null 会触发框架 UB）

## P1.3 验证结果（2026-10-25）

`dotnet run --project src/MaaYys.P1Probe -- pi`（默认解析 MaaYYs 的 interface.json，可传路径覆盖）：
- 8 个渠道服资源 bundle 全部解析，加载链正确（base ← 渠道层，后者覆盖前者）
- 29 个 import 任务入口合并成功（29/29 文件解析通过），94 个 option 池（同名先到先得）
- 交叉校验通过：option / group / resource / controller 引用全部有效，资源路径全部存在
- 识别出 3 个未被 import 引用的孤儿任务文件（活动-小超鬼王 / 自动业原火 / 限时活动）与 1 个孤儿资源目录（test/）

关键结论（均经官方源码实证）：
- import 合并语义对齐 MaaPiCli `Parser.cpp`：task/preset 追加；option 为 `unordered_map::insert` 语义——**同名先到先得，顶层 interface.json 优先**
- 官方硬校验三项：`interface_version == 2`、控制器类型合法、任务引用的 option 必须存在于合并池
- **流程表修正**：原「32 个任务入口」为按 tasks/ 目录文件数的误计数；按 PI V2 语义只有 import 引用的 29 个构成任务入口（3 个孤儿文件不加载），流程表全文已同步修正

## P1.4 验证结果（2026-10-25 ~ 26）

`dotnet run --project src/MaaYys.P1Probe -- win32`（游戏窗口实测运行中）：

- 窗口枚举命中 1 个：`hWnd=0x1805EC class=[Win32Window] title=[阴阳师-网易游戏]`
- 连接：`PostConnection -> Succeeded`，`Connected=True`，`Uuid=00000000001805EC`
- 截图：FramePool 下非黑像素 **97~100%**，画面完整覆盖客户区（PrintWindow 仅 64.1%）
- 点击：好友按钮（截图坐标 894,675 = 客户区 1000,755）前台点击 → 好友面板打开；面板内「添加」按钮后台点击 → 添加界面打开

实现要点：
- `Native/`：Toolkit 窗口枚举 API（MaaToolkit.dll）+ Win32 控制器全套 Post/Wait API + ImageBuffer/StringBuffer 封装
- 托管封装：`MaaDesktopWindows`（正则语义对齐 `boost::regex_search` 子串匹配）、`MaaWin32Controller`
- 探针参数：`--click x,y` / `--swipe ...`（策略 A：默认仅截图，输入须显式传参）、`--resize=WxH`、`--screencap-method=`、`--mouse-method=`、`--foreground`、`--children`

### 四项关键结论（P2 架构决策依据）

1. **截图方法必须 FramePool，不能用 PrintWindow**
   游戏渲染 1145x644 拉伸 ×1.25 铺满 1431x805 客户区；PrintWindow 只抓到渲染层，截图右侧/底部为黑边（实测右 254px / 下 144px）。
   框架按「截图尺寸/客户区」等比映射，黑边导致点击坐标整体偏移约 200px（曾误点到阴阳寮/花合战）。
   FramePool 抓完整客户区，映射精确成立，无需修正。
   → 已实现 `CreateWithFallback`：首选 → FramePool → PrintWindow，连接失败/截图空/全黑/黑边 >10% 自动回退；
   `ScreenshotResult` 带 `BlackBorderRight/Bottom` 测量。
   注：MAA 官方 Default 档案同样是 `FramePool + PostMessageWithCursorPos`（界面上的 PrintWindow+Seize 为手动历史档案）。

2. **主界面（庭院场景层）UI 点击必须窗口前台**
   后台 SendMessage 点击好友按钮 10 余次全部无效；完整 MOVE→DOWN→UP 序列、移动物理光标、伪造 `WM_ACTIVATE` 均无效。
   加 `--foreground`（`SetForegroundWindow` 后点击）即一次成功。

3. **二级面板内 UI 后台消息即可驱动**
   面板关闭按钮 (1172,160)、面板内「添加」按钮 (1215,296) 后台 SendMessage 点击均生效。
   → 建议 P2 采用混合输入策略：开面板时前台一次，面板内全程后台。

4. **探针进程需 PerMonitorV2 DPI 感知**
   系统 125% 缩放下，DPI-unaware 进程 `GetClientRect` 返回虚拟化坐标，Seize/光标类方法会整体点偏。已在 `Program.cs` 设置。

排查教训：截图目测坐标必须按缩放比换算（Read 显示的是缩略图），多次目测判读出错；程序化检测（红×像素占比、全屏差异）远可靠于目测。

验证截图（debug/win32-screenshot-*.bmp，已 gitignore）。
