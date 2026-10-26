# P4 Agent 功能移植验收报告

> 日期：2026-10-27 ｜ 阶段：P4（Agent 功能移植）｜ 里程碑：M4 Agent 能力就绪
> 关联文档：`docs/MaaYys-开发流程表.md` §15 ｜ 输入清单：流程表第 9 节（17 动作 + 2 识别）

## 1. 范围与结论

把原 MaaYYs 的 **17 个自定义动作 + 2 个自定义识别** 用 Go `maa-framework-go/v4` 的
`MaaAgentServer` 实现为独立子进程，由 C# 引擎经 `MaaAgentClient` 拉起、握手、绑定资源。
pipeline（见第 9 节汇总）里实际引用了其中 16 个动作名 + 计划要求的 `RandomSwipe`，共 17；
整个资源树里 `RandomSwipe` 零引用，但作为计划内 17 个之一仍实现并注册（注册后无害）。

| 验收项 | 结果 |
|---|---|
| 19 个能力全部注册 | ✅ `probe agent` 列出 17 动作 + 2 识别，数量/名称完全匹配 |
| 真实动作执行闭环 | ✅ `RandomWait` 经 pipeline 在 agent 进程内跑通（状态 Succeeded） |
| 引擎接入 | ✅ `MaaYysEngine.ConnectAsync` 连成功后自动拉起 agent，Dispose 时回收 |
| 测试 / 构建 | ✅ 173/173（+3 agent 集成测试）；全解决方案 0 警告 0 错误 |

## 2. 工具链坑（已解决）

`maa-framework-go/v4` 要求 **Go 1.25**，但环境默认 `environment/go` 是 1.24.12。
本地其实已有 1.25 工具链缓存（`environment/gopath/pkg/mod/golang.org/toolchain@v0.0.1-go1.25.0.windows-amd64`），
只是 `GOMODCACHE` 默认指向 `C:\Users\ASUS\go\pkg\mod` 导致 go 去联网下载失败（Bad Gateway）。

**构建命令**（离线可用）：

```bash
cd agent
GOTOOLCHAIN=go1.25.0 \
GOMODCACHE="D:/WorkBuddy-Work/MaaYYS/environment/gopath/pkg/mod" \
GOPATH="D:/WorkBuddy-Work/MaaYYS/environment/gopath" \
../environment/go/bin/go.exe build -o agent.exe .
```

`go.mod` 保持 `go 1.25.0` 不变。

## 3. P4.1 框架：`MaaAgentSession`

新文件 `src/MaaYys.Engine/Agent/MaaAgentSession.cs`。职责：

- **启动**：`TryStart(resource, options?)` 定位 `agent/agent.exe`（interface.json 的 `agent.child_exec`
  或向上查找）→ 找空闲 TCP 端口 → 以 `<port> --libdir <maafw>` 拉起子进程 →
  `MaaAgentClient.CreateTcp(port)` → `BindResource(resource)` → `Connect()`（超时 20s）。
- **libdir 同源**：默认从当前进程的 `MaaFramework.NativeLibraryPath`（由 `NativeLoader` 解析到
  `third_party/maafw`）取目录，保证 agent 与 GUI 用同一套 MaaFramework 运行库。
- **降级策略**：任意步骤失败返回 `null`，调用方（引擎）降级为「无 agent」，绝不拖垮主流程。
- **回收**：`Dispose()` 先 `Disconnect()`（让 agent 的 `AgentServerJoin` 返回）再杀子进程，
  避免 client 持有已释放 resource 指针导致的 `0xC0000005`。

**引擎接入**（`MaaYysEngine.cs`）：`ConnectAsync` 在 `_tasker.BindController` 之后、对非 `Null`
控制器调用 `MaaAgentSession.TryStart(_resource)`，成功打印 `agent 已连接：17 动作 / 2 识别 @port`，
失败仅告警；`Dispose` 中 `_agentSession?.Dispose()`。

> 已知限制：切换渠道导致 `_resource` 被替换（`LoadResourceChainAsync`）后，旧 agent 会话绑定的
> 是旧 resource，自定义能力会失效。P6 资源热切换时需重新拉起 agent（已在字段注释登记）。

## 4. P4.2 / P4.3 十七个动作

入参 schema 全部来自 `resource/` 下 pipeline 实际引用（Python 解析去重）。
标注 **[逻辑已实现]** 的无需真机即可正确工作；标注 **[待真机校准]** 的逻辑结构完整、参数解析正确、
不会崩溃，但点击坐标/流程顺序需接阴阳师窗口后微调（MXU 的 `agent/` 只含 `agent.exe` 无源码）。

