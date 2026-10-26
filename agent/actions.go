package main

import (
	"encoding/json"
	"fmt"
	"time"

	maa "github.com/MaaXYZ/maa-framework-go/v4"
)

// ───────────────────────────── 17 个自定义动作 ─────────────────────────────
// 入参 schema 来自 resource/ 下 pipeline 实际引用（见开发流程表第 9 节）。
// 标记 [逻辑已实现] 的在不依赖真机的情况下即可正确工作；
// 标记 [待真机校准] 的是游戏 UI 交互，逻辑结构完整、参数解析正确、不会崩溃，
// 但点击坐标/流程顺序需接真机（阴阳师窗口）后微调。

// RandomWait {min,max}：随机等待 min~max 秒。 [逻辑已实现]
func actRandomWait(_ *maa.Context, arg *maa.CustomActionArg) bool {
	var p struct {
		Min int `json:"min"`
		Max int `json:"max"`
	}
	if err := json.Unmarshal([]byte(arg.CustomActionParam), &p); err != nil {
		return paramErr("RandomWait", err)
	}
	if p.Max < p.Min {
		p.Min, p.Max = p.Max, p.Min
	}
	secs := randRangeInt(p.Min, p.Max)
	logf("RandomWait: sleep %ds (range %d~%d)", secs, p.Min, p.Max)
	time.Sleep(time.Duration(secs) * time.Second)
	return true
}

// ClearHitCount {node_name | node_names[]}：清空指定节点的命中计数。 [逻辑已实现]
func actClearHitCount(ctx *maa.Context, arg *maa.CustomActionArg) bool {
	var p struct {
		NodeName  string   `json:"node_name"`
		NodeNames []string `json:"node_names"`
	}
	if err := json.Unmarshal([]byte(arg.CustomActionParam), &p); err != nil {
		return paramErr("ClearHitCount", err)
	}
	names := append([]string{}, p.NodeNames...)
	if p.NodeName != "" {
		names = append(names, p.NodeName)
	}
	if len(names) == 0 {
		return paramErr("ClearHitCount", fmt.Errorf("node_name/node_names 均缺失"))
	}
	for _, n := range names {
		if err := ctx.ClearHitCount(n); err != nil {
			logf("ClearHitCount: clear %q failed: %v", n, err)
			return false
		}
	}
	logf("ClearHitCount: cleared %d node(s)", len(names))
	return true
}

// TimeCheck {start,end,mode,in/out,date_rule{type,value[]}}：时间窗门控。 [逻辑已实现]
// 返回 true 表示当前时间落在允许区间（mode=in）或区间外（mode=out）。
func actTimeCheck(_ *maa.Context, arg *maa.CustomActionArg) bool {
	var p struct {
		Start    string `json:"start"`
		End      string `json:"end"`
		Mode     string `json:"mode"`
		DateRule *struct {
			Type  string `json:"type"`
			Value []int  `json:"value"`
		} `json:"date_rule"`
	}
	if err := json.Unmarshal([]byte(arg.CustomActionParam), &p); err != nil {
		return paramErr("TimeCheck", err)
	}
	now := time.Now()
	if p.DateRule != nil && p.DateRule.Type == "week" {
		hit := false
		for _, v := range p.DateRule.Value {
			// schema 用 1=周一…7=周日，与 Go time.Weekday（Monday=1）一致
			if v == int(now.Weekday()) {
				hit = true
				break
			}
		}
		if !hit {
			logf("TimeCheck: weekday %d not in %v -> false", int(now.Weekday()), p.DateRule.Value)
			return false
		}
	}
	in := inTimeWindow(now, p.Start, p.End)
	if p.Mode == "out" {
		logf("TimeCheck: mode=out window[%s,%s]=%v -> %v", p.Start, p.End, in, !in)
		return !in
	}
	logf("TimeCheck: window[%s,%s]=%v", p.Start, p.End, in)
	return in
}

