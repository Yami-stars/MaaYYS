# MaaYys 项目开发流程表

> 版本：v2.0 ｜ 日期：2026-10-27 ｜ 状态：P1-P4 全部完成（**M3.5 Agent 能力就绪**里程碑达成），**P5 核心功能实现 5/5 全部完成**（任务队列 / 选项系统 / 执行引擎 / 定时任务 / 日志系统），**P6 进度 1/5**（主界面布局，已按 MAA 形态改版）
> 进度同步：P1.3 修正任务入口数 32→29；P1.4 实测追加 2 项风险登记（DPI 黑边 / 前台输入）；P1.6 评审结论见 `docs/P1.6-技术方案评审.md`
> 命名：2026-10-27 全仓库 `MaaYss` → `MaaYys`（缩写写错，Yss 应为 Yys）。**上游项目名 `MaaYYs`（大写 YY）保持不动**——那是 MaaYYs-win32 MXU，不是我们的缩写。
> 前置输入：《MaaYys-知识获取报告.md》（知识获取已完成）
> 功能基准：MaaYYs-win32 MXU v3.13.6 ｜ UI/设置基准：MAA v6.20.1 ｜ 引擎：MaaFramework

---

## 1. 总体路线图（阶段总览）

| 阶段 | 名称 | 目标 | 工期(人日) | 关键门禁 |
|---|---|---|---|---|
| P0 | 知识获取与立项 | 完成参考对象分析、技术选型、开发流程评审 | 2 ✅ | 本报告 + 流程表评审通过 |
| P1 | 技术预研与原型验证 | 打通 MaaFramework 全链路，验证 Win32 控制可行性 | 5 ✅ | 原型机能连桌面端并完成一次完整识别+点击 |
| P2 | 基础框架搭建 | GUI 骨架 + 引擎封装 + 设备管理 + 配置系统 | 8 ✅ | 能连接设备、加载资源、跑通一个 pipeline |
| P3 | 资源层移植 | pipeline/模板图/模型/任务定义从 MaaYYs 移植重构 | 10 ✅ | 29 个任务全部可加载、JSON 校验通过 |
| P4 | Agent 功能移植 | Go agent 17 动作 + 2 识别移植（共 19 能力） | 7 ✅ | 19 能力全注册、RandomWait 真实动作跑通 |
| P5 | 核心功能实现 | 任务队列/选项系统/执行引擎/定时/日志 | 10 ✅（5/5 完成） | 端到端跑通「自动御魂」全流程（M4 判定需实机长跑，见第 3 节） |
| P6 | UI 与设置体系 | 对标 MAA 的主界面 + 11 类设置中心 | 8（1/5 完成） | 设置项全量落地，界面交互达标 |
| P7 | 增强功能 | 外部通知/远程控制/数据统计/自动更新 | 7 | 各功能独立可用 |
| P8 | 测试与发布 | 全量测试 + CI/CD + 文档 + 灰度 | 6 | 发布候选包通过验收清单 |
| — | **合计** | | **63 人日** | ≈ 单人 3 个月 / 双人 1.5 个月 |

---

## 2. 详细开发流程表

### P1 技术预研与原型验证（5 人日）

| # | 任务 | 内容说明 | 交付物 | 验证标准 | 工期 | 依赖 |
|---|---|---|---|---|---|---|
| 1.1 | 开发环境搭建 | Go 1.24 / .NET 8 SDK / MaaFramework 运行库解压；仓库脚手架（.gitignore、README 骨架） | 可构建的空工程 | `go build`、`dotnet build` 均通过 | 0.5 | — |
| 1.2 | 引擎集成验证 | C# P/Invoke 或 Maa.Framework 绑定 MaaFramework.dll；创建 MaaInstance、MaaTasker | 引擎封装 Demo | 能调用 MaaTaskerPostTask 并收到回调 | 1 | 1.1 |
| 1.3 | PI V2 协议解析验证 | 加载 interface.json，验证 controller/resource/import 解析，多 bundle 覆盖语义 | 资源加载 Demo | 控制台打印出 8 个渠道服资源、29 个 import 任务入口（另识别 3 个未引用孤儿文件） | 1 | 1.2 |
| 1.4 | Win32 控制链路验证 ✅ | Win32 控制单元连接桌面端（类名 Win32Window + 标题正则）；**FramePool** 截图；SendMessage 后台点击 | 可运行的连接 Demo | 截图完整无黑边；主界面前台点击生效；面板内 UI 后台点击生效 | 1 | 1.2 |
| 1.5 | Agent 链路验证 ✅ | maa-framework-go **v4** 起 MaaAgentServer；注册测试 custom_action + custom_recognition；C# AgentClient 连接并调通 | agent 最小闭环 | 日志出现 agent 注册成功 + action 被调用 | 1 | 1.1 |
| 1.6 | 技术方案评审 ✅ | 评审绑定方式（P/Invoke vs NuGet）、agent 语言与版本、目录规范、P2 输入架构 | `docs/P1.6-技术方案评审.md` | 方案确认并冻结为基线，风险记入风险登记 | 0.5 ✅ | 1.2-1.5 |

**P1 评审结论摘要（详见 `docs/P1.6-技术方案评审.md`）**
1. 绑定方式：**自研 P/Invoke** 固化，全部原生声明集中在 `MaaYys.Engine/Native/`，上层只见托管包装。
2. agent：**Go 1.25 + `maa-framework-go/v4` v4.0.0**（v1.x 无 AgentServer）；`BindResource` 先于 `Connect`，`Resource` 晚于 `Disconnect` 销毁。
3. 输入架构：**FramePool 截图 + 混合输入**（打开面板时 `SetForegroundWindow` 一次，面板内全程后台消息）。
4. 目录/配置规范：仓库根 `interface.json`；配置默认程序目录 `config/gui.json`（便携优先）。

### P2 基础框架搭建（8 人日）

| # | 任务 | 内容说明 | 交付物 | 验证标准 | 工期 | 依赖 |
|---|---|---|---|---|---|---|
| 2.1 | GUI 骨架 ✅ | WPF 主窗口：导航框架、明/暗主题（对标 MAA DarkMode）、MVVM 基础类；已建 `src/MaaYys.App`（net9.0-windows，输出 MaaYys.exe）+ PerMonitorV2 manifest + 首页读取引擎版本 | `src/MaaYys.App` | 窗口可切换主题，导航可用（构建 0 警告；exe 启动冒烟存活） | 1.5 ✅ | P1 |
| 2.2 | 引擎封装层 ✅ | `MaaYysEngine` 会话宿主：Resource/Controller/Tasker 统一生命周期（释放顺序固定）；原生回调转 `IObservable`（`Events` / `TaskStateChanges`，可选回派 SynchronizationContext）；`TaskStateMachine` 状态机（Pending/Running/终态 + 非法迁移记录）；原生句柄收敛为 internal | `src/MaaYys.Engine`（Session/）+ `tests/MaaYys.Engine.Tests` | 单元测试覆盖连接/断开/任务投递：**22/22 通过**（含空控制器全链路集成测试） | 2 ✅ | 1.2 |
| 2.3 | 设备管理 ✅ | `DeviceScanner` 自动探测（Win32 正则 + ADB 设备枚举）、`MaaAdbController`、`ControllerCapture` 共用截图实现、`FramePump` 可调帧率采集泵、连接页 UI（设备列表/连接断开/实时预览/参数选择） | `src/MaaYys.Engine/Devices/` + `ConnectionView` | 桌面端已实测：探测命中、FramePool 无黑边、5/10/20 fps 可调（实测 5.0/9.1/15.9）；ADB 路径已实现，模拟器实机验证待环境 | 1.5 ✅ | 2.2 |
| 2.4 | 实例管理 ✅ | `InstanceDefinition`（controller+resource+tasks 组合）/`InstanceStore`（instances.json 原子写、损坏自动备份重建、Current 指针）/`DeviceLease`（设备独占，同设备禁止双实例）/`InstanceCatalog`（读 interface.json 资源与任务，缺失降级）+ 顶部标签条多开 + 实例页草稿式编辑器（含实时预览与连接控制） | `src/MaaYys.Engine/Config/` + `InstancesView` + `MainWindow` 标签条 | 实测增删改切换：3→5→4→3 个实例，Current 指针正确迁移；一次连接 4 个实例仅首个成功（其余被设备独占 / 无 ADB 设备拦截）；**55/55** 单元测试通过 | 1 ✅ | 2.2 |
| 2.5 | 资源加载与切换 ✅ | 按 interface.json resource[].path 顺序加载 bundle，实现同名覆盖；渠道服切换热重载 | 资源加载模块 | 切换官服↔B站服资源不重启程序 | 1.5 ✅ | 1.3 |
| 2.6 | 配置系统 ✅ | 强类型 JSON 配置（对标 MAA gui.new.json）：Configurations 命名组 + Current 指针 + ConfigVersion + 导入导出/迁移 | 配置模块 | 配置改动重启后保持；迁移旧配置不丢 | 1.5 ✅ | 2.1 |

