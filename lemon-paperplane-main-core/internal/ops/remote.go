package ops

import (
	"context"
	"errors"
	"strings"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/engine"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/gateway"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/remote"
)

// remoteDenied：经远程监听调用时拒绝的操作。gateway.* 是客户端功能；system.shutdown 防止把无头服务器停掉；
// mcp.config 会带出服务器的本机令牌（本机令牌绝不出这台机器）。
func remoteDenied(name string) bool {
	return strings.HasPrefix(name, "gateway.") || name == "system.shutdown" || name == "mcp.config"
}

// auditRemoteCall：远程调用的写操作记进审计日志（只记操作名）。
func (r *Registry) auditRemoteCall(ctx context.Context, op *Op) {
	if r.Extras.Remote == nil || op.ReadOnly {
		return
	}
	switch op.Name {
	case "terminal.input", "terminal.resize", "run.input", "view.event", "ui.respond", "fs.stat":
		return // 高频交互，不记
	}
	e := remote.AuditFrom(ctx)
	e.Event, e.Detail = "op", op.Name
	r.Extras.Remote.Audit(e)
}

func (r *Registry) remoteMgr() (*remote.Manager, error) {
	if r.Extras.Remote == nil {
		return nil, engine.Errf("remote.unavailable")
	}
	return r.Extras.Remote, nil
}

func (r *Registry) gatewayMgr() (*gateway.Manager, error) {
	if r.Extras.Gateway == nil {
		return nil, engine.Errf("remote.gatewayUnavailable")
	}
	return r.Extras.Gateway, nil
}

func remoteErr(err error) error {
	if errors.Is(err, remote.ErrNotFound) {
		return engine.ErrNotFound
	}
	var ge *gateway.Error
	if errors.As(err, &ge) {
		return err
	}
	if err != nil {
		return &engine.UserError{Msg: err.Error()}
	}
	return nil
}