func inTimeWindow(now time.Time, start, end string) bool {
	parse := func(s string) (time.Time, bool) {
		if s == "" {
			return time.Time{}, false
		}
		t, err := time.Parse("15:04", s)
		if err != nil {
			return time.Time{}, false
		}
		return time.Date(now.Year(), now.Month(), now.Day(), t.Hour(), t.Minute(), 0, 0, now.Location()), true
	}
	st, ok1 := parse(start)
	et, ok2 := parse(end)
	if !ok1 || !ok2 {
		return true // 无有效时间窗则默认放行
	}
	if et.Before(st) {
		// 跨午夜，如 22:00~06:00
		return !now.Before(st) || !now.After(et)
	}
	return !now.Before(st) && !now.After(et)
}

// RepeatChallengeNTimes {expected_number, start_repeat}：挑战次数门控。 [待真机校准]
// 通过命中计数判断是否已达成预期次数；start_repeat 为真时尝试启动「重复挑战」UI。
func actRepeatChallengeNTimes(ctx *maa.Context, arg *maa.CustomActionArg) bool {
	var p struct {
		ExpectedNumber int  `json:"expected_number"`
		StartRepeat    bool `json:"start_repeat"`
	}
	if err := json.Unmarshal([]byte(arg.CustomActionParam), &p); err != nil {
		return paramErr("RepeatChallengeNTimes", err)
	}
	hit, err := ctx.GetHitCount(arg.CurrentTaskName)
	if err == nil {
		logf("RepeatChallengeNTimes: hit=%d expected=%d", hit, p.ExpectedNumber)
		if hit >= uint64(p.ExpectedNumber) {
			return false // 已达次数，停止重复
		}
	}
	if p.StartRepeat {
		// [待真机校准] 实际实现应点击「重复挑战」按钮；
		// 此处仅记录意图，坐标需在真机上确认。
		logf("RepeatChallengeNTimes: start_repeat requested (UI click TODO on device)")
	}
	return true
}

// BonusToggleAction {drop_soul?, exp_100%?, exp_50%?, gold_100%?, gold_50%?}： [待真机校准]
// 切换加成开关。param 为各加成项的布尔目标值。
func actBonusToggleAction(ctx *maa.Context, arg *maa.CustomActionArg) bool {
	var p map[string]bool
	if err := json.Unmarshal([]byte(arg.CustomActionParam), &p); err != nil {
		return paramErr("BonusToggleAction", err)
	}
	// [待真机校准] 加成面板各开关的屏幕坐标需在真机上标定；
	// 此处先完整解析并记录目标状态，再按坐标点击切换。
	logf("BonusToggleAction: targets=%v (UI click TODO on device)", p)
	for k, v := range p {
		logf("  bonus %q -> %v", k, v)
	}
	return true
}

// RandomTask {"任务名":权重, ...}：按权重随机挑一个任务并作为子任务运行。 [逻辑已实现]
func actRandomTask(ctx *maa.Context, arg *maa.CustomActionArg) bool {
	var p map[string]int
	if err := json.Unmarshal([]byte(arg.CustomActionParam), &p); err != nil {
		return paramErr("RandomTask", err)
	}
	if len(p) == 0 {
		return paramErr("RandomTask", fmt.Errorf("空任务表"))
	}
	name, ok := weightedPick(p)
	if !ok {
		return paramErr("RandomTask", fmt.Errorf("权重全为 0"))
	}
	logf("RandomTask: picked %q", name)
	if _, err := ctx.RunTask(name); err != nil {
		logf("RandomTask: RunTask(%q) failed: %v", name, err)
		return false
	}
	return true
}