### P3 资源层移植（10 人日）

| # | 任务 | 内容说明 | 交付物 | 验证标准 | 工期 | 依赖 |
|---|---|---|---|---|---|---|
| 3.1 | pipeline 移植与整理 | ~~51 个 pipeline 文件全量移植；统一命名规范；清理无效/调试节点（debug.json/test.json 剔除）~~ **执行结果：51 个文件移植完成，剔除 `test.json` 后 50 个；`debug.json` 经实测必须保留**（`default_pipeline.json` 的 `Default.on_error` 依赖它承载全局兜底链）；修 `寮3042` 无效声明；命名只做统计不改名 | `resource/base/pipeline/` | **✅ 50/50 可解析、0 条悬空引用、框架侧 1511 节点一一对应**（报告：`docs/P3.1-pipeline自检报告.md`） | 2 | P2 |
| 3.1+ | 缓存清理与资源整理 ✅ | 未接线的 3 个任务保持隐藏并列为「受保护根」；设置页「清理图片缓存」按钮；过期资源在资源版本更新时自动整理（多资源集取交集、直接删除） | `src/MaaYys.Engine/Cleanup/` + `SettingsView` | **✅ 实测 v3.13.6：删 278 个文件 + 重写 9 个文件 = 清掉 214 个节点，释放 23.2 MB；剩 154 个不可达全部受保护、0 悬空引用；143/143 测试**（报告：`docs/P6.2-缓存清理与资源整理.md`） | 1.5 | 3.1 |
| 3.2 | 模板图移植 ✅ | 710 张全量迁入；`image/<模块>/<节点名>[_序号].png` 命名规范；去重/压缩**评估后不做** | `resource/resource_pack/base/image/` | **✅ 604 处 template 引用 / 431 张，缺失 0；435 张 6.9 MB、内容重复仅 1 组**（报告：`docs/P3.2-模板图移植.md`） | 1.5 | 3.1 |
| 3.3 | ONNX 模型移植 ✅ | OCR（det/rec/keys）+ 6 个 `.onnx`（4 在用 + 2 版本候补）；记录「model 只写文件名但文件在 detect/ 下」「ocr 无引用却必须保留」两个坑；**`model/` 整个目录纳入受保护（模型不可再生）** | `resource/resource_pack/base/model/` | **✅ 15 个 NeuralNetworkDetect 节点引用的 4 个模型 100% 有文件；10 个文件 / 80.9 MB、受保护 23 项可清理 0 项、整理幂等；⚠ 推理未实机验证**（报告：`docs/P3.3-ONNX模型移植.md`） | 1 | 3.1 |
| 3.4 | 任务定义移植 ✅ | 29 个 import 任务定义移植：task 声明 + option 体系（select/switch/input/checkbox + pipeline_override + 占位符注入） | `tasks/` + `src/MaaYys.Engine/Config/TaskOptions.cs` + `TasksView` | **✅ 29 任务 / 94 选项 / 75 行面板全部渲染；四种类型可编辑、改动即时落盘；`Compile` 产出 P5 用的 override（联动子选项 + `{占位符}` 类型保留）；130/130 测试**（报告：`docs/P3.4-任务定义与选项.md`） | 2 | 3.1 |
| 3.5 | 渠道服分层重构 ✅ | base + 7 渠道层差异资源隔离（**渠道层只写差异字段**，硬规则见报告）；修华为/应用宝不跳开场动画、vivo 关不掉游戏；`StartApp/StopApp` 绑定 + `ChannelLayerResolver` | `resource/resource_pack/<渠道>/` + `src/MaaYys.Engine/Resources/ChannelLayer.cs` | **✅ 8 资源集独立加载全通过：专属节点真正进入启动链、0 条分层规范问题、155/155 测试**（报告：`docs/P3.5-渠道服分层.md`） | 1.5 | 3.1 |
| 3.6 | interface.json 编写 ✅ | 沿用 MXU 结构 + PI V2 schema 校验器（必填/类型、跨段引用、路径存在性、Win32 参数名）；**Win32 参数可配置化**（清单=默认值、实例=覆盖；`win32.screencap` 修正为 FramePool 对齐 P1.4 实测） | `resource/interface.json` + `src/MaaYys.Engine/Config/InterfaceSchemaValidator.cs` | **✅ `probe pi --validate` 错误 0；官方 MaaPiCli 全流程读通（清单→控制器→找到游戏窗口→选资源集）；与 MXU 原版逐字段对照 0 差异；170/170 测试**（报告：`docs/P3.6-interface.json.md`） | 2 | 3.4,3.5 |

### P4 Agent 功能移植（7 人日）

| # | 任务 | 内容说明 | 交付物 | 验证标准 | 工期 | 依赖 |
|---|---|---|---|---|---|---|
| 4.1 | agent 框架搭建 | maa-framework-go **v4**（非 v1.x，后者无 AgentServer）子进程框架；MaaAgentServer 生命周期；与 GUI 的 socket 通信；日志互通 | `agent/` 工程 | GUI 启动时自动拉起 agent，退出时回收 | 1 ✅ | P1.5 |
| 4.2 | 核心动作移植 | RandomWait、SwitchSoul、ClearHitCount、RepeatChallengeNTimes、BonusToggleAction、TimeCheck、RandomSwipe、ReStartGame | agent 动作集 A | 8 个动作冒烟通过 | 3 ✅ | 4.1 |
| 4.3 | 高级动作移植 | HumanTouch、ChallengeDungeonBoss、BattleTeam、RandomTouch、RandomTask、QuestionMatcher、Guess、AutoFoster、AutoBattle | agent 动作集 B | 9 个动作冒烟通过 | 2 ✅ | 4.2 |
| 4.4 | 自定义识别移植 | TaskCounterRecognition（次数识别）、CoBattleTargetRecognition（组队目标识别） | agent 识别集 | 识别结果与截图比对正确 | 1 ✅ | 4.1 |

### P5 核心功能实现（10 人日）

| # | 任务 | 内容说明 | 交付物 | 验证标准 | 工期 | 依赖 |
|---|---|---|---|---|---|---|
| 5.1 | 任务队列系统 ✅ | 对标 MAA TaskQueue：拖拽排序、同类任务多实例+重命名、启用/禁用、状态着色（跳过/完成/运行中/失败）、「从此处运行」右键菜单 | 任务链 UI + 模型 | 排序持久化；状态颜色实时正确 | 3 ✅ | 2.1,3.4 |
| 5.2 | 选项系统 ✅ | select/switch/input 渲染；pipeline_override 下发；`{param}` 占位符注入；选项嵌套；任务级 + 全局级选项（MXU setting 扩展） | 选项面板组件 | 改选项后重跑任务行为符合预期（编译合并已单测；运行时行为待 5.3 投递时兑现） | 2 ✅ | 3.4 |
| 5.3 | 任务执行引擎 ✅ | 启动/停止/暂停；异步状态回调；任务超时（对标 MAA StallTimeout）；失败重试策略；运行时长上限 | `src/MaaYys.Engine/Execution/` + 任务页运行控制条 | ✅ 可随时停止且状态一致；超时正确触发（209/209 测试，含真引擎 Null 控制器集成 2 例） | 2 ✅ | 2.2,5.1 |
| 5.4 | 定时任务 ✅ | 定时器（时/分 + 绑定配置，条数不限）；周计划（7 位掩码，支持每天/工作日/周末/任意组合）；强制定时启动；~~开机自启~~（移交 P6.1 应用设置） | `Engine/Scheduling/` + `Services/TimerRunner.cs` + 定时页 | ✅ 到点触发准确、同分钟不重复、重启不补跑；周计划判断正确（229/229 测试，含真引擎定时链路 1 例） | 1.5 ✅ | 5.3,2.6 |
| 5.5 | 日志系统 ✅ | 运行日志分级展示；agent 日志合并（stdout/stderr 重定向）；日志缩略截图卡片（对标 MAA UseCardLog）；日志导出（文本/CSV + 截图目录）；时间格式配置 | `Engine/Logging/` + 日志页 | ✅ 266/266 测试（新增 37）；实机捕获 agent 输出 5 行；导出实测出 txt + PNG | 1.5 ✅ | 2.1,5.3 |

### P6 UI 与设置体系（8 人日）— 对标 MAA v6.20.1

