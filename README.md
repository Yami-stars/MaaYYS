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
│   ├── MaaYss.Engine/      # 引擎封装（MaaFramework.dll P/Invoke + MaaInstance/Tasker）
│   └── MaaYss.P1Probe/     # P1 链路验证控制台（P2 起替换为 WPF GUI）
├── agent/                  # Go agent（自定义 action/recognition，maa-framework-go）
├── third_party/maafw/      # MaaFramework 运行库副本（不入库，来源见下）
├── environment/            # Go SDK 与 GOPATH（不入库）
└── docs/                   # 规划文档（知识获取报告、开发流程表）
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

`third_party/maafw/` 复制自 MaaYYs-win32 v3.13.6 发布包（`D:\YYS\MaaYYs-win32-win-x86_64-v3.13.6-2608192116-MXU\maafw`），
内置 MaaFramework **v5.13.0-beta.2**。该目录已加入 `.gitignore`，换环境时需重新复制。

## P1 进度

| # | 任务 | 状态 |
|---|---|---|
| 1.1 | 开发环境搭建与脚手架 | ✅ 完成 |
| 1.2 | 引擎集成验证 | ✅ 完成 |
| 1.3 | PI V2 协议解析验证 | 待开始 |
| 1.4 | Win32 控制链路验证 | 待开始 |
| 1.5 | Agent 链路验证 | 待开始 |
| 1.6 | 技术方案评审 | 待开始 |

## P1.2 验证结果（2026-10-25）

`dotnet run --project src/MaaYss.P1Probe -- engine` 输出：MaaFramework v5.13.0-beta.2 → 资源 PostBundle 成功（hash `d2d7ae0ec19aaf9`）→ PostTask → `Tasker.Task.Starting` → `Tasker.Task.Succeeded`。

实现要点：
- 引擎层：`src/MaaYss.Engine/Native/`（P/Invoke + DllImportResolver 定位 `third_party/maafw`）、`MaaResource` / `MaaTasker` / `MaaNullController` 托管封装（SafeHandle + UTF-8 marshalling）
- 自定义控制器 `MaaNullController`：MaaYYs 运行库未分发 `MaaDbgControlUnit.dll`，改用 `MaaCustomControllerCreate` 扩展点实现 1x1 静态截图控制器（无额外 DLL 依赖）
- 注意：v5.13 起 API 为 Tasker 中心化（无 MaaInstance）；`pipeline_override` 必须传合法 JSON 字符串（官方绑定默认 `{}`，传 null 会触发框架 UB）