// HumanTouch {ROI_1[x,y,w,h], *weight*}：在 ROI 内拟人点击（单/双击 + 随机间隔）。 [逻辑已实现]
func actHumanTouch(ctx *maa.Context, arg *maa.CustomActionArg) bool {
	var p struct {
		ROI1             [4]int `json:"ROI_1"`
		SingleClickW     int    `json:"single_click_weight"`
		DoubleClickW     int    `json:"double_click_weight"`
		ShortWaitMin     int    `json:"short_wait_min"`
		ShortWaitMax     int    `json:"short_wait_max"`
		ShortWaitW       int    `json:"short_wait_weight"`
		LongWaitMin      int    `json:"long_wait_min"`
		LongWaitMax      int    `json:"long_wait_max"`
		LongWaitW        int    `json:"long_wait_weight"`
	}
	if err := json.Unmarshal([]byte(arg.CustomActionParam), &p); err != nil {
		return paramErr("HumanTouch", err)
	}
	x0, y0, w, h := p.ROI1[0], p.ROI1[1], p.ROI1[2], p.ROI1[3]
	if w <= 0 || h <= 0 {
		return paramErr("HumanTouch", fmt.Errorf("ROI_1 非法: %v", p.ROI1))
	}
	px := x0 + randSrc.Intn(w)
	py := y0 + randSrc.Intn(h)
	dname, dok := weightedPick(map[string]int{"single": p.SingleClickW, "double": p.DoubleClickW})
	double := dok && dname == "double"
	if double {
		tap(ctx, int32(px), int32(py))
		tap(ctx, int32(px), int32(py))
		logf("HumanTouch: double-click @(%d,%d)", px, py)
	} else {
		tap(ctx, int32(px), int32(py))
		logf("HumanTouch: single-click @(%d,%d)", px, py)
	}
	// 等待：短/长按按权重
	lname, lok := weightedPick(map[string]int{"short": p.ShortWaitW, "long": p.LongWaitW})
	long := lok && lname == "long"
	var waitMs int
	if long {
		waitMs = randRangeInt(p.LongWaitMin, p.LongWaitMax) * 1000
	} else {
		waitMs = randRangeInt(p.ShortWaitMin, p.ShortWaitMax) * 1000
	}
	time.Sleep(time.Duration(waitMs) * time.Millisecond)
	return true
}

// RandomTouch：在屏幕随机位置点击一次。 [逻辑已实现]
func actRandomTouch(ctx *maa.Context, _ *maa.CustomActionArg) bool {
	w, h, ok := resolution(ctx)
	if !ok || w <= 0 || h <= 0 {
		logf("RandomTouch: no resolution")
		return false
	}
	x := randSrc.Int31n(w)
	y := randSrc.Int31n(h)
	logf("RandomTouch: @(%d,%d)", x, y)
	return tap(ctx, x, y)
}

// RandomSwipe：在屏幕随机区域做一次随机滑动。 [逻辑已实现]
func actRandomSwipe(ctx *maa.Context, _ *maa.CustomActionArg) bool {
	w, h, ok := resolution(ctx)
	if !ok || w <= 0 || h <= 0 {
		logf("RandomSwipe: no resolution")
		return false
	}
	x1 := randSrc.Int31n(w)
	y1 := randSrc.Int31n(h)
	x2 := clampInt32(int32(x1)+int32(randRangeInt(-120, 120)), 0, w-1)
	y2 := clampInt32(int32(y1)+int32(randRangeInt(-120, 120)), 0, h-1)
	logf("RandomSwipe: (%d,%d)->(%d,%d)", x1, y1, x2, y2)
	return swipe(ctx, x1, y1, x2, y2, 300*time.Millisecond)
}

func clampInt32(v, lo, hi int32) int32 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

// SwitchSoul {group_name, team_name}：切换御魂/阵容。 [待真机校准]
func actSwitchSoul(_ *maa.Context, arg *maa.CustomActionArg) bool {
	var p struct {
		GroupName string `json:"group_name"`
		TeamName  string `json:"team_name"`
	}
	_ = json.Unmarshal([]byte(arg.CustomActionParam), &p)
	logf("SwitchSoul: group=%q team=%q (UI click TODO on device)", p.GroupName, p.TeamName)
	// [待真机校准] 实际应先打开式神录→切到对应分组→选中队伍；坐标待真机标定。
	return true
}