| # | 任务 | 内容说明 | 交付物 | 验证标准 | 工期 | 依赖 |
|---|---|---|---|---|---|---|
| 6.1 | 主界面布局 ✅ | **顶部四页签**（一键长草 / 多实例 / 小工具 / 设置）；一键长草 = 左任务卡（勾选 + 齿轮 + ⊕/全选/清空）+ 完成后 + Link Start!，右「多任务共用 + 常规/高级设置」；实时截图在「多实例」；定时/日志/设备探测在「小工具」 | `Views/MainWindow.xaml` + `Views/Pages/TasksView.xaml` + `Controls/TaskQueueView.xaml` + `ViewModels/ToolsViewModel.cs` | ✅ 1280×760 / 1040×660 / 明暗双主题截图均无截断；空队列、连接失败、高级设置均实测（详见 `docs/P6.1-主界面布局.md` §8） | 2 ✅ | 5.1 |
| 6.2 | 设置中心-系统类 | 连接设置（Win32Extra：截图/鼠标/键盘方式、静音、窗口正则可配置）、启动设置（前置程序路径/参数/等待秒数、**开机自启**）、运行设置（区服、前后置脚本、防休眠、时长上限；P5.3/P5.4 写死的停滞超时/重试/失败即停/定时轮询间隔、日志落盘） | 设置页 1 | 每个设置项改动即时生效并持久化 | 3 | 2.6 |
| 6.3 | 设置中心-应用类 | 通知（卡住超时/提醒间隔）、外部通知（多通道+触发条件）、远程控制（HTTP 端点/轮询/身份）、第三方数据（可选）、更新（源/代理/自动下载安装）、公告 | 设置页 2 | 外部通知可真实收到测试消息 | 1.5 | 6.2 |
| 6.4 | 界面细节打磨 | 暗色主题（对标 MAA DarkMode）、多语言 i18n、托盘最小化、页签/任务链过渡动画、日志时间格式、新手分步引导（对标 Guide.StepIndex） | UI 细节 | 明暗主题切换无残留；引导可跳过 | 1.5 | 6.1 |
| 6.5 | 任务设置面板 | 每个任务右侧滑出齿轮面板编辑参数；悬浮提示；选项预览（对标 MXU showOptionPreview） | 任务设置交互 | 不打断任务运行可改参数（下次生效） | 1 | 5.2,6.1 |

### P7 增强功能（7 人日）

| # | 任务 | 内容说明 | 交付物 | 验证标准 | 工期 | 依赖 |
|---|---|---|---|---|---|---|
| 7.1 | 外部通知 | Webhook / Server 酱 / Bark 等通道；触发条件（完成/出错/卡死/定时前/日志关键词）；消息模板 | 通知模块 | 端到端送达；限流不轰炸 | 2 | 5.3 |
| 7.2 | 远程控制 | HTTP API：查询状态、投递任务、停止；Token 鉴权；仅监听 127.0.0.1（可选局域网） | 远程控制模块 | Postman 脚本全通过 | 2 | 5.3 |
| 7.3 | 数据统计 | 本地任务统计（次数/时长/掉落记录）；报表导出 CSV；界面图表 | 统计模块 | 数据与日志一致性校验通过 | 1.5 | 5.5 |
| 7.4 | 自动更新 | 检查更新（GitHub / Mirror 酱源）；后台下载；安装重启；版本类型（stable/nightly）；代理支持 | Updater | 跨小版本升级成功且配置保留 | 1.5 | P8 前可并行 |

### P8 测试与发布（6 人日）

| # | 任务 | 内容说明 | 交付物 | 验证标准 | 工期 | 依赖 |
|---|---|---|---|---|---|---|
| 8.1 | 全功能冒烟 | 29 个任务逐一冒烟（从庭院启动）；异常场景（断连、窗口最小化、弹窗干扰） | 冒烟报告 | 29/29 有明确结论；阻断性问题清零 | 2 | P5-P7 |
| 8.2 | 兼容性矩阵测试 | Win10/Win11；桌面端多版本；庭院皮肤 v5/v6；8 渠道服登录流程；模拟器 ADB | 兼容性报告 | 各组合有记录；已知问题分级 | 1 | 8.1 |
| 8.3 | CI/CD | GitHub Actions：多架构（x64/arm64）打包；git-cliff 生成 changelog；tag 触发 Release；JSON 校验 + agent 编译门禁 | 工作流 | push tag 自动产出可安装包 | 1.5 | P7 |
| 8.4 | 文档与帮助 | README、使用手册（安装/连接/任务/设置/FAQ）、开发文档（pipeline 规范/agent 扩展/资源热更新）、免责声明 | 文档集 | 新手按文档 10 分钟内跑通首个任务 | 1 | P7 |
| 8.5 | 灰度发布 | 小范围发布（QQ 群/Release pre）；反馈收集闭环；热修复资源包机制验证 | 灰度版 | 反馈渠道畅通；资源热更新链路可用 | 0.5 | 8.3,8.4 |

---

## 3. 里程碑与验收标准

| 里程碑 | 时点 | 验收标准 |
|---|---|---|
| M1 原型打通 | P1 末 | Win32 连桌面端 + 一次完整「识别→点击」闭环；agent 链路通 |
| M2 框架就绪 | P2 末 | GUI 骨架 + 引擎封装 + 配置系统可用；单任务跑通 |
| M3 资源就绪 | P3 末 | 29 任务全量加载；渠道服切换正常；MXU 交叉兼容 |
| M3.5 Agent 能力就绪 | P4 末 | 19 个自定义能力（17 动作 + 2 识别）全注册；`RandomWait` 真实动作经 pipeline 跑通；GUI 自动拉起/回收 agent 子进程 |
| M4 功能就绪 | P5 末 | **「自动御魂」端到端全自动运行 ≥1 小时无阻断**（含换阵容/加成/次数识别） |
| M5 体验达标 | P6 末 | 设置中心 11 类全量落地；界面交互评审通过 |
| M6 发布候选 | P8 末 | 冒烟 29/29、CI 自动出包、文档齐全、灰度可回滚 |

## 4. 工期与人力估算

- **总量**：63 人日（P1–P8）
- **单人**：约 3 个自然月（含 20% 缓冲 ≈ 3.5 月）
- **双人并行**（引擎/资源 + GUI/设置）：约 6-7 周
- **资源移植（P3/P4）可与框架搭建（P2）并行**：若两人分工，可压缩至 5 周左右

## 5. 关键依赖与风险登记

| 风险 | 影响阶段 | 缓解措施 |
|---|---|---|
| 桌面端更新导致识别素材失效 | P3/P8 | 资源与程序分离，支持资源包独立热更新（对标 MaaYYs CI 仅发资源） |
| Win32 后台点击在部分版本失效 | P1.4 | 鼠标/键盘方式配置化（SendMessage/PostMessage/… 可选） |
| **DPI 缩放致截图黑边、点击坐标偏移** | P1.4（已验证 125% 下偏移约 200px） | **截图强制 FramePool（并保留 PrintWindow 回退）；进程声明 PerMonitorV2 DPI 感知** |
| **主界面 UI 点击需窗口前台**（10 组对照实测） | P2/P5 | **混合输入：打开面板时 SetForegroundWindow 一次，面板内全程后台消息；降低抢焦点频次** |
| **Go 绑定版本陷阱**（v1.x 无 AgentServer，v4 需 Go 1.25） | P4 | ✅ **已闭环**：agent 固定 maa-framework-go/v4；本机 1.24.12 通过 `GOTOOLCHAIN=go1.25.0` + 本地 module cache 离线构建（见第 15 节） |
| Go agent 与 C# GUI 通信稳定性 | P4/P5 | ⚠ **部分闭环**：子进程生命周期（拉起→握手→BindResource→回收）+ best-effort 降级已实现；**心跳/断线重连/守护重启待 P5.3 长跑验证** |
| 29 任务同时适配工作量大 | P3 | 按「日常→副本→PVP→活动」优先级分批验收，活动类后置 |
| MAA 设置体系庞大导致范围蔓延 | P6 | 11 类设置按「系统类→应用类」两批交付，每批评审后冻结 |
| OCR 识别队名/层数失败 | P8 | 预设名称规范提示 + 识别置信度日志 + 失败跳过策略 |
| **模型文件不可再生** | P3/P8 | ✅ 已处理：恢复 `Courtyard-v5.0` / `special-icon-v0.1.0`，`model/` 整个目录纳入受保护名单（模板图仍按「有无引用」判定），见第 13 节 |
| **WPF 窗口最小化恢复后客户区空白**（DWM 重定向表面异步重建，偶发） | P2（已修复） | Win32 `SWP_FRAMECHANGED` + `RedrawWindow` 强制重绘；PrintWindow 内容自愈守卫 + 1px 尺寸抖动兜底；`minimize-loop` 压力测试量化验证 |
| **Windows 效率模式（EcoQoS）降频/挂起后台进程**，拖慢恢复 | P2（已修复） | 启动时 `SetProcessInformation(ProcessPowerThrottling)` 关闭执行速度节流 |
| **游戏窗口「最小化→控制器恢复」后 alpha=0 幽灵化**（可见性/几何全对但桌面全透明） | P2（已修复） | `GameWindowVisibility.EnsureVisible`：alpha 0→255、移除 `WS_EX_TRANSPARENT`、强制重绘；连接时查一次 + 2 秒独立巡检兜底；最小化状态一律不碰 |

---

## 6. 下一步行动与完成情况（P1 评审通过后）

### 6.1 已完成（按阶段归拢）

