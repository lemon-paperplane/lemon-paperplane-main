// Package proc 是平台相关的进程操作：进程组、整树结束、资源统计、监听端口。
package proc

// Stat 是一棵进程树的资源占用。
type Stat struct {
	CPU   float64 // 百分比（多核可超过 100）
	RSSMB float64
	PIDs  []int
	Procs []ProcInfo // 进程树里每个进程的占用（按内存从大到小）
}

// ProcInfo 是进程树里的一个进程。
type ProcInfo struct {
	PID   int     `json:"pid"`
	PPID  int     `json:"ppid"`
	Name  string  `json:"name"`
	CPU   float64 `json:"cpu"`
	RSSMB float64 `json:"rssMB"`
}
