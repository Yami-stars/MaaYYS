// MaaYys agent 子进程：MaaAgentServer 宿主。
//
// P4 Agent 功能移植：注册阴阳师自动脚本所需的 17 个自定义动作 + 2 个自定义识别，
// 供 C# 侧（MaaAgentClient）连接并调用，完成 agent 链路闭环。
//
// 用法：
//   agent.exe <identifier> [--libdir <MaaFramework DLL 目录>]
//
// identifier 由 MaaAgentClient 生成（TCP 模式下为端口号字符串），
// 由父进程（GUI / 探针）传入。
package main

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"

	maa "github.com/MaaXYZ/maa-framework-go/v4"
)

// 自定义动作名（必须与 resource/ 下 pipeline 的 custom_action 引用完全一致）。
const (
	nameRandomWait            = "RandomWait"
	nameSwitchSoul            = "SwitchSoul"
	nameClearHitCount         = "ClearHitCount"
	nameRepeatChallengeNTimes = "RepeatChallengeNTimes"
	nameBonusToggleAction     = "BonusToggleAction"
	nameTimeCheck             = "TimeCheck"
	nameRandomSwipe           = "RandomSwipe"
	nameReStartGame           = "ReStartGame"
	nameHumanTouch            = "HumanTouch"
	nameChallengeDungeonBoss  = "ChallengeDungeonBoss"
	nameBattleTeam            = "BattleTeam"
	nameRandomTouch           = "RandomTouch"
	nameRandomTask            = "RandomTask"
	nameQuestionMatcher       = "QuestionMatcher"
	nameGuess                 = "Guess"
	nameAutoFoster            = "AutoFoster"
	nameAutoBattle            = "AutoBattle"
)

// 自定义识别名（必须与 pipeline 的 custom_recognition 引用完全一致）。
const (
	nameCoBattleTarget = "CoBattleTargetRecognition"
	nameTaskCounter    = "TaskCounterRecognition"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "agent error:", err)
		os.Exit(1)
	}
}

func run() (err error) {
	identifier, libDir, err := parseArgs(os.Args[1:])
	if err != nil {
		return err
	}

	opts := make([]maa.InitOption, 0, 1)
	if libDir != "" {
		fmt.Println("[agent] libdir:", libDir)
		opts = append(opts, maa.WithLibDir(libDir))
	}

	if err := maa.Init(opts...); err != nil {
		return fmt.Errorf("init MAA: %w", err)
	}
	defer func() {
		if releaseErr := maa.Release(); releaseErr != nil {
			err = errors.Join(err, fmt.Errorf("release MAA: %w", releaseErr))
		}
	}()

	fmt.Printf("[agent] MaaFramework %s | identifier=%s\n", maa.Version(), identifier)

	// 即使注册或启动失败也要关闭 socket。
	defer maa.AgentServerShutDown()

	// ── 注册 17 个自定义动作 ──
	type actionReg struct {
		name string
		fn   maa.CustomActionFunc
	}
	actions := []actionReg{
		{nameRandomWait, actRandomWait},
		{nameSwitchSoul, actSwitchSoul},
		{nameClearHitCount, actClearHitCount},
		{nameRepeatChallengeNTimes, actRepeatChallengeNTimes},
		{nameBonusToggleAction, actBonusToggleAction},
		{nameTimeCheck, actTimeCheck},
		{nameRandomSwipe, actRandomSwipe},
		{nameReStartGame, actReStartGame},
		{nameHumanTouch, actHumanTouch},
		{nameChallengeDungeonBoss, actChallengeDungeonBoss},
		{nameBattleTeam, actBattleTeam},
		{nameRandomTouch, actRandomTouch},
		{nameRandomTask, actRandomTask},
		{nameQuestionMatcher, actQuestionMatcher},
		{nameGuess, actGuess},
		{nameAutoFoster, actAutoFoster},
		{nameAutoBattle, actAutoBattle},
	}
	for _, a := range actions {
		if regErr := maa.AgentServerRegisterCustomAction(a.name, a.fn); regErr != nil {
			return fmt.Errorf("register custom action %s: %w", a.name, regErr)
		}
	}

	// ── 注册 2 个自定义识别 ──
	type recoReg struct {
		name string
		fn   maa.CustomRecognitionFunc
	}
	recos := []recoReg{
		{nameCoBattleTarget, recCoBattleTarget},
		{nameTaskCounter, recTaskCounter},
	}
	for _, r := range recos {
		if regErr := maa.AgentServerRegisterCustomRecognition(r.name, r.fn); regErr != nil {
			return fmt.Errorf("register custom recognition %s: %w", r.name, regErr)
		}
	}

	fmt.Printf("[agent] registered: %d actions + %d recognitions\n", len(actions), len(recos))

	if err := maa.AgentServerStartUp(identifier); err != nil {
		return fmt.Errorf("start agent server: %w", err)
	}

	fmt.Println("[agent] server started, waiting for client")

	// 客户端断开后 Join 返回。
	maa.AgentServerJoin()
	fmt.Println("[agent] server stopped")
	return nil
}

func parseArgs(args []string) (identifier string, libDir string, err error) {
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--libdir":
			if i+1 >= len(args) {
				return "", "", errors.New("--libdir 缺少参数")
			}
			i++
			libDir = args[i]
		default:
			if identifier != "" {
				return "", "", fmt.Errorf("多余的位置参数: %s", args[i])
			}
			identifier = args[i]
		}
	}

	if identifier == "" {
		return "", "", fmt.Errorf("usage: %s <identifier> [--libdir <dll dir>]", filepath.Base(os.Args[0]))
	}
	return identifier, libDir, nil
}