1. ✅ **P1** 六项任务 + 评审全部完成，方案冻结
2. ✅ **P2 全部完成**：P2.1 GUI 骨架 / P2.2 引擎封装 / P2.3 设备管理 / P2.4 实例管理 / P2.5 资源加载与切换 / P2.6 配置系统
3. ✅ 资源部署落定（方式二）：仓库根 `resource\` 为唯一来源，构建时自动同步到 exe 同目录，不再依赖环境变量
4. ✅ 缓存清理与资源整理（2026-10-26）：三个未接线任务**保持隐藏并受保护**；设置页只留「清理图片缓存」一个按钮，过期资源在资源版本更新时自动整理、直接删除（详见第 10 节）
5. ✅ P3.1 pipeline 移植与整理（自检器已产品化，详见第 9 节）
6. ✅ P3.2 模板图 / P3.3 ONNX 模型文档收尾（2026-10-26）：硬指标早已达标，报告补齐（详见第 13 节）
7. ✅ P3.4 任务定义与选项（2026-10-26）：任务页渲染 29 任务参数、`pipeline_override` 编译就绪（详见第 11 节）
8. ✅ P3.5 渠道服分层重构（2026-10-26）：渠道层只写差异、`package` 由引擎直读并显式 StartApp（详见第 12 节）
9. ✅ P3.6 interface.json 编写（2026-10-27）：PI schema 校验 0 错误、MaaPiCli 交叉兼容、Win32 参数可配置化（详见第 14 节）——**P3 全部完成**
10. ✅ **P4 Agent 功能移植**（4.1–4.4 全部完成；17 动作 + 2 识别共 19 能力；验收见 `docs/P4-agent功能移植.md` 与第 15 节，**M3.5 Agent 能力就绪里程碑达成**）

### 6.2 进行中

11. ✅ **P5 核心功能实现全部完成**（5.1 ✅ → 5.2 ✅ → 5.3 ✅ → 5.4 ✅ → 5.5 ✅）
    - **P5.1 进度（2026-10-27）**：任务队列模型（多实例唯一 Id / 拖拽重排 / 别名）落地，配置持久化通过单测（`TaskConfigurationTests`）；任务页升级为可拖拽队列（运行态色条、inline 重命名、同类 ×N、右键「从此处运行 / 重命名 / 上移下移 / 删除」、添加任务下拉）；`RunFromHere` 与执行引擎运行时驱动留待 5.3。
    - **P5.2 进度（2026-10-27）**：全局级选项（MXU setting 扩展）落地——`TaskConfiguration.GlobalOptions` 持久化；`InterfaceConfig` 校验 `setting`/`global_option` 引用并暴露 `GlobalOptionKeys()`；`TaskOptions` 新增全局层合并（`Compile` 4 参重载 / `CompileGlobal`）与显式键 `Describe`；任务页右侧改为「任务参数 / 全局选项」双页，全局页按 setting 段分组渲染；真实清单新增 `tasks/_global_options.json`（2 个全局选项）。验收：190/190 测试、PI V2 错误 0、29 任务渲染无回归（详见 `docs/P5.2-选项系统.md`）。
    - **P5.3 进度（2026-10-27）**：执行引擎落地——新增 `Engine/Execution/`（`QueueExecutor` 顺序投递 + 暂停/停止 + 重试 + 停滞超时看门狗 + 单任务时长上限；`IExecutionHost` 抽象 + `MaaEngineExecutionHost` 适配；`ExecutionPlan` 把队列项编译成 `pipeline_override`）；任务页新增「开始运行 / 暂停·继续 / 停止」控制条，`RunFromHere` 与 `SetStatus` 真正接入执行器，色条随运行态变化。验收：209/209 测试、0 警告 0 错误（详见 `docs/P5.3-任务执行引擎.md`）。
    - **P5.4 进度（2026-10-27）**：定时任务落地——新增 `Engine/Scheduling/`（`TimerEntry` 数据模型 + `TimerRules` 纯函数规则 + `TimerScheduler` 20 秒轮询与双重去重）；定时计划随配置组存于 `config/maayys.json` 顶层 `timers`（跨组共享，含 `last_fired` 分钟键防重启补跑）；**执行入口提到 workspace 层**（`Services/QueueRunService`），任务页与定时器共用同一执行器，天然互斥；`Services/TimerRunner` 处理「配置组缺失 / 已有队列在跑 / 强制定时启动（先停后启，最多等 30 秒）」，消息经事件回 UI；新增「定时」页（导航第 3 项，行内直接编辑、改动即落盘、试跑 / 立即检查 / 下次触发展示）。开机自启改归 P6.2「启动设置」。验收：229/229 测试、0 警告 0 错误（详见 `docs/P5.4-定时任务.md`）。
    - **P5.5 进度（2026-10-27）**：日志系统落地——新增 `Engine/Logging/`（`LogBuffer` 环形缓冲 + `FrameworkLogTranslator` 原生事件翻译 + `EngineLogPipeline` 管线 + `ThumbnailFactory` 块平均缩放 + `LogExporter` 文本/CSV 格式化 + `LogOptions` 策略持久化）；**agent 子进程 stdout/stderr 重定向并入同一日志流**（`MaaAgentSession.OutputReceived`，异步读不阻塞子进程）；警告/错误自动附最近一帧缩略图，点击可看大图/另存 PNG；新增「日志」页（导航第 4 项，级别/来源/关键字过滤、时间格式、自动滚动/暂停、节点级事件开关、复制/导出/清空）。顺带修掉暗色主题下 **CheckBox 标签不可见**的全局渲染问题。验收：266/266 测试、agent 实机捕获 5 行、导出实测出 txt + PNG（详见 `docs/P5.5-日志系统.md`）。

12. **P6 UI 与设置体系（1/5）**
    - **P6.1 进度（2026-10-27）**：主界面落地——导航第 1 项由「首页」换成「主界面」（`WorkbenchView`）：左任务链（340px）+ 右「连接 / 截图 / 日志」三面板，三块都可折叠（`PanelStateViewModel.RowHeight` 直接绑 `RowDefinition.Height`，折叠回落 `Auto`）；任务链从任务页抽成共用控件 `Controls/TaskQueueView`，**`MainViewModel.Tasks` 成为唯一实例**（主界面 / 任务页 / 顶部工具栏三处同源）；顶部工具栏接上「开始 / 暂停 / 停止」与 `Tasks.RunOverview` 运行总览；日志面板按 `LogBuffer.Version` 轮询增量、只留 200 行、自动跟到最新行。顺带修掉三个真实缺陷：队列增删后摘要与「开始」按钮不刷新（`RaiseCommandStates` 漏发通知）、连接失败从不落日志（日志面板在设备未启动时是空的）、禁用态主按钮仍高亮。验收：266/266 测试、1280×720 与 1040×640 截图无截断、折叠与增量日志实测（详见 `docs/P6.1-主界面布局.md`）。

### 6.3 已知遗留（未闭环，非阻塞）

- **ADB 渠道服实机验证**：~~需自行读渠道包 `package` 并显式调 `MaaControllerPostStartApp`~~ → P3.5 已实现（`MaaControllerApps`），仅差 ADB 实机环境验证（见第 12 节）
- ~~待产品决策：3 个未 import 的任务是否开放~~ → 已决策：**先隐藏**，设置页给出「加一行 import 即可启用」的方法（见第 10 节）
- **P4 游戏交互类动作真机校准**：MXU 的 `agent/` 无源码，8 个游戏 UI 类动作按 param schema 重建，需接阴阳师窗口微调（见第 15 节）
- **开机自启**：从 P5.4 移交，**改归 P6.2「启动设置」**——原计划并进 P6.1，但 P6.1 是纯布局；开机自启要配注册表 `Run` 键 + 静默启动参数 + 关闭行为，与「前置程序 / 等待秒数」是同一组设置，跟 P6.2 一起做才不割裂（「最小化到托盘」仍在 P6.4）
- **P5.3/P5.4 策略参数未暴露界面**：停滞超时 60s、不重试、失败即停、定时轮询 20s 目前写死默认值 → 归 **P6.2 运行设置**
- **日志只保留在内存**：P5.5 的缓冲关掉程序即丢，没有滚动文件；若要"崩溃后回看"，需加文件 sink（按大小滚动 + 保留 N 份）→ 归 **P6.2 运行设置**
- ~~日志面板尚未并入主界面~~ → **P6.1 已闭环**：主界面右栏常驻日志面板（增量 + 自动滚动），需要过滤时点「完整日志」跳日志页
- **主界面面板折叠状态不持久化**：重启回到三面板全展开，要记状态得进配置 → 归 P6.2 起的设置项

*本流程表基于《MaaYys-知识获取报告.md》的技术结论编制，工期为估算值，评审后冻结为基线。*

---

## 7. P2.5 验收补记：同名覆盖的最终结论（2026-10-26）

补充 `MaaResourceGetNodeData` 后（导出存在，StringBuffer 绑定已齐全），用探针读出节点合并后的最终定义：

- `resource --switch "bilibili服" --node 启动游戏` → 官服链与 B站服链读回的 JSON **一字不差**
- `resource --proof` → 构造临时覆盖包改 `post_delay`，**1000 → 12345 生效** → **覆盖机制本身 100% 正常**

差异消失的根因在资源包侧：7 个渠道目录只覆盖 `package`，而 MaaFramework 规定
`package` **仅在 `action` 为 `StartApp`/`StopApp` 时有效**；base 的 `启动游戏` 是 `DirectHit`，
裸 `package` 被当作未知字段丢弃。

| 影响面 | 结论 |
|---|---|
| P2.5 验收（bundle 顺序 + 同名覆盖） | ✅ 达成，机制已由构造实验直接证明 |
| Win32 桌面端的渠道服差异 | 无差异（桌面端不靠 `package` 启动应用），符合预期 |
| ADB 渠道服（P2.3 遗留项） | ⚠ 需自行读取渠道包 `package` 并显式调 `MaaControllerPostStartApp`，不能依赖 pipeline 裸字段 |

---

## 8. P2.6 配置系统验收（2026-10-26）

**数据模型（两项决策已确认）**

1. **独立配置组 + 实例可选引用**：配置组不挂在实例下，`InstanceDefinition.ConfigurationName` 存「用哪一组」，
   空串 = 跟随 `Current` 指针。多开时每个实例可各用一套，也能多个实例共用一套。
2. **旧配置自动提升**：读到旧 `instances.json` 的 `task_entries` 且尚无配置组时，
   为每个有任务的实例建同名配置组并写回组名；已有配置组则不迁移（不覆盖手工配置）。

**文件分工**：`instances.json` = 连哪个设备；`maayys.json` = 跑哪些任务、选项怎么选。

**验收结果**

| 验收项 | 结论 |
|---|---|
| 配置改动重启后保持 | ✅ 4 个连续进程（新建/加任务/再新建/重启读取/重命名/导出）内容与 Current 全部保持 |
| 迁移旧配置不丢 | ✅ 2 实例 5 条目 → 2 组、去重后 4 个任务、`configuration_name` 写回、二次迁移改写 0 个 |
| ConfigVersion | ✅ 高于程序版本时兼容读取并提示 |
| 命名组 + Current | ✅ Current 缺失/指向不存在组时回退首个；改名/删除时实例绑定同步维护 |
| 导入导出 | ✅ 自有全量/单组往返；同名自动加后缀；可直接导入 MAA `gui.new.json` |
| 容错 | ✅ 坏 JSON 备份为 `.bad-时间戳` 后重建；空名组丢弃；重复任务去重并提示 |
| 测试 / 构建 | ✅ 82/82；0 警告 0 错误 |

**留到后续**：选项值目前只做存取与保留，编辑入口属 P6.2 设置中心。

---

## 9. P3.1 pipeline 移植与整理验收（2026-10-26）

完整报告见 `docs/P3.1-pipeline自检报告.md`，此处只留结论与需要记住的语义。

| 验收项 | 结果 |
|---|---|
| 51 个 pipeline 文件全量移植 | ✅ 移植 51；剔除 `test.json` 后 **50** |
| JSON 全部可解析 | ✅ 50/50，0 失败 |
| 节点引用无悬空 | ✅ 0 条（`next` / `on_error` / `interrupt` / 锚点 / `default_pipeline.json`） |
| 框架侧一致性 | ✅ `MaaResourceGetNodeData` 可见 1511 = 静态扫描 1511 |
| 统一命名规范 | ⚠ 58 个（3.8%）不规范，**只统计不改名**（改一个名要同步 800+ 处引用） |
| 清理调试节点 | ✅ 删 `test.json`（16 节点全不可达、agent 二进制零引用）；**`debug.json` 必须保留** |

**与流程表原计划的两处偏离（均有实测依据）**

1. **`debug.json` 不能剔除**：`default_pipeline.json` 的 `Default.on_error` 指向它定义的
   `悬赏封印_协助战斗1`，而 `Default` 段对**所有节点**生效。删掉等于每个节点都引用悬空。
2. **同名覆盖是字段级合并**：`official/启动游戏.json` 只写 `package`，base 的 `recognition`/`next`
   在 `GetNodeData` 里仍然存在 → 渠道层只需写差异字段（P3.5 按此实现，不必复制整节点）。

**新增能力**：`MaaYys.P1Probe pipeline [--layer] [--verify]`；引擎侧
`PipelineInventory` / `PipelineDefaults` / `ResourceReferenceScope`；22 个单元测试锁住全部语义坑。

**P4 输入**：17 个自定义动作 + 2 个自定义识别（含仅渠道层引入的 `RandomTouch`）。

**待产品决策**：`interface.json` 只 import 了 29/32 个任务文件，未导入的
`限时活动` / `活动爬塔-武道大会-修行合训` / `自动业原火` 涉及 72 个节点；
补 import 后可达节点 1039 → 1111，三份 pipeline 全部复活。

---

## 10. 缓存清理与资源整理验收（2026-10-26·第二版）

完整说明见 `docs/P6.2-缓存清理与资源整理.md`，此处只留结论。

**需求 → 落地**

| 需求 | 做法 |
|---|---|
| 三个任务先隐藏，以后同类活动改一下就能用 | `interface.json` 的 import 保持 29 个；三份 `tasks/*.json` 与 72 个 pipeline 节点完整保留，登记为「受保护根」，整理永不触碰 |
| 清理功能就是一个按钮「清理图片缓存」 | 设置页只留这一个按钮；弹确认 → 直接删 `debug/`、`cache/` 下的图片（日志保留），不进回收站 |
| 过期资源在版本更新时删掉（不进回收区） | `ResourceMaintenance`：`version` 或资源指纹变化 → 启动后台整理 → `ResourcePruner` 直接删；结果写 `debug/resource-prune.log` 与 `config/resource-state.json` |

**判定门槛（核心：不可达 ≠ 可删）**

| 层 | 内容 | 处理 |
|---|---|---|
| 受保护 · 隐藏任务 | 未 import 任务的入口链（72 个节点） | ❗️ 永不 |
| 受保护 · 约定保留 | `活动/` 下的轮换包、`debug.json`（全局兜底链）、`model/ocr/*`（框架内置 OCR 隐式依赖） | ❗️ 永不 |
| 整理 · 节点 | 从任何任务入口/覆盖目标都走不到（可达性遍历 + 锚点解析） | 删节点、重写 JSON |
| 整理 · 资源 | 模板图/模型未被任何节点引用，且文件名不在任何 pipeline 原文里 | 删文件 |
| 整理 · 配置 | `*.bak` / `*.bad-*` | 删文件 |

**为什么取交集**：`interface.json` 有 8 个资源集，节点在官服走不到、在 bilibili 服却有人用 → 不能删。
所以每套各扫一遍，**每一套都判可整理、节点名交集非空**才动手。
删掉死节点会让「只被它引用的图」变成新废物，所以一次整理要**循环到不动点**（实测 2 轮收敛）。

**实测**（v3.13.6 全量，8 个资源集）

- 整理前：节点 1511（可达 1138 / 不可达 373）；可整理 10 项 + 需确认 175 项 · 受保护 17 项
- 整理：**删 278 个文件 + 重写 9 个文件 = 清掉 214 个节点，释放 23.2 MB**，资源 99 MB → 76 MB
- 整理后：1292 节点、不可达 154（**全部受保护**）、仍可整理 0 项、0 悬空引用；`pipeline` 自检 0 错误、模板图缺失 0
- 再跑一次 → 「没有需要整理的过期资源」（幂等）
- 图片缓存：界面 10 张 / 10.2 MB → 点一次删完，`*.log` 全部保留

**新增接口**：`probe audit [--prune] [--json] [--no-assets]`、`probe cache [--clear]`；GUI 调试参数 `--clear-cache`、`--scroll-end`。

**测试**：143/143（新增 13 个用例：隐藏任务保护、活动包/debug/OCR 保护、配置备份识别、审计只读、
整理只认可清理项、越界拒绝、缩进与行尾保留、dry-run 不动文件、多资源集取交集、
版本未变不重复整理、版本变化后再次整理、图片缓存只删图不删日志）。

---

## 11. P3.4 任务定义与选项验收（2026-10-26）

完整说明见 `docs/P3.4-任务定义与选项.md`，此处只留结论。

| 验收项 | 结果 |
|---|---|
| GUI 选项面板渲染全部 29 任务参数 | ✅ 29/29（`probe tasks` 逐任务跑通） |
| 选项规模 | ✅ 94 个定义：select 21 / switch 43 / input 35 / checkbox 5（checkbox 全部未被任务引用，界面不出现） |
| 面板行数（含联动展开） | ✅ 75 行 |
| 改动持久化 | ✅ 立即写 `config/maayys.json`；重启后取值保持（实测自动御魂三处） |
| 编译 `pipeline_override` | ✅ 任务级 → 选项级 → case 级按字段深合并；`{占位符}` 整串替换保留类型、内插按文本 |

**取值编码约定**（写死在 `TaskOptions` 一处，别在别处重定义）

| 类型 | 键 | 值 |
|---|---|---|
| select / switch | 选项名 | case 名 |
| checkbox | 选项名 | `a,b` |
| input | `选项名/字段名` | 用户输入字符串 |

默认值**不入库**：未设置时回落到 `default_case` / `inputs[].default`，资源更新新增选项时旧配置自动拿到默认值。

**一个必须记住的坑**：`select` 的联动子选项只在被选中时存在（自动御魂选「大蛇」→ 层数是
`悲鸣/拾层/…`；选「业原火」→ `痴之阵/嗔之阵/贪之阵`），所以参数面板是**按当前取值重建的**，
不能在初始化时把整棵树缓存下来。

**数据侧发现**：19 个选项定义了但没有任何任务引用（含 5 个 checkbox），属上游历史残留，不影响渲染，未清理。

**新增接口**：`probe tasks [--task] [--value] [--json]`；GUI 调试参数 `--task`、`--set-option "任务|选项|取值"`。

---

## 12. P3.5 渠道服分层重构验收（2026-10-26）

完整报告见 `docs/P3.5-渠道服分层.md`，此处只留结论。

**分层契约**（渠道层 = 差异层）

1. 渠道层只写与 base 的差异字段（字段级合并，数组整体替换）；
2. **绝不用渠道层覆盖「启动游戏」的 next** —— 那会跳过 base 的「启动游戏-跳过动画」；
   专属节点挂到「启动游戏-跳过动画」的 next 上；
3. `关闭阴阳师` 的 package 必须是纯包名（`am force-stop` 不接受 activity）；
4. `package` 是唯一允许显式重复 base 同值的字段（渠道身份声明）。

**package 必须由引擎直读文件**：框架规定 `package` 仅在 `action=StartApp/StopApp` 时合法，
base 的「启动游戏」是 DirectHit（桌面端语义）→ 渠道层写的 package 被当未知字段丢弃，
`GetNodeData` 读不出来。引擎侧 `ChannelLayerResolver.ResolvePackages` 直读文件取包名，
Adb 控制器下显式 `MaaControllerPostStartApp/StopApp`（新增绑定，Null/Win32 优雅降级）。

**修掉的两个真实缺陷**

| 缺陷 | 影响 | 修复 |
|---|---|---|
| 华为/应用宝覆盖「启动游戏.next」，base 后来加的「启动游戏-跳过动画」不在链上 | 这两个渠道服**永不跳开场动画** | 专属节点改挂「启动游戏-跳过动画.next」 |
| vivo「关闭阴阳师.package」带 activity | `am force-stop` 失败，**关不掉游戏** | 改为纯包名 |

**实测**：8 资源集独立加载全通过（0.16~0.88 s）；华为 1 个/应用宝 2 个专属节点经 `GetNodeData`
核对真正进入启动链；0 条分层规范问题；P3.1 自检与资源整理审计无回归；155/155 测试（新增 12 个）。

**新增接口**：`probe channel [--set 资源集] [--verify]`。

**遗留**：应用宝链缺「关闭广告/客户端更新提示」两项，保持上游行为待实测；ADB 实机链路验证待环境；
`ReStartGame`（P4）的启动逻辑届时改走 `MaaControllerApps`。

---

## 13. P3.2 模板图 / P3.3 ONNX 模型验收（2026-10-26）

硬指标在此之前就已达标（每次资源自检都在跑），本次补齐报告：
`docs/P3.2-模板图移植.md`、`docs/P3.3-ONNX模型移植.md`。

### P3.2 模板图

| 验收项 | 结果 |
|---|---|
| 710 张模板图迁移 | ✅ 全量迁入，**没有 MXU 有而我们没有的漏项** |
| 命名规范 `任务名/节点名_序号.png` | ✅ 主体符合；1 个顶层散落（编辑器导出带 ROI 坐标）+ 6 个空目录，**登记不处理** |
| 压缩去重 | ✅ 已评估：435 张仅 6.9 MB、内容完全重复仅 1 组 2 张、>200 KB 仅 3 张 → **收益 ~0，不做** |
| template 引用 100% 有对应文件 | ✅ **604 处引用 / 去重 431 张，缺失 0**（相对路径 + 文件名双口径都是 0） |

现状 435 张 / 6.9 MB（MXU 710 张 / 9.8 MB）：差额 275 张是 P6.2 资源整理删掉的无引用图，
删前备份在 `D:\YYS\backup-maayyss-resource-preprune-20261026.tar.gz`。

### P3.3 ONNX 模型

| 验收项 | 结果 |
|---|---|
| OCR（det / rec / keys）迁移 | ✅ `model/ocr/` 4 个文件就位，已列为受保护 |
| 检测模型迁移 | ✅ 6 个 `.onnx`（4 在用 + 2 版本候补）就位，`model/` 整个目录受保护 |
| NeuralNetworkDetect 可加载推理 | ⚠ **未实机验证**（环境无游戏窗口；引用与文件已静态核对 0 缺失） |

现状 10 个文件 / 80.9 MB（与 MXU 一致）。

**两个坑（写死在这里，别再踩）**

1. `model` 字段只写**文件名**（`BarrierCard.onnx`），文件却在 `model/detect/` 下 →
   任何自写的缺失校验都必须**递归匹配文件名**，不能按目录精确路径比对；
2. `model/ocr/*` 在 pipeline 里**零引用**，却是框架内置 OCR 按固定路径加载的隐式依赖，
   删了 OCR 全废 → 已受保护。

**已处理（2026-10-27）**：`Courtyard-v5.0.onnx` / `special-icon-v0.1.0.onnx`（共约 20 MB）在首次整理时
被当作「无引用资源」删掉，现已从 MXU 原包恢复，并把 `model/` 整个目录纳入受保护名单。

判定因此分成两套：**模板图看有没有引用**（可再生，无引用就参与整理）；**模型一律不看**（不可再生，
模板图能重截、模型只能重新训练或找回原文件；`model/` 总共 6 个 `.onnx` 约 60 MB，全留代价远小于
「以后要用时找不到」）。兼容性矩阵里要测「庭院皮肤 v5/v6」，v5 的模型必须还在。

复跑结果：模板图 435 张（未引用 0）、模型 10 个（未引用 0）、受保护 23 项、可清理 0 项；
`probe audit --prune` 连跑两次都是「没有需要整理的过期资源」。155/155 测试。

---

## 14. P3.6 interface.json 验收（2026-10-27）

完整报告见 `docs/P3.6-interface.json.md`，此处只留结论。

| 验收项 | 结果 |
|---|---|
| 通过 PI schema 校验 | ✅ `probe pi --validate`：**错误 0 · 警告 1**（Adb 段缺省，预期内） |
| MXU 也能加载（交叉兼容） | ✅ 官方 `MaaPiCli.exe` 用我们的清单走通全流程（含**自己找到游戏窗口** 0x1d07b0）；逐字段对照 MXU 原版：顶层 21 字段完全一致、0 丢失 0 新增 |
| controller（Win32 参数可配置化） | ✅ 清单 = 默认值、实例字段 = 覆盖；新建实例预填清单默认值 |
| 测试 / 构建 | ✅ 170/170（新增 15 个）；0 警告 |

**校验器分级**：Error（协议不合法/引用悬空/路径不存在）· Warning（能跑但值得看）· Info（统计）。
一个必须记住的行为：**键鼠/截图方式写错名字，MaaFramework 是静默回退而不是报错**
（截图→PrintWindow、输入→SendMessage），所以校验器把它定为 Error。

**顺手修掉的不一致**：MXU 原清单 `win32.screencap: PrintWindow` 与程序实际用的 FramePool 不一致
（P1.4 实测 PrintWindow 在 DPI 125% 下黑边约 20%、点击偏移）。P3.6 让清单成为权威，
所以把清单改成 `FramePool`——这是本次对清单唯一的实质修改。

**M3 资源就绪里程碑达成**：29 任务全量加载 ✅ · 渠道服切换正常 ✅ · MXU 交叉兼容 ✅

**P3 遗留**：`display_short_side` 尚未参与截图缩放（P6.2 连接设置）；~~`setting` 段未启用（MXU 也没用）~~ → **P5.2 已启用**（全局级选项，见第 16 节 / `docs/P5.2-选项系统.md`）；
`NeuralNetworkDetect` 推理待实机（P3.3 遗留）。

**下一步**：~~P4 Agent 功能移植（Go 1.25 + maa-framework-go/v4；17 动作 + 2 识别，清单见第 9 节）~~ → ✅ **已完成**，验收见第 15 节。

---

## 15. P4 Agent 功能移植验收（2026-10-27）

完整报告见 `docs/P4-agent功能移植.md`，此处只留结论。

| 验收项 | 结果 |
|---|---|
| P4.1 框架 | ✅ `MaaAgentSession` 封装 agent 子进程生命周期：拉起 → TCP 握手 → BindResource → 退出回收；接入 `MaaYysEngine.ConnectAsync`（非 Null 控制器自动拉起，best-effort 降级不拖垮主流程） |
| P4.2/4.3 动作（17） | ✅ 全部注册；`RandomWait` 真实动作经 pipeline 跑通（状态 Succeeded），其余按入参 schema 实现，游戏 UI 类动作标注「待真机校准」 |
| P4.4 识别（2） | ✅ `TaskCounterRecognition` / `CoBattleTargetRecognition` 注册并可用；计数/协战目标逻辑结构完整 |
| 端到端验证 | ✅ `probe agent`：agent 连接成功，17 动作 + 2 识别清单齐全，RandomWait 执行成功 |
| 测试 / 构建 | ✅ 173/173（新增 3 个 agent 集成测试）；全解决方案 0 警告 0 错误 |

**工具链坑（已解决）**：v4 模块要求 Go 1.25，本机 `environment/go` 是 1.24.12；但 `environment/gopath/pkg/mod/golang.org/toolchain@v0.0.1-go1.25.0` 已在本地，
只需 `GOTOOLCHAIN=go1.25.0 GOMODCACHE=.../environment/gopath/pkg/mod` 即可离线构建。`go.mod` 保持 `go 1.25.0`。

**P4 遗留（需真机/原工程源码）**：MXU 的 `agent/` 只含 `agent.exe` 无源码，游戏交互类动作（SwitchSoul/BattleTeam/AutoBattle/ChallengeDungeonBoss/AutoFoster/ReStartGame/QuestionMatcher/Guess）
的点击坐标与流程顺序按 param schema 重建，需接阴阳师窗口后微调；`NeuralNetworkDetect` 推理同理待实机。
`TaskCounterRecognition` 原始为 OCR 识别，本次以调用计数近似，待真机替换为 ROI 内数字识别。

**下一步**：P5 核心功能实现（任务队列/选项系统/执行引擎/定时/日志）——5.1、5.2 已完成，见第 16 节。

**M3.5 Agent 能力就绪里程碑达成**：19 个自定义能力（17 动作 + 2 识别）经 agent 子进程注册并闭环验证 ✅（M4「功能就绪」仍定于 P5 末，见第 3 节）

---

## 16. P5.1 / P5.2 验收（2026-10-27）

完整报告见 `docs/P5.1-任务队列系统.md`、`docs/P5.2-选项系统.md`。

| 验收项 | 结果 |
|---|---|
| P5.1 队列模型 | ✅ `TaskSelection` 增加实例唯一 `Id` / `Alias` / `DisplayName`；`TaskConfiguration` 新增 `AddInstance` / `RemoveById` / `MoveById` / `EnsureIds`；`Validate()` 唯一键由 `Name` 改为 `Name#Id`，**允许同类多实例**；旧配置加载时自动补 Id（向后兼容） |
| P5.1 队列 UI | ✅ 任务页左栏升级为可拖拽 `ListBox`：拖拽重排、运行态色条（`TaskRunStatus`）、启用勾选、双击/右键内联重命名、同类 `×N` 角标、右键「从此处运行 / 重命名 / 上移下移 / 删除」、顶部「添加任务」下拉（可重复添加同类） |
| P5.2 全局选项 | ✅ `TaskConfiguration.GlobalOptions` 持久化；`InterfaceConfig` 校验 `setting[].option` / `global_option` 引用并暴露 `GlobalOptionKeys()`；`TaskOptions` 新增 4 参 `Compile`（先全局层、任务级覆盖）与 `CompileGlobal`，3 参 `Compile` 保持兼容 |
| P5.2 选项 UI | ✅ 右栏改为「任务参数 / 全局选项」双页；选项行模板抽为 `OptionRowTemplate` 复用；全局页按 setting 段分组、实时预览全局层贡献；无 setting 段时自动隐藏 |
| 测试 / 构建 | ✅ **190/190**（P5.1 新增 8 例 `TaskConfigurationTests`、P5.2 新增 9 例 `GlobalOptionsTests`）；全解决方案 0 警告 0 错误 |
| 资源校验 | ✅ 真实 `resource/interface.json` PI V2：**错误 0 · 警告 1**（Android adb 缺省，预期内）；`probe tasks` 29 任务渲染无回归（选项 94→96） |

**顺带修复**：`.gitignore` 中 `config/` 与 `src/**/config/` 误伤源码目录 `src/MaaYys.Engine/Config/`（Windows git 大小写不敏感），导致 P3.4/P5.1 的 12 个配置源文件从未入库；已改为只忽略仓库根 `/config/`。

**P5 遗留（已由 P5.3 兑现）**：`RunFromHere` 原为占位提示、`SetStatus` 未被运行时驱动；P5.3 已把编译结果真正投递 `MaaTaskerPostTask` 并回写运行态（见第 17 节）。

---

## 17. P5.3 任务执行引擎验收（2026-10-27）

完整报告见 `docs/P5.3-任务执行引擎.md`。

| 验收项 | 结果 |
|---|---|
| 执行核心 | ✅ `QueueExecutor`：顺序投递、跳过禁用项、逐项状态回调（Pending/Running/终态）、汇总（成功/失败/跳过/下次下标） |
| 启动 / 停止 | ✅ 停止中止当前任务且不再跑后续项；`State` 必回 `Idle`，不会卡在 `Stopping` |
| 暂停 / 继续 | ✅ 暂停=中止并记住下标，继续从该任务重跑（框架无冻结原语，与 MAA 行为一致） |
| 停滞超时 | ✅ 心跳取**任一框架事件**（节点级事件也算进展），超时中止并判失败且不重试 |
| 失败重试 | ✅ `MaxRetries` + `RetryDelayMs`；耗尽后按 `StopOnFailure` 决定是否停队列 |
| 时长上限 | ✅ `MaxDurationSeconds` 到点中止（有进展也算），与停滞超时同属 fatal |
| 投递契约 | ✅ `ExecutionPlan` 把队列项编译成 `(entry, pipeline_override)`，任务级 + 全局选项合并，未知任务降级为默认 pipeline |
| 界面接线 | ✅ 运行控制条（开始 / 暂停·继续 / 停止 + 状态文案）；`RunFromHere` 真实调度；色条随运行态实时变化；实例切换自动重建宿主 |
| 测试 / 构建 | ✅ **209/209**（新增 19：执行器 12 + 执行计划 5 + 真引擎集成 2）；0 警告 0 错误 |

**设计取舍**：停滞超时与超时长判失败后**不重试**（重试必然复现，只会拖长卡死）；普通失败才走重试。策略参数暂走默认值（60s 停滞、不重试、失败即停），暴露到设置页归 P6.2。

**下一步**：~~P5.4 定时任务（8 组定时器 + 周计划 + 强制定时启动 + 开机自启）~~ → **已完成，验收见第 18 节**（开机自启移交 P6.1）。

---

## 18. P5.4 定时任务验收（2026-10-27）

完整报告见 `docs/P5.4-定时任务.md`。

| 验收项 | 结果 |
|---|---|
| 定时器 | ✅ 条数不限（流程表的「8 组」是 MAA 的预置槽位数，不是能力上限）；每条 = 时 + 分 + 配置组 + 启用 + 备注 |
| 周计划 | ✅ 7 位掩码（位索引 = `DayOfWeek` 数值），界面一~日 7 个勾选框 + 摘要（每天 / 工作日 / 周末 / 一、三） |
| 到点准确性 | ✅ `TimerScheduler` 20 秒轮询；同一分钟内多次轮询只触发一次（内存字典 + `last_fired` 双保险）；**重启不补跑** |
| 强制定时启动 | ✅ 到点若已有队列：`force_start` 真 → 先停（最多等 30s 到空闲）再启；假 → 跳过并记录原因 |
| 执行入口统一 | ✅ 新增 `Services/QueueRunService` 挂在 workspace：任务页与定时器共用同一执行器，杜绝两个队列同时投递 |
| 持久化 | ✅ `config/maayys.json` 顶层 `timers`（跨组共享）；改名改绑 / 删组清理 / 非法条目跳过 / 重复 Id 去重均有用例 |
| 界面 | ✅ 「定时」页（导航第 3 项）：行内直接编辑、改动即落盘、试跑 / 立即检查 / 下次触发；空状态有引导；任务页无回归 |
| 开机自启 | ⚠️ **未做**，移交 P6.1 应用设置（需注册表 + 静默启动参数，与托盘/关闭行为一起做才完整） |
| 测试 / 构建 | ✅ **229/229**（新增 21：规则 8 + 调度 5 + 持久化 6 + 真引擎定时链路 1）；0 警告 0 错误 |

**设计取舍**：
1. 周计划位序直接用 `.NET DayOfWeek`（0=周日…6=周六），不引入 MAA 的 `0=周日/1=周一` 或 cron 的 `1=周一` 两套换算——少一层换算少一处坑。
2. `last_fired` 落盘是「重启不补跑」的唯一可靠手段；只靠内存会在程序重启后于同一分钟重复执行一次。
3. 执行入口从任务页提到 workspace：定时触发时任务页可能从未创建，若各自持有执行器，两个队列会同时向 Maafw 投递。

**下一步**：P5.5 日志系统（见下节）。

---

## 19. P5.5 日志系统验收（2026-10-27）

**结论：5.5 完成，P5 五个子任务全部收尾（5/5）。**

| 验收项 | 结果 |
|---|---|
| 分级展示 | ✅ 五个级别（Trace/Debug/Info/Warn/Error）+ 阈值过滤 + 彩色徽标 + 节点级事件开关（默认关，排查时开） |
| agent 日志合并 | ✅ `MaaAgentSession` 重定向 stdout/stderr（异步读）；`probe agent` 实机捕获 5 行：框架版本 / 注册能力数 / server started / `RandomWait: sleep 1s`，任务仍 `Succeeded` |
| 截图卡片 | ✅ 警告/错误自动附最近一帧缩略图（块平均缩放，`ThumbnailFactory`）；内联缩略图 + 点击看大图/另存 PNG；上限 300 张，超限只丢图不丢日志 |
| 导出 | ✅ 文本（表头 + 时间格式 + 详情 + `[截图 shot-000003.png]`）与 CSV（UTF-8 BOM）；截图写入同名 `_shots/` 目录；实测 4 条 → txt + PNG 均生成且 PNG 头有效 |
| 时间格式配置 | ✅ 4 个预设，存 `config/maayys.json` 的 `log` 段；非法格式回退默认并记 `LoadErrors` |
| 长跑不卡顿 | ✅ 结构保证：缓冲容量上界 2 万 + 追加 O(1)（头指针淘汰/批量压实）+ 界面 200ms 轮询增量 + 仅保留 2000 行 + ListBox 虚拟化回收 + 批量滚动一次 |
| 测试 / 构建 | ✅ **266/266**（新增 37）；0 警告 0 错误 |
| 界面 | ✅ 日志页（导航第 4 项）截图核对：列对齐、级别配色、截图卡片、空状态与有数据两态均正常；任务页/定时页无回归 |

**顺带修复**：暗色主题下 WPF 默认 `CheckBox` 模板不继承前景色，标签被画成黑字（像素级核对发现"只有白方块"）。已在两套主题各加一条隐式 `CheckBox` 样式，全应用所有勾选框受益。

**设计取舍**：
1. 缩略图在引擎里**只缩放不编码**（Engine 是 `net9.0`，无 WPF/System.Drawing），PNG 编码留给 GUI 的 `PngBitmapEncoder`；导出时按 `shot-xxxxxx.png` 命名约定对齐，文本与图片两侧无需互相依赖。
2. 截图来源是**最近一帧缓存**而不是现抓：框架回调线程里现抓图会在任务执行中重入控制器，风险远大于收益。
3. 界面**轮询而非订阅**：逐条事件在长跑里会变成每秒上千次 UI 调度；轮询 + 版本号在空闲时是零开销。
4. 日志条目不逐条通知，批量追加后只滚动一次——逐条 `ScrollIntoView` 会触发几十次布局。

**遗留**：日志无落盘（→ P6.2）、策略项未全暴露（→ P6.2）、~~日志面板待并入主界面~~ → **P6.1 已闭环**。

**下一步**：**P6.1 主界面布局**（左侧任务链 + 右侧「连接 / 截图 / 日志」三面板，可折叠；顶部开始/停止；任务运行状态总览）。

---

## 20. P6.1 主界面布局验收（2026-10-27）

> ⚠️ 本节记录的是**第一版**（左任务链 + 右三面板）。该版本已被第 21 节的 MAA 式四页签布局取代，
> `WorkbenchView` / `WorkbenchViewModel` / `PanelStateViewModel` 已删除；结论仍成立的部分是「任务链抽成共用控件」与三个顺带修掉的缺陷。

完整报告见 `docs/P6.1-主界面布局.md`，此处只留结论。

**布局**：导航第 1 项「首页」→「主界面」。左 340px 任务链，右侧「连接（Auto）/ 截图（3*）/ 日志（2*）」三面板，各自标题栏右侧有折叠箭头；折叠时 `PanelStateViewModel.RowHeight` 回落 `Auto`，行高只剩标题栏。顶部工具栏：标题 / 运行状态总览（右对齐、超长省略）/ 开始·暂停·停止 / 主题切换。默认窗口 1320×780，`MinWidth 1040`。

**单一数据源**：`MainViewModel.Tasks` 是唯一 `TasksViewModel`——主界面左栏、任务页、顶部三个运行按钮绑同一个对象；任务链 UI 抽成 `Controls/TaskQueueView`，两处共用一套拖拽 / 重命名 / 右键菜单代码。

**顺带修掉的缺陷**（都是"以前没人看得见、现在同屏暴露"）：
1. `AddTaskToQueue` / `RemoveInstance` 只刷命令可用态，不发 `TaskSummary` / `CanStartRun` → 列表已有 4 项而摘要写「0 项」、开始按钮不亮。`RaiseCommandStates()` 补发。
2. 连接全过程不写日志 → 日志面板在设备未启动时永远只有启动那行。`InstanceViewModel.LogConnect()` 在 7 个节点落日志（`Engine` 来源 / `connect` 分类）。
3. 禁用态的自定义主按钮仍是高亮蓝底，看着像可点 → `IsEnabled=False` 加 `Opacity 0.4`。

**验证**：`dotnet test` 266/266、`dotnet build` 0 警告 0 错误；无头截图逐张核对 —— 1280×720 空队列 / 1280×720 四任务 / 1040×640 最小窗口 / 折叠截图+日志 / 连接失败写日志 / 任务页回归，均无截断与重叠。

**新增调试参数**：`--size WxH`（指定窗口尺寸，验收条件本身要求 1280×720）、`--add-task 名称`（可重复，走与「添加任务」下拉同一条路）、`--collapse 连接|截图|日志`（可重复）。

**遗留**：面板折叠状态不持久化、面板顺序固定、多实例下顶部「开始」以任务页选中的配置组为准（→ 分别归 P6.2 设置项 / P6.4 打磨 / P7 多开深化）。

**下一步**：**P6.2 设置中心-系统类**（连接设置 / 启动设置含开机自启 / 运行设置，并暴露 P5.3·P5.4 写死的策略参数与日志落盘）。

---

## 21. P6.1 改版：对齐 MAA 四页签布局（2026-10-27）

完整报告见 `docs/P6.1-主界面布局.md` §8。

**起因**：第一版把连接/截图/日志都堆在主界面，与 MAA 的实际形态不一致。用户给出 MAA 界面截图作为目标，确认页签结构为
**一键长草 / 多实例 / 小工具 / 设置**，实时截图移到「多实例」下面。

**结构**：左侧 184px 导航整体删除 → 顶部页签行（文字 + 选中下划线）；实例标签条并入顶栏下拉。
一键长草 = 286px 任务卡（勾选 + 名称 + `×N` + 齿轮，左侧 3px 运行态色条；底部 ⊕/全选/清空）
\+ 完成后动作 + 整宽 `Link Start!`；右侧「以下选项为多任务共用」（实例 / 配置组 / 连接状态）
\+ 「常规设置 / 高级设置」二级页签（常规 = 当前任务参数，高级 = 全局选项 + `pipeline_override` 预览）。
定时 / 日志 / 设备探测收进小工具二级导航；设置页补「关于」（版本与三类清单路径）。

**删除**：`WorkbenchView`、`WorkbenchViewModel`、`PanelStateViewModel`。

**新增能力**：`SelectAllCommand` / `DeselectAllCommand`（清空 = 取消勾选，不删任务）/ `EditItemCommand`（齿轮定位）/
`AfterCompletionIndex`（无动作 / 退出程序 / 关机，关机二次确认，仅在 `ExecutionOutcome.Completed` 触发）；
`SelectedRightTab` 改成发通知属性。

**验证**：`dotnet test` 266/266、`dotnet build` 0 警告 0 错误；1280×760 四页签 + 1040×660 最小窗口 + 明暗双主题 +
空队列 + 连接失败共 8 张截图逐张核对，无截断/重叠。

**新增调试参数**：`--tab 名称`（按名字切页签）、`--right-tab N`、`--clear-queue`（会写配置）、`--light`。
**废除**：`--collapse`（面板已不存在）。

**遗留**：「完成后」动作未持久化（→ P6.2 运行设置）、顶栏实例下拉只管切换不管增删、小工具二级导航不支持分组。

**下一步**：**P6.2 设置中心-系统类**（连接 / 启动含开机自启 / 运行设置；暴露 P5.3·P5.4 写死的策略参数、日志落盘、完成后动作持久化）。
