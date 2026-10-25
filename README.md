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

前置：.NET SDK 9+、Go 1.24+（本项目 Go 位于 `environment/go`，无需系统安装）。

```powershell
# C#
dotnet build MaaYss.sln

# Go agent
$env:GOPATH = "D:\WorkBuddy-Work\MaaYYS\environment\gopath"
$env:PATH = "D:\WorkBuddy-Work\MaaYYS\environment\go\bin;$env:PATH"
go build ./agent/...
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
| 1.4 | Win32 控制链路验证 | ✅ 完成（点击验证待显式执行，策略 A） |
| 1.5 | Agent 链路验证 | 待开始 |
| 1.6 | 技术方案评审 | 待开始 |

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

## P1.4 验证结果（2026-10-25）

`dotnet run --project src/MaaYss.P1Probe -- win32`（默认解析 MaaYYs 的 interface.json，游戏窗口实测运行中）：

- 窗口枚举命中 1 个：`hWnd=0x1805EC class=[Win32Window] title=[阴阳师-网易游戏]`（window_regex 放宽为「阴阳师」后命中当前窗口标题）
- 连接：`PostConnection -> Succeeded`，`Connected=True`，`Uuid=00000000001805EC`
- Info JSON 证实方法生效：`screencap_method=16`（PrintWindow）、`mouse/keyboard_method=2`（SendMessage），与 interface.json 配置一致
- 截图：`1280x720`（短边 720 缩放生效）3 通道 BGR，平均亮度 107.7/255，非黑像素 64.1% —— 后台截图抓到了真实庭院画面（不抢焦点）
- 原始分辨率 `1431x805`，缩放关系正确

实现要点：
- `Native/`：Toolkit 窗口枚举 API（MaaToolkit.dll，NativeLoader 已扩展解析）+ Win32 控制器全套 Post/Wait API + ImageBuffer/StringBuffer 封装（`MaaHandles.cs`：SafeHandle + BMP 自编码）
- 托管封装：`MaaDesktopWindows`（正则过滤语义对齐 `boost::regex_search` 子串匹配）、`MaaWin32Controller`（连接/截图/点击/滑动/SetOption）
- 点击验证按用户选定策略 A：默认仅验证到截图，点击/滑动须 `--click x,y` / `--swipe ...` 显式执行（执行前打印坐标）

验证截图（debug/win32-screenshot-*.bmp，已 gitignore）。
