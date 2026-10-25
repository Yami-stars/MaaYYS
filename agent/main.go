// MaaYss agent 子进程：MaaAgentServer 宿主。
//
// P1.5 用途：注册一个测试用 custom_action 与 custom_recognition，
// 供 C# 侧（MaaAgentClient）连接并调用，验证 agent 链路闭环。
//
// 用法：
//   agent.exe <identifier> [--libdir <MaaFramework DLL 目录>]
//
// identifier 由 MaaAgentClient 生成（纯数字 1-65535 表示 TCP 端口），
// 由父进程（GUI / 探针）传入。
package main

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"

	"github.com/MaaXYZ/maa-framework-go/v4"
)

const (
	// 探针侧的 pipeline 通过这两个名字引用 agent 提供的能力。
	probeActionName      = "MaaYssProbeAction"
	probeRecognitionName = "MaaYssProbeRecognition"
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

	if err := maa.AgentServerRegisterCustomAction(probeActionName, maa.CustomActionFunc(runProbeAction)); err != nil {
		return fmt.Errorf("register custom action %s: %w", probeActionName, err)
	}
	if err := maa.AgentServerRegisterCustomRecognition(probeRecognitionName, maa.CustomRecognitionFunc(runProbeRecognition)); err != nil {
		return fmt.Errorf("register custom recognition %s: %w", probeRecognitionName, err)
	}

	fmt.Printf("[agent] registered: action=%s recognition=%s\n", probeActionName, probeRecognitionName)

	if err := maa.AgentServerStartUp(identifier); err != nil {
		return fmt.Errorf("start agent server: %w", err)
	}

	fmt.Println("[agent] server started, waiting for client")

	// 客户端断开后 Join 返回。
	maa.AgentServerJoin()
	fmt.Println("[agent] server stopped")
	return nil
}

// runProbeAction 是 P1.5 的被调方：打印收到的参数并返回成功。
func runProbeAction(ctx *maa.Context, arg *maa.CustomActionArg) bool {
	fmt.Printf("[agent] ACTION CALLED name=%s task=%s param=%s box=%v\n",
		arg.CustomActionName, arg.CurrentTaskName, arg.CustomActionParam, arg.Box)

	// 输出一份可被父进程校验的标记文件（工作目录下的 agent-action-hit.txt）。
	if wd, err := os.Getwd(); err == nil {
		hitPath := filepath.Join(wd, "agent-action-hit.txt")
		content := fmt.Sprintf("action=%s\ntask=%s\nparam=%s\n",
			arg.CustomActionName, arg.CurrentTaskName, arg.CustomActionParam)
		_ = os.WriteFile(hitPath, []byte(content), 0o644)
	}

	return true
}

// runProbeRecognition 是 P1.5 的被调方：返回一个固定框，验证识别链路也通。
func runProbeRecognition(ctx *maa.Context, arg *maa.CustomRecognitionArg) (*maa.CustomRecognitionResult, bool) {
	fmt.Printf("[agent] RECOGNITION CALLED name=%s task=%s param=%s roi=%v img=%v\n",
		arg.CustomRecognitionName, arg.CurrentTaskName, arg.CustomRecognitionParam, arg.Roi,
		arg.Img != nil)

	// 返回 ROI 起点的小区域，detail 供日志校验（Rect 为 [4]int：x, y, w, h）。
	box := arg.Roi
	w, h := box.Width(), box.Height()
	if w > 10 {
		w = 10
	}
	if h > 10 {
		h = 10
	}
	box = maa.Rect{box.X(), box.Y(), w, h}

	return &maa.CustomRecognitionResult{
		Box:    box,
		Detail: `{"probe":"MaaYssProbeRecognition","ok":true}`,
	}, true
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
