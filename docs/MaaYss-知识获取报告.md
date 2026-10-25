# MaaYss 项目知识获取报告

> 版本：v1.0 ｜ 日期：2026-10-25 ｜ 状态：知识获取阶段完成（未启动开发）
> 项目代号：**MaaYss**（阴阳师自动化助手）

---

## 1. 项目定位

| 项 | 内容 |
|---|---|
| 目标游戏 | 《阴阳师》Windows 官方桌面端（MuMu 引擎封装，非模拟器） |
| 技术底座 | MaaFramework（MaaXYZ/MaaFramework，C++20 图像识别自动化框架） |
| 功能设计基准 | MaaYYs-win32 MXU v3.13.6（`D:\YYS\MaaYYs-...-MXU\mxu.exe`） |
| UI / 设置体系基准 | MAA v6.20.1（`D:\MAA-v5.16.5-win-x64\MAA.exe`，目录名为 v5.16.5，实际已就地升级至 v6.20.1） |
| 目标形态 | 一款独立完整的桌面应用：自有 GUI + 自有设置体系 + 移植并扩展的阴阳师任务资源 |

**核心结论**：不重复造轮子。引擎层直接复用 MaaFramework 运行库；任务资源从 MaaYYs 移植并重构；GUI 与设置体系参照 MAA 自研。

---

## 2. 参考对象 A：MaaYYs-win32 MXU v3.13.6（功能设计基准）

### 2.1 基本信息

- 上游：`github.com/TanyaShue/MaaYYs`（模拟器/ADB 版）；本机版本为 `b1n4he/MaaYYs-win32` 分支，专攻 Windows 桌面端
- 组成：**MXU 图形界面（v1.3.0）+ MaaFramework 运行库 + Go 语言 agent + 资源包**
- 本地实测目录结构：

```
MaaYYs-win32-...-MXU/
├── mxu.exe              # MXU v1.3.0（misteo，Tauri+React+TS 通用 GUI）
├── maafw/               # MaaFramework 运行库（MaaFramework.dll / MaaWin32ControlUnit.dll / onnxruntime / opencv / fastdeploy_ppocr）
├── agent/agent.exe      # Go 编译产物（interface.json 写死 .\agent\agent.exe）
├── interface.json       # PI V2 项目入口（version: v3.13.6）
├── tasks/               # 32 个任务定义 JSON（含 option 选项体系）
├── resource_pack/       # 资源包：base + official/taptap/bilibili/huawei/tencent/OPPO/vivo/test
│   ├── pipeline/        # 51 个 pipeline 文件（战斗/日常/活动/通用/启动）
│   ├── image/           # 710 张模板图（1280×720 基准）
│   └── model/           # OCR（det/rec.onnx）+ 7 个检测模型
├── assets/  config/  cache/  debug/
```

### 2.2 功能清单（32 个任务入口，三大分组）

| 分组 | 任务 |
|---|---|
| 日常任务 | 日常奖励领取、结界奖励领取、式神委派、寮三十捐材料、投喂宠物、网易大神签到、自动购物、式神图鉴分享 |
| 独立副本 | 自动御魂、自动御灵、自动业原火、自动秘闻、自动麒麟、困28、组队副本、英杰试炼、契灵探查、肝绘卷 |
| 限时任务 | 结界突破、自动寮突破、自动道馆突破、自动斗技、自动地鬼、自动逢魔、自动阴界之门、自动悬赏、活动爬塔、花车、限时活动、活动-小超鬼王 |
| 启停通用 | 打开游戏、关闭游戏 |

**任务能力矩阵**（pipeline 实测统计）：

| 识别类型 | 节点数 | 动作类型 | 节点数 |
|---|---|---|---|
| OCR | 794 | Click | 715 |
| TemplateMatch | 324 | Custom（agent） | 98 |
| DirectHit | 266 | Swipe | 34 |
| FeatureMatch | 94 | StartApp / StopApp / StopTask | 6 |
| NeuralNetworkDetect | 24 | | |
| ColorMatch | 8 | | |
| Custom（agent） | 12 | | |

### 2.3 Agent API 面（Go 实现，需移植的核心）

