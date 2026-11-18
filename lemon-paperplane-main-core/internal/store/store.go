// Package store 负责持久化：settings.json、workspaces.json、projects/<id>.json。
// 内存中持有全部数据，写入采用「临时文件 + rename」保证原子性。
package store

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/paths"
)

var ErrNotFound = errors.New("not found")

type Store struct {
	mu         sync.RWMutex
	layout     *paths.Layout
	settings   model.Settings
	workspaces []*model.Workspace
	projects   map[string]*model.Project
}

// Open 加载全部数据。
func Open(layout *paths.Layout) (*Store, error) {
	s := &Store{layout: layout, projects: map[string]*model.Project{}, settings: model.DefaultSettings()}
	if b, err := os.ReadFile(layout.SettingsFile()); err == nil {
		_ = json.Unmarshal(b, &s.settings)
	}
	if b, err := os.ReadFile(layout.WorkspacesFile()); err == nil {
		_ = json.Unmarshal(b, &s.workspaces)
	}
	entries, _ := os.ReadDir(layout.Projects)
	for _, e := range entries {
		if !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		b, err := os.ReadFile(filepath.Join(layout.Projects, e.Name()))
		if err != nil {
			continue
		}
		var p model.Project
		if json.Unmarshal(b, &p) == nil && p.ID != "" {
			s.projects[p.ID] = &p
		}
	}
	if len(s.workspaces) == 0 {
		s.workspaces = []*model.Workspace{{ID: NewID(), Name: "", ProjectIDs: []string{}, CreatedAt: time.Now()}}
		_ = s.saveWorkspacesLocked()
	}
	// 修复：工程引用的工作区不存在时挂到第一个工作区
	known := map[string]bool{}
	for _, w := range s.workspaces {
		known[w.ID] = true
	}
	for _, p := range s.projects {
		if !known[p.WorkspaceID] {
			p.WorkspaceID = s.workspaces[0].ID
		}
		if !contains(s.workspaceLocked(p.WorkspaceID).ProjectIDs, p.ID) {
			w := s.workspaceLocked(p.WorkspaceID)
			w.ProjectIDs = append(w.ProjectIDs, p.ID)
		}
	}
	return s, nil
}

// NewID 生成 16 字符随机 id。
func NewID() string {
	b := make([]byte, 8)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

func writeJSON(path string, v any) error {
	b, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

// ---------------------------------------------------------------------------
// 设置

func (s *Store) Settings() model.Settings {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.settings
}

// UpdateSettings 以 JSON merge 的方式更新设置。
func (s *Store) UpdateSettings(patch json.RawMessage) (model.Settings, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	cur, _ := json.Marshal(s.settings)
	var m map[string]any
	_ = json.Unmarshal(cur, &m)
	var p map[string]any
	if err := json.Unmarshal(patch, &p); err != nil {
		return s.settings, err
	}
	for k, v := range p {
		m[k] = v
	}
	b, _ := json.Marshal(m)
	next := s.settings
	if err := json.Unmarshal(b, &next); err != nil {
		return s.settings, err
	}
	s.settings = next
	return s.settings, writeJSON(s.layout.SettingsFile(), s.settings)
}

// MutateSettings 在锁内修改设置并保存。
func (s *Store) MutateSettings(fn func(*model.Settings)) model.Settings {
	s.mu.Lock()
	defer s.mu.Unlock()
	fn(&s.settings)
	_ = writeJSON(s.layout.SettingsFile(), s.settings)
	return s.settings
}

// ---------------------------------------------------------------------------
// 工作区

func (s *Store) workspaceLocked(id string) *model.Workspace {
	for _, w := range s.workspaces {
		if w.ID == id {
			return w
		}
	}
	return nil
}

func (s *Store) saveWorkspacesLocked() error {
	return writeJSON(s.layout.WorkspacesFile(), s.workspaces)
}

// Workspaces 返回副本。
func (s *Store) Workspaces() []model.Workspace {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make([]model.Workspace, 0, len(s.workspaces))
	for _, w := range s.workspaces {
		c := *w
		c.ProjectIDs = append([]string{}, w.ProjectIDs...)
		out = append(out, c)
	}
	return out
}

func (s *Store) Workspace(id string) (model.Workspace, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	w := s.workspaceLocked(id)
	if w == nil {
		return model.Workspace{}, false
	}
	c := *w
	c.ProjectIDs = append([]string{}, w.ProjectIDs...)
	return c, true
}

// DefaultWorkspaceID 返回第一个工作区。
func (s *Store) DefaultWorkspaceID() string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.workspaces[0].ID
}

func (s *Store) CreateWorkspace(name string) model.Workspace {
	s.mu.Lock()
	defer s.mu.Unlock()
	w := &model.Workspace{ID: NewID(), Name: name, ProjectIDs: []string{}, CreatedAt: time.Now()}
	s.workspaces = append(s.workspaces, w)
	_ = s.saveWorkspacesLocked()
	return *w
}

func (s *Store) RenameWorkspace(id, name string) (model.Workspace, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	w := s.workspaceLocked(id)
	if w == nil {
		return model.Workspace{}, ErrNotFound
	}
	w.Name = name
	_ = s.saveWorkspacesLocked()
	return *w, nil
}

// DeleteWorkspace 删除工作区；moveTo 非空时把工程移过去，否则返回被移除的工程 id。
func (s *Store) DeleteWorkspace(id, moveTo string) ([]string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.workspaces) <= 1 {
		return nil, errors.New("last workspace")
	}
	w := s.workspaceLocked(id)
	if w == nil {
		return nil, ErrNotFound
	}
	var removed []string
	if t := s.workspaceLocked(moveTo); t != nil && moveTo != id {
		for _, pid := range w.ProjectIDs {
			t.ProjectIDs = append(t.ProjectIDs, pid)
			if p := s.projects[pid]; p != nil {
				p.WorkspaceID = t.ID
				_ = s.saveProjectLocked(p)
			}
		}
	} else {
		for _, pid := range w.ProjectIDs {
			removed = append(removed, pid)
			delete(s.projects, pid)
			_ = os.Remove(filepath.Join(s.layout.Projects, pid+".json"))
		}
	}
	for i, x := range s.workspaces {
		if x.ID == id {
			s.workspaces = append(s.workspaces[:i], s.workspaces[i+1:]...)
			break
		}
	}
	return removed, s.saveWorkspacesLocked()
}

