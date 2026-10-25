# MaaYss

基于 [MaaFramework](https://github.com/MaaXYZ/MaaFramework) 的《阴阳师》Windows 官方桌面端自动化助手。

- 功能设计基准：MaaYYs-win32 MXU v3.13.6
- UI / 设置体系基准：MAA v6.20.1
- 当前阶段：**P1 技术预研与原型验证**（详见 `docs/` 下两份规划文档）

> 本项目仅用于学习与交流，请自行承担使用风险。

## 目录结构

```
MaaYYS/
├── MaaYss.sln              # 解决方案
├── src/
│   ├── MaaYss.Engine/      # 引擎封装（MaaFramework.dll P/Invoke + Tasker/Resource/Controller）
│   └── MaaYss.P1Probe/     # P1 链路验证控制台（P2 起替换为 WPF GUI）
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
dotnet build MaaYss.sln

# Go agent（GOPROXY 用国内镜像可显著提速）
$env:GOPATH  = "D:\WorkBuddy-Work\MaaYYS\environment\gopath"
$env:GOCACHE = "D:\WorkBuddy-Work\MaaYYS\environment\gocache"
$env:GOPROXY = "https://goproxy.cn,direct"
& "D:\WorkBuddy-Work\MaaYYS\environment\go\bin\go.exe" build -o agent.exe ./agent
```

运行各阶段验证：

```powershell
dotnet run --project src/MaaYss.P1Probe -- engine   # P1.2
dotnet run --project src/MaaYss.P1Probe -- pi       # P1.3
dotnet run --project src/MaaYss.P1Probe -- win32    # P1.4（--click/--resize/--screencap-method 等）
dotnet run --project src/MaaYss.P1Probe -- agent    # P1.5（自动拉起 Go agent 子进程）
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
| 1.6 | 技术方案评审 | 待开始 |

## P1.5 验证结果（2026-10-26）

`dotnet run --project src/MaaYss.P1Probe -- agent`：探针（C#，AgentClient）自动拉起 Go agent 子进程并完成一次完整调用。

- 握手：`AgentClient.CreateTcp(7788)` → `identifier=7788` → 启动 `agent.exe 7788 --libdir <maafw>` → `Connected=True Alive=True`
- 注册清单回读：`custom_action=[MaaYssProbeAction]`、`custom_recognition=[MaaYssProbeRecognition]`
- pipeline 调用：`recognition` 收到 `param={"probe":"p15"}`、`roi=[0 0 720 720]`、图像非空；`action` 收到 `param={"hello":"maayss"}`、`box=[0 0 10 10]`（即 recognition 返回的框）
- 结果：`PostTask -> Succeeded`，agent 侧痕迹文件校验通过，子进程 `exit=0` 优雅退出

关键结论（P4 架构依据）：
1. **Go 绑定必须走 v4 模块路径**：`github.com/MaaXYZ/maa-framework-go/v4`（MaaYYs 的 agent 亦用 `v4.0.0-beta.12`，已用 `go version -m` 对其 exe 实证）。
   v1.x 线（最高 v1.7.0）**不含 AgentServer 绑定**，无法做 agent。
2. **v4.0.0 要求 Go 1.25.0**；本地 SDK 为 1.24.12，但 Go 的 toolchain 机制会自动下载 go1.25.0，无需手工升级 SDK。
3. **调用顺序有硬约束**：`BindResource` 必须在 `Connect` 之前（否则框架报 `resource is not bound` 拒绝握手）。
4. **销毁顺序有硬约束**：`Resource` 必须在 `AgentClient.Disconnect` 之后销毁，否则 client 内部持有悬垂指针，Disconnect 触发 `0xC0000005`。
5. `MaaStringListBufferAt` 返回的是借用的 `MaaStringBuffer*`，需再经 `MaaStringBufferGet` 取 `char*`（直接当字符串读会得到乱码）。

## P1.2 验证结果（2026-10-25，v5.15.0-beta.1 回归通过）

`dotnet run --project src/MaaYss.P1Probe -- engine` 输出：MaaFramework v5.15.0-beta.1 → 资源 PostBundle 成功（hash `d2d7ae0ec19aaf9`）→ PostTask → `Tasker.Task.Starting` → `Tasker.Task.Succeeded`。

实现要点：
- 引擎层：`src/MaaYss.Engine/Native/`（P/Invoke + DllImportResolver 定位 `third_party/maafw`）、`MaaResource` / `MaaTasker` / `MaaNullController` 托管封装（SafeHandle + UTF-8 marshalling）
- 自定义控制器 `MaaNullController`：官方运行库未分发 `MaaDbgControlUnit.dll`，改用 `MaaCustomControllerCreate` 扩展点实现 1x1 静态截图控制器（无额外 DLL 依赖）
- 注意：v5.13 起 API 为 Tasker 中心化（无 MaaInstance）；`pipeline_override` 必须传合法 JSON 字符串（官方绑定默认 `{}`，传 null 会触发框架 UB）

## P1.3 验证结果（2026-10-25）

`dotnet run --project src/MaaYss.P1Probe -- pi`（默认解析 MaaYYs 的 interface.json，可传路径覆盖）：
- 8 个渠道服资源 bundle 全部解析，加载链正确（base ← 渠道层，后者覆盖前者）
- 29 个 import 任务入口合并成功（29/29 文件解析通过），94 个 option 池（同名先到先得）
- 交叉校验通过：option / group / resource / controller 引用全部有效，资源路径全部存在
- 识别出 3 个未被 import 引用的孤儿任务文件（活动-小超鬼王 / 自动业原火 / 限时活动）与 1 个孤儿资源目录（test/）

关键结论（均经官方源码实证）：
- import 合并语义对齐 MaaPiCli `Parser.cpp`：task/preset 追加；option 为 `unordered_map::insert` 语义——**同名先到先得，顶层 interface.json 优先**
- 官方硬校验三项：`interface_version == 2`、控制器类型合法、任务引用的 option 必须存在于合并池
- **流程表修正**：原「32 个任务入口」为按 tasks/ 目录文件数的误计数；按 PI V2 语义只有 import 引用的 29 个构成任务入口（3 个孤儿文件不加载），流程表全文已同步修正

## P1.4 验证结果（2026-10-25 ~ 26）

`dotnet run --project src/MaaYss.P1Probe -- win32`（游戏窗口实测运行中）：

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
