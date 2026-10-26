package main

import (
	"encoding/json"
	"fmt"
	"sync"

	maa "github.com/MaaXYZ/maa-framework-go/v4"
)

// ───────────────────────────── 2 个自定义识别 ─────────────────────────────
// 标记同 actions.go：[逻辑已实现] 表示不依赖真机即可正确工作；[待真机校准] 表示
// 结构完整、不会崩溃，但识别精度/坐标需接真机后微调。

// coBattleRegistry 是协战目标的进程内登记表（add/check 共享）。
var (
	coBattleMu     sync.Mutex
	coBattleTarget = false // 是否已记录协战目标
)

// CoBattleTargetRecognition {action: add|check}：协战目标登记/校验。 [逻辑已实现]
//   - add：把当前 ROI 记为协战目标
//   - check：校验协战目标是否已就位（已 add 则匹配）
func recCoBattleTarget(_ *maa.Context, arg *maa.CustomRecognitionArg) (*maa.CustomRecognitionResult, bool) {
	var p struct {
		Action string `json:"action"`
	}
	if err := json.Unmarshal([]byte(arg.CustomRecognitionParam), &p); err != nil {
		logf("RECO CoBattleTarget: param error: %v", err)
		return nil, false
	}
	box := arg.Roi
	switch p.Action {
	case "add":
		coBattleMu.Lock()
		coBattleTarget = true
		coBattleMu.Unlock()
		logf("RECO CoBattleTarget: added target roi=%v", box)
		return &maa.CustomRecognitionResult{
			Box:    box,
			Detail: `{"action":"add","ok":true}`,
		}, true
	case "check":
		coBattleMu.Lock()
		ok := coBattleTarget
		coBattleMu.Unlock()
		logf("RECO CoBattleTarget: check -> %v", ok)
		return &maa.CustomRecognitionResult{
			Box:    box,
			Detail: fmt.Sprintf(`{"action":"check","ok":%v}`, ok),
		}, ok
	default:
		logf("RECO CoBattleTarget: unknown action %q", p.Action)
		return nil, false
	}
}

// taskCounter 按任务名记录识别被调用的次数（用于推断「已完成的轮数」）。
var (
	taskCounterMu sync.Mutex
	taskCounter   = map[string]int{}
)

// TaskCounterRecognition {target_count}：次数识别。 [待真机校准]
// 记录本任务被识别到的次数，达到 target_count 时视为达成（matched=false 以终止循环）。
// [待真机校准] 原始实现应为 OCR 识别画面上的剩余/已完成次数；此处以调用计数近似，
// 需在真机上替换为对 ROI 内数字的正确识别。
func recTaskCounter(_ *maa.Context, arg *maa.CustomRecognitionArg) (*maa.CustomRecognitionResult, bool) {
	var p struct {
		TargetCount int `json:"target_count"`
	}
	if err := json.Unmarshal([]byte(arg.CustomRecognitionParam), &p); err != nil {
		logf("RECO TaskCounter: param error: %v", err)
		return nil, false
	}
	taskCounterMu.Lock()
	taskCounter[arg.CurrentTaskName]++
	count := taskCounter[arg.CurrentTaskName]
	taskCounterMu.Unlock()

	// 计数达到目标 → 不再匹配（让 pipeline 走 on_error/停止分支）
	matched := count < p.TargetCount
	logf("RECO TaskCounter: task=%s count=%d target=%d matched=%v",
		arg.CurrentTaskName, count, p.TargetCount, matched)
	return &maa.CustomRecognitionResult{
		Box:    arg.Roi,
		Detail: fmt.Sprintf(`{"count":%d,"target":%d,"matched":%v}`, count, p.TargetCount, matched),
	}, matched
}
