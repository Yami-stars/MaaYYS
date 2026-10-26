package main

import (
	"encoding/json"
	"fmt"
	"math/rand"
	"time"

	maa "github.com/MaaXYZ/maa-framework-go/v4"
)

// randSrc 是 agent 内独立的随机数源，供并发的自定义动作/识别回调安全使用。
var randSrc = rand.New(rand.NewSource(time.Now().UnixNano()))

func logf(format string, a ...any) {
	fmt.Printf("[agent] "+format+"\n", a...)
}

// paramErr 记录参数解析失败并返回 false（自定义动作失败 → 走节点 on_error）。
func paramErr(name string, err error) bool {
	logf("ACTION %s: param error: %v", name, err)
	return false
}

// randRangeInt 返回 [min, max] 闭区间内的随机整数，min>max 时返回 min。
func randRangeInt(min, max int) int {
	if max <= min {
		return min
	}
	return min + randSrc.Intn(max-min+1)
}

// weightedPick 按权重随机选一个 key。所有权重 <=0 时返回 false。
func weightedPick(m map[string]int) (string, bool) {
	total := 0
	for _, w := range m {
		total += w
	}
	if total <= 0 {
		return "", false
	}
	r := randSrc.Intn(total)
	for k, w := range m {
		r -= w
		if r < 0 {
			return k, true
		}
	}
	// 理论不可达，map 遍历兜底
	for k := range m {
		return k, true
	}
	return "", false
}

// tap 在共享控制器上点击 (x,y)，best-effort：控制器不可用时返回 false。
func tap(ctx *maa.Context, x, y int32) bool {
	ctrl := controllerOf(ctx)
	if ctrl == nil {
		return false
	}
	job, err := ctrl.PostClick(x, y)
	if err != nil {
		logf("tap(%d,%d) post failed: %v", x, y, err)
		return false
	}
	return job.Wait().Success()
}

// swipe 在共享控制器上从 (x1,y1) 滑到 (x2,y2)，best-effort。
func swipe(ctx *maa.Context, x1, y1, x2, y2 int32, d time.Duration) bool {
	ctrl := controllerOf(ctx)
	if ctrl == nil {
		return false
	}
	job, err := ctrl.PostSwipe(x1, y1, x2, y2, d)
	if err != nil {
		logf("swipe post failed: %v", err)
		return false
	}
	return job.Wait().Success()
}

// controllerOf 从回调 context 取到共享控制器（agent 进程经 IPC 转发）。
func controllerOf(ctx *maa.Context) *maa.Controller {
	if ctx == nil {
		return nil
	}
	tasker := ctx.GetTasker()
	if tasker == nil {
		return nil
	}
	return tasker.GetController()
}

// resolution 取当前分辨率，best-effort。
func resolution(ctx *maa.Context) (int32, int32, bool) {
	ctrl := controllerOf(ctx)
	if ctrl == nil {
		return 0, 0, false
	}
	w, h, err := ctrl.GetResolution()
	if err != nil {
		return 0, 0, false
	}
	return w, h, true
}

// unmarshalParam 解析 custom_action_param / custom_recognition_param（JSON 字符串）。
func unmarshalParam[T any](raw string) (T, error) {
	var out T
	if raw == "" {
		return out, nil
	}
	err := json.Unmarshal([]byte(raw), &out)
	return out, err
}