func (s *Store) ReorderWorkspaces(ids []string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	pos := map[string]int{}
	for i, id := range ids {
		pos[id] = i
	}
	sort.SliceStable(s.workspaces, func(i, j int) bool {
		pi, oki := pos[s.workspaces[i].ID]
		pj, okj := pos[s.workspaces[j].ID]
		if !oki {
			pi = 1 << 30
		}
		if !okj {
			pj = 1 << 30
		}
		return pi < pj
	})
	_ = s.saveWorkspacesLocked()
}

// ---------------------------------------------------------------------------
// 工程

func (s *Store) saveProjectLocked(p *model.Project) error {
	return writeJSON(filepath.Join(s.layout.Projects, p.ID+".json"), p)
}

// Project 返回深拷贝。
func (s *Store) Project(id string) (*model.Project, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	p := s.projects[id]
	if p == nil {
		return nil, false
	}
	return clone(p), true
}

// Projects 返回某工作区（空表示全部）的工程副本，按工作区顺序。
func (s *Store) Projects(workspaceID string) []*model.Project {
	s.mu.RLock()
	defer s.mu.RUnlock()
	var out []*model.Project
	for _, w := range s.workspaces {
		if workspaceID != "" && w.ID != workspaceID {
			continue
		}
		for _, pid := range w.ProjectIDs {
			if p := s.projects[pid]; p != nil {
				out = append(out, clone(p))
			}
		}
	}
	return out
}

// FindProjectByPath 按路径查找。
func (s *Store) FindProjectByPath(path string) (*model.Project, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	for _, p := range s.projects {
		if p.Path == path {
			return clone(p), true
		}
	}
	return nil, false
}

// PutProject 新增或整体替换工程并保存。
func (s *Store) PutProject(p *model.Project) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.workspaceLocked(p.WorkspaceID) == nil {
		p.WorkspaceID = s.workspaces[0].ID
	}
	old := s.projects[p.ID]
	if old != nil && old.WorkspaceID != p.WorkspaceID {
		if w := s.workspaceLocked(old.WorkspaceID); w != nil {
			w.ProjectIDs = remove(w.ProjectIDs, p.ID)
		}
	}
	w := s.workspaceLocked(p.WorkspaceID)
	if !contains(w.ProjectIDs, p.ID) {
		w.ProjectIDs = append(w.ProjectIDs, p.ID)
		_ = s.saveWorkspacesLocked()
	}
	s.projects[p.ID] = clone(p)
	return s.saveProjectLocked(p)
}

// UpdateProject 在锁内修改工程并保存，返回修改后的副本。
func (s *Store) UpdateProject(id string, fn func(p *model.Project) error) (*model.Project, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	p := s.projects[id]
	if p == nil {
		return nil, ErrNotFound
	}
	if err := fn(p); err != nil {
		return nil, err
	}
	if err := s.saveProjectLocked(p); err != nil {
		return nil, err
	}
	return clone(p), nil
}

func (s *Store) RemoveProject(id string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	p := s.projects[id]
	if p == nil {
		return ErrNotFound
	}
	delete(s.projects, id)
	for _, w := range s.workspaces {
		w.ProjectIDs = remove(w.ProjectIDs, id)
	}
	_ = s.saveWorkspacesLocked()
	return os.Remove(filepath.Join(s.layout.Projects, id+".json"))
}

// MoveProject 移动工程到工作区的 index 位置（<0 表示末尾）。
func (s *Store) MoveProject(id, workspaceID string, index int) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	p := s.projects[id]
	t := s.workspaceLocked(workspaceID)
	if p == nil || t == nil {
		return ErrNotFound
	}
	for _, w := range s.workspaces {
		w.ProjectIDs = remove(w.ProjectIDs, id)
	}
	if index < 0 || index > len(t.ProjectIDs) {
		index = len(t.ProjectIDs)
	}
	t.ProjectIDs = append(t.ProjectIDs[:index], append([]string{id}, t.ProjectIDs[index:]...)...)
	p.WorkspaceID = workspaceID
	_ = s.saveProjectLocked(p)
	return s.saveWorkspacesLocked()
}

// ReorderProjects 设置工作区内工程顺序。
func (s *Store) ReorderProjects(workspaceID string, ids []string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	w := s.workspaceLocked(workspaceID)
	if w == nil {
		return ErrNotFound
	}
	var next []string
	for _, id := range ids {
		if contains(w.ProjectIDs, id) && !contains(next, id) {
			next = append(next, id)
		}
	}
	for _, id := range w.ProjectIDs {
		if !contains(next, id) {
			next = append(next, id)
		}
	}
	w.ProjectIDs = next
	return s.saveWorkspacesLocked()
}

func clone(p *model.Project) *model.Project {
	b, _ := json.Marshal(p)
	var c model.Project
	_ = json.Unmarshal(b, &c)
	return &c
}

func contains(xs []string, s string) bool {
	for _, x := range xs {
		if x == s {
			return true
		}
	}
	return false
}

func remove(xs []string, s string) []string {
	out := xs[:0:0]
	for _, x := range xs {
		if x != s {
			out = append(out, x)
		}
	}
	return out
}