func (r *Registry) registerRemote() {
	// ---------------------------------------------------------------- 本机作为服务器
	def(r, "remote.status", "Remote access status", "Whether other devices may connect to this server: listen address, suggested addresses, certificate fingerprint, password login, autostart, key and account counts.", "ri",
		func(ctx context.Context, _ *empty) (any, error) {
			m, err := r.remoteMgr()
			if err != nil {
				return nil, err
			}
			return m.Status(), nil
		})
	def(r, "remote.configure", "Configure remote access", "Enable/disable the TLS remote listener, change its port, allow password login, start the background service at login, restrict client IPs (CIDR list). Only given fields change.", "i",
		func(ctx context.Context, in *remote.ConfigureParams) (any, error) {
			m, err := r.remoteMgr()
			if err != nil {
				return nil, err
			}
			st, err := m.Configure(*in, remote.AuditFrom(ctx))
			return st, remoteErr(err)
		})
	def(r, "remote.keys", "Access keys", "List access keys (kind=key: created manually; kind=device: issued at account sign-in). current marks the key used by this request.", "ri",
		func(ctx context.Context, _ *empty) (any, error) {
			m, err := r.remoteMgr()
			if err != nil {
				return nil, err
			}
			cur := ""
			if p := remote.PrincipalFrom(ctx); p != nil {
				cur = p.KeyID
			}
			return m.Keys(cur), nil
		})
	def(r, "remote.keyCreate", "Create access key", "Create an access key for a device. Returns the key, the secret token and a connection code (shown only once).", "i",
		func(ctx context.Context, in *struct {
			Name    string `json:"name" required:"true"`
			Address string `json:"address,omitempty" desc:"extra address put first in the connection code, e.g. a public domain"`
		}) (any, error) {
			m, err := r.remoteMgr()
			if err != nil {
				return nil, err
			}
			k, err := m.CreateKey(in.Name, in.Address, remote.AuditFrom(ctx))
			return k, remoteErr(err)
		})
	def(r, "remote.keyRevoke", "Revoke access key", "Revoke an access key; devices using it are rejected immediately.", "di",
		func(ctx context.Context, in *struct {
			ID string `json:"id" required:"true"`
		}) (any, error) {
			m, err := r.remoteMgr()
			if err != nil {
				return nil, err
			}
			return nil, remoteErr(m.RevokeKey(in.ID, remote.AuditFrom(ctx)))
		})
	def(r, "remote.users", "Accounts", "List accounts allowed to sign in with a password.", "ri",
		func(ctx context.Context, _ *empty) (any, error) {
			m, err := r.remoteMgr()
			if err != nil {
				return nil, err
			}
			return m.Users(), nil
		})
	def(r, "remote.userSet", "Set account password", "Create an account or change its password (at least 10 characters; stored as an Argon2id hash).", "i",
		func(ctx context.Context, in *struct {
			Name     string `json:"name" required:"true"`
			Password string `json:"password" required:"true"`
		}) (any, error) {
			m, err := r.remoteMgr()
			if err != nil {
				return nil, err
			}
			return nil, remoteErr(m.SetUser(in.Name, in.Password, remote.AuditFrom(ctx)))
		})
	def(r, "remote.userRemove", "Delete account", "Delete an account and revoke the devices that signed in with it.", "di",
		func(ctx context.Context, in *struct {
			Name string `json:"name" required:"true"`
		}) (any, error) {
			m, err := r.remoteMgr()
			if err != nil {
				return nil, err
			}
			return nil, remoteErr(m.RemoveUser(in.Name, remote.AuditFrom(ctx)))
		})
	def(r, "remote.audit", "Security log", "Recent security events (sign-ins, failures, bans, key and account changes, remote write operations), newest first.", "ri",
		func(ctx context.Context, in *struct {
			Limit int `json:"limit,omitempty"`
		}) (any, error) {
			m, err := r.remoteMgr()
			if err != nil {
				return nil, err
			}
			return m.AuditRecent(in.Limit), nil
		})

	// ---------------------------------------------------------------- 客户端网关（只能本机调用）
	def(r, "gateway.decode", "Decode connection code", "Parse a connection code (ppc1_…) into server addresses, certificate fingerprint, access key and server name.", "ri",
		func(ctx context.Context, in *struct {
			Code string `json:"code" required:"true"`
		}) (any, error) {
			p, err := remote.DecodeCode(in.Code)
			if err != nil {
				return nil, &gateway.Error{C: "bad_request", Msg: err.Error(), St: 400}
			}
			return map[string]any{"urls": p.URLs, "fingerprint": p.Fingerprint, "token": p.Token, "name": p.Name}, nil
		})
	def(r, "gateway.probe", "Probe server", "Connect to a server address and return its URL, certificate fingerprint (unverified, for the user to confirm), whether the certificate is CA-trusted, and its hello info.", "ri",
		func(ctx context.Context, in *struct {
			Address string `json:"address" required:"true"`
		}) (any, error) {
			g, err := r.gatewayMgr()
			if err != nil {
				return nil, err
			}
			return g.Probe(ctx, in.Address)
		})
	def(r, "gateway.login", "Sign in to server", "Exchange account and password for a device access key on a server (the password is not stored).", "i",
		func(ctx context.Context, in *struct {
			URL         string `json:"url" required:"true"`
			Fingerprint string `json:"fingerprint"`
			Trusted     bool   `json:"trusted"`
			User        string `json:"user" required:"true"`
			Password    string `json:"password" required:"true"`
			Device      string `json:"device"`
		}) (any, error) {
			g, err := r.gatewayMgr()
			if err != nil {
				return nil, err
			}
			tok, err := g.Login(ctx, in.URL, in.Fingerprint, in.User, in.Password, in.Device, in.Trusted)
			if err != nil {
				return nil, err
			}
			return map[string]string{"token": tok}, nil
		})
	def(r, "gateway.open", "Open gateway", "Open a local loopback endpoint for a remote server. Use the returned port and token exactly like the local runtime.json; info is the server's system.info.", "i",
		func(ctx context.Context, in *struct {
			URL         string `json:"url" required:"true"`
			Fingerprint string `json:"fingerprint"`
			Trusted     bool   `json:"trusted"`
			Token       string `json:"token" required:"true"`
		}) (any, error) {
			g, err := r.gatewayMgr()
			if err != nil {
				return nil, err
			}
			return g.Open(ctx, in.URL, in.Fingerprint, in.Token, in.Trusted)
		})
	def(r, "gateway.close", "Close gateway", "Close a gateway and its port forwards.", "i",
		func(ctx context.Context, in *struct {
			ID string `json:"id" required:"true"`
		}) (any, error) {
			g, err := r.gatewayMgr()
			if err != nil {
				return nil, err
			}
			return nil, g.Close(in.ID)
		})
	def(r, "gateway.forward", "Forward port", "Forward a port on the server's localhost to this computer (same port number when free).", "i",
		func(ctx context.Context, in *struct {
			ID   string `json:"id" required:"true"`
			Port int    `json:"port" required:"true"`
		}) (any, error) {
			g, err := r.gatewayMgr()
			if err != nil {
				return nil, err
			}
			return g.Forward(in.ID, in.Port)
		})
	def(r, "gateway.unforward", "Stop port forward", "Stop forwarding a server port.", "i",
		func(ctx context.Context, in *struct {
			ID   string `json:"id" required:"true"`
			Port int    `json:"port" required:"true"`
		}) (any, error) {
			g, err := r.gatewayMgr()
			if err != nil {
				return nil, err
			}
			return nil, g.Unforward(in.ID, in.Port)
		})

	def(r, "runconfig.setRemoteLinks", "Set remote link options", "While connected to a remote server, how a run configuration's service addresses open: forward (tunnel the port to this computer) and/or direct (open the server address). At least one must be true.", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			ConfigID  string `json:"configId" required:"true"`
			Forward   bool   `json:"forward"`
			Direct    bool   `json:"direct"`
		}) (any, error) {
			return nil, r.e.SetRemoteLinks(in.ProjectID, in.ConfigID, model.RemoteLinks{Forward: in.Forward, Direct: in.Direct})
		})

	def(r, "fs.mkdir", "Create folder", "Create a folder at an absolute path (its parent must exist). Used by the remote folder picker.", "",
		func(ctx context.Context, in *struct {
			Path string `json:"path" required:"true"`
		}) (any, error) {
			p, err := r.e.FSMkdir(in.Path)
			if err != nil {
				return nil, err
			}
			return map[string]string{"path": p}, nil
		})

	// ---------------------------------------------------------------- 选择服务器上的文件夹
	def(r, "fs.browse", "Browse folders", "List any folder on this machine (for choosing folders on a remote server). Empty path = home directory. Returns path, parent, home, separator, Windows drive roots and entries (folders first).", "r",
		func(ctx context.Context, in *struct {
			Path string `json:"path"`
		}) (any, error) {
			return r.e.FSBrowse(in.Path)
		})
}