**自定义动作（19 个，按引用次数）**：
`RandomWait`(24) `SwitchSoul`(20) `ClearHitCount`(15) `RepeatChallengeNTimes`(9) `BonusToggleAction`(8) `TimeCheck`(5) `RandomSwipe`(5) `ReStartGame`(2) `HumanTouch`(2) `ChallengeDungeonBoss`(2) `BattleTeam`(2) `RandomTouch` `RandomTask` `QuestionMatcher` `MyCustomAction` `Guess` `AutoFoster` `AutoBattle`

**自定义识别（2 个）**：`TaskCounterRecognition`(10)、`CoBattleTargetRecognition`(2)

功能语义：随机等待/随机滑动/拟人点击（防检测）、换御魂、清零计数、N 次重复挑战、加成 Buff 开关、时间校验、重启游戏、挑战 BOSS、队伍预设切换（OCR 队名）、答题匹配、猜拳、自动养成、自动战斗。

### 2.4 控制器与连接方案（Win32 核心）

- **Win32 桌面版**：窗口类名 `Win32Window`；截图 `PrintWindow`（可抓硬件加速画面）；键鼠 `SendMessage`（后台不抢焦点）；`display_short_side: 720`
- **Android（Adb）**：兼容模拟器
- **PlayCover（macOS）**：iOS 版（uuid `com.netease.onmyoji`）
- 已知调优点：鼠标/键盘方式可在 `SendMessage` / `PostMessage` / `SendMessageWithWindowPos` 间切换

**窗口标题匹配规则（2026-10-25 已修改）**：

| 项 | 原版（上游默认） | 当前（本次修改后） |
|---|---|---|
| `window_regex` | `阴阳师-MuMu模拟器专版` | `阴阳师` |
| 匹配效果 | 仅命中标题完整为「阴阳师-MuMu模拟器专版」的窗口 | **标题包含「阴阳师」即命中**（如桌面端改版后的新标题、带区服/版本后缀的标题等） |

匹配语义说明（MaaFramework 源码 `source/MaaPiCli/CLI/interactor.cpp` 实证）：

```cpp
if (boost::regex_search(rt_config.class_name, *class_regex) && boost::regex_search(rt_config.window_name, *window_regex))
```

即 `class_regex` / `window_regex` 均为**子串正则搜索（regex_search，非全文匹配）**，因此 `阴阳师` 可命中任意标题含该词的窗口；多个窗口同时命中时，客户端会列出候选供选择，不会误连。类名仍限定 `Win32Window`（MuMu 引擎窗口），命中范围可控。修改方式：编辑根目录 `interface.json` 中 Win32 控制器的 `window_regex`，**重启 mxu.exe 后生效**（无需改 mxu.exe 二进制本身，标题正则由 interface.json 驱动，调试日志 `搜索桌面窗口, windowRegex: ...` 可验证）。

### 2.5 渠道服资源分层机制

`base`（公共资源）+ 渠道差异层（official / taptap / bilibili / huawei / tencent / OPPO / vivo），通过 `interface.json` 的 `resource[].path` 数组顺序加载，**后加载 bundle 覆盖同名节点**。渠道差异目前主要是登录界面启动流程。

### 2.6 ONNX 模型资产

| 模型 | 用途 |
|---|---|
| `model/ocr/det.onnx` + `rec.onnx` + `keys.txt` | PaddleOCR 文字识别（约 21MB） |
| `Courtyard-v5.0.onnx` / `Courtyard-v6.0.onnx` | 庭院皮肤版本检测（多套庭院皮肤适配的关键） |
| `BarrierCard.onnx` / `Barrier_v1.onnx` | 结界突破/道馆卡牌检测 |
| `special-icon-v0.1.0.onnx` / `v0.1.1.onnx` | 特殊图标检测 |

### 2.7 可借鉴点 / 待改进点

| 可借鉴（功能设计直接继承） | 待改进（MaaYss 的机会点） |
|---|---|
| 32 任务全量能力与分组 | MXU 为通用壳，任务设置深度不如 MAA 任务链 |
| Win32 后台控制方案 | 无多配置/多实例管理体系（MXU instances 较弱） |
| 渠道服分层资源机制 | 无外部通知、远程控制、数据统计 |
| Go agent 19 动作 + 2 识别 | 无自动更新（依赖 Mirror 酱/GitHub 手动） |
| 拟人化随机操作（防检测） | 无任务周计划/定时启动策略（MXU 定时任务较弱） |

---

## 3. 参考对象 B：MAA v6.20.1（UI / 设置体系基准）

### 3.1 基本信息