| # | 动作 | 入参（去重后） | 状态 |
|---|---|---|---|
| 1 | RandomWait | `{min,max}` | [逻辑已实现] 随机休眠 |
| 2 | ClearHitCount | `{node_name}` / `{node_names[]}` | [逻辑已实现] `ctx.ClearHitCount` |
| 3 | TimeCheck | `{start,end,mode,in/out,date_rule{type,value[]}}` | [逻辑已实现] 时间窗/星期门控 |
| 4 | RandomTask | `{"任务名":权重,...}` | [逻辑已实现] 加权随机 `RunTask` |
| 5 | HumanTouch | `{ROI_1[x,y,w,h],单/双击权重,短/长等待}` | [逻辑已实现] ROI 内拟人点击 |
| 6 | RandomTouch | 无 | [逻辑已实现] 随机点击 |
| 7 | RandomSwipe | 无 | [逻辑已实现] 随机滑动 |
| 8 | SwitchSoul | `{group_name,team_name}` | [待真机校准] 切御魂/阵容 |
| 9 | BattleTeam | `{位置,分组名称,队伍名称}` | [待真机校准] 设出战队伍 |
| 10 | AutoBattle | `{group_name,team_name}` | [待真机校准] 自动战斗 |
| 11 | ChallengeDungeonBoss | `{count,isReward}` | [待真机校准] 挑战副本 Boss |
| 12 | AutoFoster | `{FosterTarget}` | [待真机校准] 自动育成 |
| 13 | ReStartGame | 无 | [待真机校准] 重启游戏（包名待确认） |
| 14 | BonusToggleAction | `{drop_soul?,exp_100%?,exp_50%?,gold_100%?,gold_50%?}` | [待真机校准] 切加成开关 |
| 15 | RepeatChallengeNTimes | `{expected_number,start_repeat}` | [待真机校准] 次数门控 |
| 16 | QuestionMatcher | `{sync,upload}` | [待真机校准] 题目匹配（OCR+查库） |
| 17 | Guess | 无 | [待真机校准] 答题猜测 |

> 实现统一在 `agent/actions.go` + `agent/util.go`（随机、加权、控制器点击/滑动、param 解析 helpers）。
> 游戏 UI 交互通过 `ctx.GetTasker().GetController()` 拿到共享控制器（agent 进程经 IPC 转发主进程控制器），
> 用 `PostClick` / `PostSwipe` 落操作。

## 5. P4.4 两个识别

| 识别 | 入参 | 状态 |
|---|---|---|
| CoBattleTargetRecognition | `{action:add\|check}` | [逻辑已实现] 进程内协战目标登记表（add 记录 / check 校验） |
| TaskCounterRecognition | `{target_count}` | [待真机校准] 以调用计数近似「已完成轮数」，达 `target_count` 即不匹配以终止循环；原始实现为 OCR 识别 ROI 内数字，待真机替换 |

> 实现见 `agent/recognitions.go`。两个识别均返回 `Rect` 命中框 + JSON `detail`，供下游节点分支。

## 6. 验证

**端到端（`probe agent`，退出码 0）**：

```
[3] 自定义动作 (17): [RepeatChallengeNTimes, RandomWait, ... AutoBattle]
    自定义识别 (2): [TaskCounterRecognition, CoBattleTargetRecognition]
    动作注册校验: 通过
    识别注册校验: 通过
[4] 投递引用 RandomWait 的 pipeline（验证动作执行）：
    PostTask -> id=200000001 状态=Succeeded
=== P4 验证完成：agent 链路闭环（注册 + 执行）通过 ===
```

**测试**：新增 `tests/MaaYys.Engine.Tests/MaaAgentSessionTests.cs`（3 个），覆盖「19 能力全注册 /
真实动作执行 / Dispose 回收端口」。总 173/173 通过。

**构建**：全解决方案 `dotnet build` 0 警告 0 错误。

## 7. 遗留与下一步

- **需真机/原源码**：游戏交互类动作（#8–#17 中待校准项）与 `TaskCounterRecognition` 的 OCR 识别，
  需接阴阳师窗口（或拿到 MXU 的 Go 源）后微调坐标与流程。当前全部为「结构正确、参数解析正确、不崩溃」的重建版。
- **NN 推理**：`NeuralNetworkDetect` 模型推理仍待实机（P3.3 遗留）。
- **下一步 P5**：核心功能实现（任务队列 / 选项系统 / 执行引擎 / 定时 / 日志）。
