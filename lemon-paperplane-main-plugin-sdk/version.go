package sdk

import (
	"regexp"
	"sort"
	"strconv"
	"strings"
)

// Version 是宽松解析的版本号：最多 4 段数字 + 预发布标记。
// 能解析 "17.0.12"、"v22.20.0"、"go1.25.4"、"21.0.12.1"、"3.13.5"、"9.0.0-rc1"。
// Java 的 "1.8.0_492" 这类格式请先由 java 插件规范化为 "8.0.492"。
type Version struct {
	Parts [4]int
	N     int    // 实际给出的段数
	Pre   string // 预发布标记，如 "rc1"、"ea"
	Raw   string
}

var versionRe = regexp.MustCompile(`(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:[._](\d+))?(?:[-+.]?([A-Za-z][0-9A-Za-z.-]*))?`)

// ParseVersion 解析版本号，失败返回 ok=false。
func ParseVersion(s string) (Version, bool) {
	raw := s
	s = strings.TrimSpace(s)
	s = strings.TrimPrefix(s, "v")
	s = strings.TrimPrefix(s, "go")
	m := versionRe.FindStringSubmatch(s)
	if m == nil {
		return Version{}, false
	}
	var v Version
	v.Raw = raw
	for i := 1; i <= 4; i++ {
		if m[i] == "" {
			break
		}
		n, err := strconv.Atoi(m[i])
		if err != nil {
			return Version{}, false
		}
		v.Parts[i-1] = n
		v.N = i
	}
	v.Pre = m[5]
	return v, true
}

// MustVersion 解析失败时返回零值。
func MustVersion(s string) Version {
	v, _ := ParseVersion(s)
	return v
}

// Major 返回主版本号。
func (v Version) Major() int { return v.Parts[0] }

// Minor 返回次版本号。
func (v Version) Minor() int { return v.Parts[1] }

// String 返回规范化字符串（按给出的段数）。
func (v Version) String() string {
	n := v.N
	if n == 0 {
		n = 1
	}
	parts := make([]string, n)
	for i := 0; i < n; i++ {
		parts[i] = strconv.Itoa(v.Parts[i])
	}
	s := strings.Join(parts, ".")
	if v.Pre != "" {
		s += "-" + v.Pre
	}
	return s
}

// Compare 比较两个版本：-1、0、1。预发布版本小于同号正式版。
func (v Version) Compare(o Version) int {
	for i := 0; i < 4; i++ {
		if v.Parts[i] != o.Parts[i] {
			if v.Parts[i] < o.Parts[i] {
				return -1
			}
			return 1
		}
	}
	switch {
	case v.Pre == o.Pre:
		return 0
	case v.Pre == "":
		return 1
	case o.Pre == "":
		return -1
	case v.Pre < o.Pre:
		return -1
	default:
		return 1
	}
}

// CompareVersions 比较两个版本字符串，无法解析的排在最前。
func CompareVersions(a, b string) int {
	va, oka := ParseVersion(a)
	vb, okb := ParseVersion(b)
	switch {
	case !oka && !okb:
		return strings.Compare(a, b)
	case !oka:
		return -1
	case !okb:
		return 1
	}
	return va.Compare(vb)
}

// SortVersionsDesc 把版本字符串从高到低排序。
func SortVersionsDesc(vs []string) {
	sort.SliceStable(vs, func(i, j int) bool { return CompareVersions(vs[i], vs[j]) > 0 })
}

// ---------------------------------------------------------------------------
// 约束：npm semver 范围语法的实用子集
//   "17"  "17.x"  "17.*"  "*"  ""            x-range
//   ">=17"  ">17.0.2"  "<24"  "<=3.12"  "=1.2.3"
//   "^20.19"  "~3.10"
//   "1.2 - 2.3"                                 连字符范围
//   ">=17 <24"                                  空格（或逗号）= 且
//   "^18.18.0 || >=20"                          || = 或

type comparator struct {
	op string // = > >= < <=
	v  Version
}

// Constraint 是解析后的版本约束。
type Constraint struct {
	sets  [][]comparator // OR of AND
	raw   string
	any   bool
	never bool
}

// ParseConstraint 解析约束字符串；无法识别的片段会被忽略。
func ParseConstraint(s string) Constraint {
	c := Constraint{raw: s}
	s = strings.TrimSpace(s)
	if s == "" || s == "*" || s == "x" || strings.EqualFold(s, "latest") {
		c.any = true
		return c
	}
	for _, alt := range strings.Split(s, "||") {
		alt = strings.TrimSpace(alt)
		if alt == "" || alt == "*" {
			c.any = true
			continue
		}
		var set []comparator
		// 连字符范围
		if a, b, ok := strings.Cut(alt, " - "); ok {
			if va, ok := ParseVersion(a); ok {
				set = append(set, comparator{">=", va})
			}
			if vb, ok := ParseVersion(b); ok {
				set = append(set, upperFromPartial(vb, true)...)
			}
			c.sets = append(c.sets, set)
			continue
		}
		fields := strings.FieldsFunc(alt, func(r rune) bool { return r == ' ' || r == ',' })
		for i := 0; i < len(fields); i++ {
			f := fields[i]
			// 允许 ">= 17" 这种运算符与版本分开写
			if (f == ">=" || f == "<=" || f == ">" || f == "<" || f == "=" || f == "==" || f == "^" || f == "~") && i+1 < len(fields) {
				f += fields[i+1]
				i++
			}
			set = append(set, parseComparator(f)...)
		}
		if len(set) == 0 {
			// 无法识别的片段（如 "lts"）不匹配任何版本，由插件自行转换后再传入
			continue
		}
		c.sets = append(c.sets, set)
	}
	if !c.any && len(c.sets) == 0 {
		c.never = true
	}
	return c
}

