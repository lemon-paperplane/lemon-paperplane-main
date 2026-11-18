package sdk

import "testing"

func TestConstraints(t *testing.T) {
	cases := []struct {
		c, v string
		ok   bool
	}{
		{"17", "17.0.16", true},
		{"17", "18.0.1", false},
		{">=17", "21.0.12", true},
		{">=17 <24", "24.0.1", false},
		{">=17 <24", "23.0.1", true},
		{"^20.19 || >=22.12", "22.20.0", true},
		{"^20.19 || >=22.12", "21.1.0", false},
		{"^20.19 || >=22.12", "20.19.0", true},
		{"^18.18.0 || >=20", "16.17.1", false},
		{">=3.12,<3.13", "3.12.9", true},
		{">=3.12,<3.13", "3.13.5", false},
		{"~=3.10", "3.13.0", true},
		{"~=3.10.2", "3.11.0", false},
		{"==3.12.*", "3.12.4", true},
		{"<=3.12", "3.12.7", true},
		{"<=3.12", "3.13.0", false},
		{"16.x", "16.17.1", true},
		{"lts", "1", false},
		{"", "1.2.3", true},
		{">16", "16.9.0", false},
		{">16", "17.0.0", true},
		{"1.21", "1.21.5", true},
		{">= 1.23", "1.25.4", true},
	}
	for _, c := range cases {
		if got := MatchVersion(c.c, c.v); got != c.ok {
			t.Errorf("MatchVersion(%q, %q) = %v, want %v", c.c, c.v, got, c.ok)
		}
	}
}

func TestBestVersion(t *testing.T) {
	cands := []string{"21.0.12", "17.0.16", "8.0.492", "21.0.10"}
	if i := BestVersion(cands, ">=17", "17"); cands[i] != "17.0.16" {
		t.Errorf("preferred 17 got %s", cands[i])
	}
	if i := BestVersion(cands, ">=17", ""); cands[i] != "21.0.12" {
		t.Errorf("highest got %s", cands[i])
	}
	if i := BestVersion(cands, ">=25", ""); i != -1 {
		t.Errorf("expected none")
	}
}

func TestSplitArgs(t *testing.T) {
	got := SplitArgs(`-Xmx512m "-Dfoo=a b" --x='y z'`)
	want := []string{"-Xmx512m", "-Dfoo=a b", "--x=y z"}
	if len(got) != len(want) {
		t.Fatalf("got %v", got)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("got %v", got)
		}
	}
}