- `github.com/MaaAssistantArknights/MaaAssistantArknights`，明日方舟自动化，与 MaaFramework 同源生态（MaaCore 即 MaaFramework 前身）
- 本机实测：`MAA.exe` v6.20.1 + `MaaCore.dll` + `MaaWpfGui`（WPF/.NET）+ `Python/` 嵌入式 agent + `MAA.Updater.exe`

### 3.2 五层架构（MaaYss 可对标）

| 层 | 组成 | MaaYss 对应 |
|---|---|---|
| 入口层 | MaaWpfGui（WPF，Windows）、MaaMacGui、maa-cli | 自研 WPF GUI |
| 绑定层 | Python / Rust / Golang / Java / Dart | C#（GUI 内）+ Go（agent） |
| 核心层 | MaaCore：Vision（识别）/ Task（调度）/ Controller（控制）/ Config（资源） | 直接用 MaaFramework.dll |
| 资源层 | resource/tasks JSON、template 模板图、onnx 模型 | 从 MaaYYs 移植 |
| 设备层 | Windows 桌面进程、安卓模拟器/真机 | 阴阳师桌面端 + 模拟器 |

### 3.3 配置体系（`gui.new.json`，强类型 JSON，v6.15 起迁移）

MAA 的设置中心共 **11 大类**，实测键位如下（MaaYss 设置体系按此对标）：

| 设置类别 | 代表配置项（实测） |
|---|---|
| 连接设置 | `ConnectSettings`：自动检测、Adb 路径/地址、雷电/MuMu 多开索引、**Win32Extra**（ScreencapMethod=FramePool / MouseMethod=PostMessageWithCursorPos / KeyboardMethod=SendMessage / MuteWhileRunning 静音）、TouchMode、KillAdbOnExit |
| 启动设置 | `StartUpSettings`：RunDirectly、启动模拟器/PC 客户端路径与参数、等待秒数、连接失败重启 |
| 运行设置 | `RuntimeSettings`：ClientType 区服、StartGame、PreRunScript/PostRunScript 前后置脚本、BlockSleep 防休眠、RunDurationLimit 运行时长上限 |
| 通知设置 | `Notification`：卡住超时（StallTimeoutMinutes=25）、提醒间隔 |
| 外部通知 | `ExternalNotification`：完成/出错/卡住/定时前/日志数/关键词白名单触发，多通道投递 |
| 远程控制 | `RemoteControl`：TaskEndpointUri、ReportStatusUri、轮询间隔、身份标识 |
| 第三方数据 | `ThirdParty`：企鹅物流/一图流汇报开关与 Token |
| 定时启动 | `Timers`：8 组定时器（Hour/Min + 绑定配置）、ForceScheduledStart 强制定时、ScheduledWakeUp 唤醒 |
| 更新设置 | `Update`：UpdateSource、MirrorChyan CDK、代理、自动下载/安装、夜间版开关 |
| 公告 | `AnnouncementInfo`：不再提醒 |
| 界面设置 | `Gui`：DarkMode 暗色、Localization 语言、UseTray/MinimizeToTray 托盘、TransitionSpeed 过渡动画、LogItemDateFormat 日志格式、InverseClearMode 反选清除 |

多配置管理：`Configurations.<名称>` 命名配置组 + `Current` 指针 + `ConfigVersion`，支持配置导入导出与迁移。

### 3.4 任务链（TaskQueue）设计 —— MaaYss 任务系统直接对标

- 任务列表**拖拽排序**、同一任务类型可**添加多个实例并重命名**（右键齿轮）
- 每个任务独立启用/禁用，运行中复选框**颜色区分状态**（跳过/完成/运行中/失败）
- **周计划**：按星期几决定任务是否执行（如仅周一打剿灭）
- **「从此处运行」**：从指定任务开始执行，失败后无需从头重跑
- 任务设置齿轮面板：每个任务的参数在右侧滑出面板编辑
- 完成后动作、任务超时等全局策略

### 3.5 界面与交互特性

- 暗色模式、多语言（本地化）、托盘最小化、窗口位置记忆
- 页签/任务链切换**方向性过渡动画**、设置页平滑滚动
- 日志缩略图（截图卡片）、任务执行实时状态
- 新手引导（Guide.StepIndex 分步指引）
- 干员/物品**头像图片显示**（识别结果可视化）

---

## 4. MaaFramework 核心机制（开发必备）

### 4.1 四大核心模块