func parseComparator(f string) []comparator {
	op := ""
	for _, p := range []string{">=", "<=", "==", "~=", ">", "<", "=", "^", "~", "v"} {
		if strings.HasPrefix(f, p) {
			op = p
			f = f[len(p):]
			break
		}
	}
	if op == "v" {
		op = ""
		f = "v" + f
	}
	f = strings.TrimSpace(f)
	if f == "*" || f == "x" || f == "X" || f == "" {
		return nil
	}
	// x-range: 去掉 .x/.*
	trimmed := f
	for _, suf := range []string{".x", ".X", ".*"} {
		for strings.HasSuffix(trimmed, suf) {
			trimmed = strings.TrimSuffix(trimmed, suf)
		}
	}
	v, ok := ParseVersion(trimmed)
	if !ok {
		return nil
	}
	switch op {
	case "", "=", "==":
		if v.N >= 3 && trimmed == f {
			return []comparator{{"=", v}}
		}
		// 部分版本 = 范围
		return append([]comparator{{">=", v}}, upperFromPartial(v, false)...)
	case ">=", ">", "<=", "<":
		if op == "<=" && v.N < 3 {
			// "<=3.12" 等价于 "<3.13"
			return upperFromPartial(v, false)
		}
		if op == ">" && v.N < 3 {
			// ">16" 等价于 ">=17"
			return []comparator{{">=", bump(v, v.N-1)}}
		}
		return []comparator{{op, v}}
	case "^":
		lo := v
		idx := 0
		if v.Parts[0] == 0 && v.N > 1 {
			idx = 1
			if v.Parts[1] == 0 && v.N > 2 {
				idx = 2
			}
		}
		return []comparator{{">=", lo}, {"<", bump(v, idx)}}
	case "~", "~=":
		idx := 1
		if v.N <= 1 {
			idx = 0
		}
		if op == "~=" && v.N >= 2 {
			// PEP 440: ~=3.10 → >=3.10 <4；~=3.10.2 → >=3.10.2 <3.11
			idx = v.N - 2
		}
		return []comparator{{">=", v}, {"<", bump(v, idx)}}
	}
	return nil
}

// upperFromPartial 给部分版本生成上界：16 → <17；3.12 → <3.13；inclusiveFull 用于连字符范围的完整版本。
func upperFromPartial(v Version, inclusiveFull bool) []comparator {
	if v.N >= 3 {
		return []comparator{{"<=", v}}
	}
	return []comparator{{"<", bump(v, v.N-1)}}
}

func bump(v Version, idx int) Version {
	if idx < 0 {
		idx = 0
	}
	var out Version
	for i := 0; i < idx; i++ {
		out.Parts[i] = v.Parts[i]
	}
	out.Parts[idx] = v.Parts[idx] + 1
	out.N = idx + 1
	out.Pre = "0" // 让 "<17" 也排除 17 的预发布版
	return out
}

// Any 表示约束接受任意版本。
func (c Constraint) Any() bool { return c.any || (len(c.sets) == 0 && !c.never) }

// String 返回原始字符串。
func (c Constraint) String() string { return c.raw }

// Check 判断版本是否满足约束。
func (c Constraint) Check(v Version) bool {
	if c.any {
		return true
	}
	if c.never {
		return false
	}
	for _, set := range c.sets {
		ok := true
		for _, cmp := range set {
			r := v.Compare(cmp.v)
			switch cmp.op {
			case "=":
				ok = r == 0
			case ">":
				ok = r > 0
			case ">=":
				ok = r >= 0
			case "<":
				ok = r < 0
			case "<=":
				ok = r <= 0
			}
			if !ok {
				break
			}
		}
		if ok {
			return true
		}
	}
	return false
}

// MatchVersion 判断版本字符串是否满足约束字符串。
func MatchVersion(constraint, version string) bool {
	v, ok := ParseVersion(version)
	if !ok {
		return false
	}
	return ParseConstraint(constraint).Check(v)
}

// BestVersion 从候选里选满足约束的最高版本；preferred 非空时优先选与之前缀相同（如 "17" 匹配 17.x）的最高版本。
// 返回下标，找不到返回 -1。
func BestVersion(candidates []string, constraint, preferred string) int {
	c := ParseConstraint(constraint)
	best := -1
	var bestV Version
	bestPref := false
	pref := ParseConstraint(preferred)
	for i, s := range candidates {
		v, ok := ParseVersion(s)
		if !ok || !c.Check(v) {
			continue
		}
		isPref := preferred != "" && pref.Check(v)
		switch {
		case best < 0,
			isPref && !bestPref,
			isPref == bestPref && v.Compare(bestV) > 0:
			best, bestV, bestPref = i, v, isPref
		}
	}
	return best
}