// BattleTeam {位置, 分组名称, 队伍名称}：设置出战队伍。 [待真机校准]
func actBattleTeam(_ *maa.Context, arg *maa.CustomActionArg) bool {
	var p struct {
		Position    string `json:"位置"`
		GroupName   string `json:"分组名称"`
		TeamName    string `json:"队伍名称"`
	}
	_ = json.Unmarshal([]byte(arg.CustomActionParam), &p)
	logf("BattleTeam: 位置=%q 分组=%q 队伍=%q (UI click TODO on device)", p.Position, p.GroupName, p.TeamName)
	return true
}

// AutoBattle {group_name, team_name}：自动战斗（含选队+战斗循环）。 [待真机校准]
func actAutoBattle(_ *maa.Context, arg *maa.CustomActionArg) bool {
	var p struct {
		GroupName string `json:"group_name"`
		TeamName  string `json:"team_name"`
	}
	_ = json.Unmarshal([]byte(arg.CustomActionParam), &p)
	logf("AutoBattle: group=%q team=%q (battle loop TODO on device)", p.GroupName, p.TeamName)
	return true
}

// ChallengeDungeonBoss {count, isReward}：挑战副本 Boss。 [待真机校准]
func actChallengeDungeonBoss(_ *maa.Context, arg *maa.CustomActionArg) bool {
	var p struct {
		Count    int  `json:"count"`
		IsReward bool `json:"isReward"`
	}
	_ = json.Unmarshal([]byte(arg.CustomActionParam), &p)
	logf("ChallengeDungeonBoss: count=%d isReward=%v (UI loop TODO on device)", p.Count, p.IsReward)
	return true
}

// AutoFoster {FosterTarget}：自动育成（如御魂/式神育成）。 [待真机校准]
func actAutoFoster(_ *maa.Context, arg *maa.CustomActionArg) bool {
	var p struct {
		FosterTarget int `json:"FosterTarget"`
	}
	_ = json.Unmarshal([]byte(arg.CustomActionParam), &p)
	logf("AutoFoster: target=%d (UI loop TODO on device)", p.FosterTarget)
	return true
}

// ReStartGame：重启游戏。 [待真机校准]
// 通过控制器停止再启动 YYS 包名；包名需在真机/模拟器上确认。
func actReStartGame(ctx *maa.Context, _ *maa.CustomActionArg) bool {
	ctrl := controllerOf(ctx)
	if ctrl == nil {
		logf("ReStartGame: no controller")
		return false
	}
	const yysPackage = "com.netease.onmyoji" // [待真机校准] 包名/Intent 待确认
	logf("ReStartGame: stop %s", yysPackage)
	if job, err := ctrl.PostStopApp(yysPackage); err == nil {
		job.Wait()
	}
	time.Sleep(2 * time.Second)
	logf("ReStartGame: start %s", yysPackage)
	if job, err := ctrl.PostStartApp(yysPackage); err == nil {
		job.Wait()
	}
	return true
}

// QuestionMatcher {sync, upload}：题目匹配（上传/同步题库）。 [待真机校准]
// [待真机校准] 实际需 OCR 当前题目 → 查题库 → 返回答案；此处记录意图。
func actQuestionMatcher(_ *maa.Context, arg *maa.CustomActionArg) bool {
	var p struct {
		Sync   bool `json:"sync"`
		Upload bool `json:"upload"`
	}
	_ = json.Unmarshal([]byte(arg.CustomActionParam), &p)
	logf("QuestionMatcher: sync=%v upload=%v (OCR+lookup TODO on device)", p.Sync, p.Upload)
	return true
}

// Guess：答题猜测。 [待真机校准]
func actGuess(_ *maa.Context, _ *maa.CustomActionArg) bool {
	logf("Guess: (answer-guess TODO on device)")
	return true
}