| 模块 | 职责 |
|---|---|
| Tasker | 任务调度中枢：post_task 异步驱动 PipelineTask，管理生命周期/回调 |
| Resource | 资源管理：pipeline JSON 解析、模板图、ONNX 模型；多 bundle 顺序加载、同名覆盖 |
| Controller | 控制层：Adb / Win32 / PlayCover / macOS / Linux / Gamepad / Custom / Replay 多控制单元抽象 |
| Vision | 识别层：TemplateMatch、OCR、FeatureMatch、ColorMatch、NeuralNetworkDetect（YOLOv8/v11 ONNX）、Custom |

对外通过稳定 **C API** 暴露，绑定：**Python / Node.js / C# / Go / Rust**（官方均有）。

### 4.2 PI V2 协议（interface.json 关键字段，MaaYss 的入口契约）

```
interface_version / name / label / version / description / icon / welcome / license
contact / github / telemetry(Sentry)
controller[]   # 控制器声明：type=Adb/Win32/PlayCover，win32{class_regex,window_regex,mouse,keyboard,screencap,display_short_side}
resource[]     # 资源配置：name/label/path[](bundle 顺序，后者覆盖前者)
agent          # child_exec（agent 子进程路径）+ child_args
group[]        # 任务分组（daily/standalone/promotion）
task[]         # 任务声明（可空，用 import 导入）
option{}       # 顶层选项定义（select/switch/input + pipeline_override）
import[]       # 导入 tasks/*.json
setting[]      # MXU 扩展：把 option 收纳进「设置-任务设置」分组
```

### 4.3 Pipeline 节点协议（资源层核心语法）

```jsonc
{
  "节点名": {
    "recognition": "OCR|TemplateMatch|FeatureMatch|ColorMatch|NeuralNetworkDetect|Custom|And|Or|JustReturn",
    "action": "Click|Swipe|StartApp|StopApp|StopTask|Custom|...",
    "next": ["下一节点", "[JumpBack]回跳节点", "[Anchor]锚点"],
    "on_error": [...],
    "roi": [x,y,w,h], "target": [...],
    "pre_delay": 2000, "post_delay": 2000,
    "max_hit": 5,                      // 未命中重试上限
    "template": ["路径.png"],          // 模板图
    "custom_action": "RandomWait",     // agent 动作
    "custom_action_param": {...},      // agent 参数
    "custom_recognition": "...", "custom_recognition_param": {...}
  }
}
```

### 4.4 选项系统（任务参数化的关键机制）

- `option.type`：`select`（枚举 cases）/ `switch`（Yes/No）/ `input`（文本框）
- 每个 case 携带 **`pipeline_override`**：运行时在内存中改写目标节点的 `expected`/`enabled`/`custom_action_param` 等字段
- `input` 支持 `pipeline_type: string/int`，模板占位符 `{挑战次数}` 注入 `custom_action_param`
- 选项可嵌套（case 内挂子 option，如「御魂挑战目标 → 大蛇挑战层数」）

### 4.5 Agent 协议（跨语言自定义逻辑）

- 独立子进程，通过 MaaAgentClient/Server 与引擎通信（socket/TCP）
- 注册 `@AgentServer.custom_action("名")` / `custom_recognition`，实现 `run(context, argv)` 接口
- Go 绑定 `maa-framework-go`；MaaYYs 的 agent 即 Go 实现（19 动作 + 2 识别）

### 4.6 控制单元矩阵（Win32 详细参数）

| 能力 | 可选值 |
|---|---|
| 截图 screencap | `PrintWindow`（支持 PW_RENDERFULLCONTENT，抓硬件加速）/ `FramePool` / `BackBuffer` / `GDI` |
| 鼠标 mouse | `SendMessage` / `PostMessage` / `SendMessageWithWindowPos` / `PostMessageWithCursorPos` / `Seize` |
| 键盘 keyboard | `SendMessage` / `PostMessage` / `Seize` |
| 窗口定位 | `class_regex` + `window_regex`（**子串正则搜索 regex_search，非全文匹配**；写 `阴阳师` 即命中标题含该词的所有窗口，多命中时客户端列出候选供选择） |

---

## 5. 技术选型结论

| 层 | 选型 | 理由 / 来源 |
|---|---|---|
| 核心引擎 | **MaaFramework 运行库**（MaaFramework.dll + MaaWin32ControlUnit + MaaAdbControlUnit + onnxruntime + opencv + fastdeploy_ppocr） | 与两款参考同源；C++ 成熟稳定；避免重写识别/调度 |
| GUI | **WPF（.NET 8）+ CommunityToolkit.Mvvm** | 对标 MAA MaaWpfGui 的界面与设置体系；Windows 桌面端优先 |
| 引擎绑定 | **Maa.Framework（C# NuGet）** 或直接 P/Invoke `MaaFramework.dll` C API | C# 绑定官方维护；C API 最稳 |
| Agent | **Go 1.24 + maa-framework-go**，独立子进程 | MaaYYs agent 即 Go，源码可移植，改造成本最低 |
| 资源层 | **PI V2 pipeline JSON + 模板图 + ONNX**（从 MaaYYs 移植重构） | 32 任务能力直接继承；JSON 即代码，可热更新 |
| 配置系统 | **强类型 JSON 配置**（对标 MAA gui.new.json，多配置 + Current 指针） | 可迁移、可读、可版本化 |
| 构建发布 | GitHub Actions 多架构打包 + git-cliff + Mirror 酱（可选） | 对标 MaaYYs CI |
| 更新 | 自研轻量 Updater（对标 MAA.Updater.exe） | 桌面端用户免手动更新 |

**架构全景**：

```
┌─────────────────────────────────────────────┐
│ MaaYss GUI (WPF/.NET 8)                     │
│  任务链 │ 设置中心 │ 实时截图 │ 日志 │ 定时   │
├─────────────────────────────────────────────┤
│ 引擎封装层 (MaaController/Instance/Tasker)    │
├─────────────────────────────────────────────┤
│ MaaFramework.dll (MaaCore: Vision/Task/      │
│  Controller/Config)                          │
├──────────────┬──────────────┬───────────────┤
│ Win32Control │ AdbControl   │ (PlayCover 可选)│
│ PrintWindow  │ 模拟器        │                │
│ SendMessage  │              │                │
├──────────────┴──────────────┴───────────────┤
│ Agent 子进程 (Go: 19 自定义动作 + 2 识别)      │
├─────────────────────────────────────────────┤
│ 资源层: pipeline JSON + 710 模板图 + ONNX     │
│  (base + 8 渠道服分层, 32 tasks 定义)          │
└─────────────────────────────────────────────┘
```

---

## 6. 风险与注意事项

1. **游戏版本漂移**：桌面端更新会导致按钮/图标变化，庭院皮肤多套（v5/v6 已建模检测）→ 资源层必须支持**免编译热更新**（pipeline/模板图/模型与程序分离，可独立发资源包）
2. **窗口定位失效**：窗口标题/类名随版本变化 → `window_regex` 做成**配置项**而非硬编码。**已处置（2026-10-25）**：本地 MaaYYs 安装的 `window_regex` 已由 `阴阳师-MuMu模拟器专版` 放宽为 `阴阳师`（子串匹配，桌面端改标题不影响连接）；MaaYss 设计上应在「连接设置」中暴露窗口标题输入框（对标 MAA Win32Extra），用户可自助修改
3. **后台点击无效**：不同桌面端版本对 SendMessage 响应不同 → 鼠标/键盘方式做成设置项（对标 MAA Win32Extra）
4. **OCR 队名识别失败**：队伍预设名称避免复杂符号/空格（MaaYYs 已知问题）→ 提供名称规范提示 + 模糊匹配容错
5. **合规与声明**：仅个人学习交流用途，README/LICENSE 需明确风险自负声明；Sentry 遥测需用户可关闭
6. **多开与资源竞争**：多实例同时运行时的配置隔离、端口冲突（对标 MXU instances + MAA 多配置）
7. **反作弊**：保留拟人化随机操作（HumanTouch/RandomWait/RandomSwipe），控制请求频率

---

## 修订记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-10-25 | 初版：参考对象分析 + MaaFramework 机制 + 技术选型 |
| v1.1 | 2026-10-25 | 修改本地 MaaYYs 安装的 Win32 窗口标题匹配规则：`interface.json` 的 `window_regex` 由 `阴阳师-MuMu模拟器专版` 改为 `阴阳师`（2.4 节同步更新，附 regex_search 源码实证）；第 6 节风险 2 标记为已处置 |

*本报告基于 2026-10-25 对本地两款软件的实测探测 + MaaFramework/MXU/MaaYYs/MAA 开源仓库调研编写，供项目立项与开发流程排期使用。*
