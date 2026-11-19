using System;
using System.ComponentModel;
using System.Linq;

namespace PaperplaneKit.Tests;

/// <summary>远程服务器：服务器路径、网关 / remote.* 模型解码、凭据管理器。</summary>
public class RemoteTests
{
    [Test]
    public void ServerPathPosix()
    {
        const string s = "/";
        Assert.Equal("/home/me/Code", ServerPath.Join("/home/me", "Code", s));
        Assert.Equal("/home/me/a/b", ServerPath.Join("/home/me/", "a/b", s));
        Assert.Equal("/abs", ServerPath.Join("/home/me", "/abs", s));
        Assert.Equal("/x", ServerPath.Join("/", "x", s));
        Assert.Equal("Code", ServerPath.Name("/home/me/Code/", s));
        Assert.Equal("/", ServerPath.Name("/", s));
        Assert.Equal("/home/me", ServerPath.Parent("/home/me/Code", s));
        Assert.Equal("/", ServerPath.Parent("/home", s));
        Assert.Null(ServerPath.Parent("/", s));
        Assert.True(ServerPath.IsAbsolute("/a", s));
        Assert.False(ServerPath.IsAbsolute("C:\\a", s));
        Assert.False(ServerPath.Same("/Home/me", "/home/me", s));
        Assert.True(ServerPath.Same("/home/me/", "/home/me", s));
        Assert.Equal("a/b", ServerPath.Relative("/p", "/p/a/b", s));
        Assert.Equal("", ServerPath.Relative("/p/", "/p", s));
        Assert.Null(ServerPath.Relative("/p", "/pq/a", s));
        Assert.Equal("~/Code", ServerPath.Abbreviate("/home/me/Code", "/home/me", s));
        Assert.Equal("/home/meme", ServerPath.Abbreviate("/home/meme", "/home/me", s));
        var crumbs = ServerPath.Breadcrumbs("/home/me", s);
        Assert.Equal("/,home,me", string.Join(",", crumbs.Select(c => c.Name)));
        Assert.Equal("/home", crumbs[1].Path);
    }

    [Test]
    public void ServerPathWindows()
    {
        const string s = "\\";
        Assert.Equal("C:\\Users\\me\\Code", ServerPath.Join("C:\\Users\\me", "Code", s));
        Assert.Equal("C:\\a\\b", ServerPath.Join("C:\\", "a/b", s));
        Assert.Equal("C:\\x", ServerPath.Join("C:", "x", s));
        Assert.Equal("Code", ServerPath.Name("C:\\Users\\me\\Code\\", s));
        Assert.Equal("C:\\Users", ServerPath.Parent("C:\\Users\\me", s));
        Assert.Equal("C:\\", ServerPath.Parent("C:\\Users", s));
        Assert.Null(ServerPath.Parent("C:\\", s));
        Assert.True(ServerPath.IsRoot("D:\\", s));
        Assert.True(ServerPath.IsAbsolute("c:/x", s));
        Assert.False(ServerPath.IsAbsolute("/x", s));
        Assert.True(ServerPath.Same("C:\\Users\\Me\\", "c:/users/me", s));
        Assert.Equal("a\\b", ServerPath.Relative("C:\\P", "c:\\p\\a\\b", s));
        Assert.Equal("~\\Code", ServerPath.Abbreviate("C:\\Users\\Me\\Code", "C:\\Users\\me", s));
        var crumbs = ServerPath.Breadcrumbs("C:\\Users\\me", s);
        Assert.Equal("C:\\,Users,me", string.Join(",", crumbs.Select(c => c.Name)));
    }

    [Test]
    public void FormatAbbreviateUsesServerHome()
    {
        var oldHome = Format.ServerHome;
        var oldSep = Format.ServerSeparator;
        try
        {
            Format.ServerHome = "/home/me";
            Format.ServerSeparator = "/";
            Assert.Equal("~/x", Format.AbbreviatePath("/home/me/x"));
            Assert.Equal("/home/Me/x", Format.AbbreviatePath("/home/Me/x"));
            // 显式 home 不受影响
            Assert.Equal("~\\a", Format.AbbreviatePath("C:\\U\\a", home: "C:\\U"));
        }
        finally
        {
            Format.ServerHome = oldHome;
            Format.ServerSeparator = oldSep;
        }
    }

    [Test]
    public void DecodeRemoteModels()
    {
        var st = Json.Deserialize<RemoteStatus>(@"{""enabled"":true,""running"":true,""listen"":""0.0.0.0:47290"",""port"":47290,
            ""addresses"":[""https://192.168.1.156:47290""],""fingerprint"":""sha256:ab"",""fingerprintDisplay"":""AB12 CD34"",
            ""customCert"":false,""passwordLogin"":true,""autostart"":false,""autostartSupported"":true,""allowCIDRs"":[],""keys"":2,""users"":1}");
        Assert.True(st.Enabled);
        Assert.Equal(47290, st.Port);
        Assert.Equal("AB12 CD34", st.FingerprintDisplay);
        Assert.Equal(1, st.Addresses.Count);
        Assert.Equal("", st.Error);

        var keys = Json.Deserialize<System.Collections.Generic.List<RemoteKey>>(@"[{""id"":""1a2b"",""name"":""MacBook"",""kind"":""device"",""user"":""me"",
            ""createdAt"":""2026-11-19T10:00:00+08:00"",""lastUsedAt"":""2026-11-19T11:00:00Z"",""lastIP"":""192.168.1.2"",""current"":true}]");
        Assert.Equal(RemoteKeyKind.Device, keys[0].Kind);
        Assert.Equal("192.168.1.2", keys[0].LastIP);
        Assert.True(keys[0].Current);
        Assert.NotNull(keys[0].LastUsedAt);

        var open = Json.Deserialize<GatewayOpen>(@"{""id"":""abc"",""port"":51234,""token"":""t"",""info"":{""version"":""0.1.0"",""os"":""linux"",
            ""hostname"":""box"",""home"":""/home/me"",""pathSeparator"":""/"",""apiVersion"":1}}");
        Assert.Equal(51234, open.Port);
        Assert.Equal("/", open.Info.Separator);
        Assert.Equal("box", open.Info.Hostname);

        var audit = Json.Deserialize<System.Collections.Generic.List<RemoteAuditEntry>>(@"[{""time"":""2026-11-19T10:00:00Z"",""event"":""login.fail"",""ip"":""1.2.3.4"",""who"":""root""}]");
        Assert.Equal("1.2.3.4", audit[0].Ip);

        var browse = Json.Deserialize<FSBrowseResult>(@"{""path"":""C:\\Users"",""parent"":""C:\\"",""home"":""C:\\Users\\me"",""sep"":""\\"",
            ""roots"":[""C:\\"",""D:\\""],""entries"":[{""name"":""me"",""path"":""C:\\Users\\me"",""dir"":true,""hidden"":false,""size"":0,""modTime"":""2026-11-19T10:00:00Z""}]}");
        Assert.Equal(2, browse.Roots.Count);
        Assert.True(browse.Entries[0].Dir);

        var summary = Json.Deserialize<ProjectSummary>(@"{""id"":""p"",""remoteLinks"":{""c1"":{""forward"":false,""direct"":true}}}");
        Assert.False(summary.LinksOf("c1").Forward);
        Assert.True(summary.LinksOf("c2").Forward);
        // 旧内核没有 pathSeparator：按系统推断
        Assert.Equal("\\", Json.Deserialize<SystemInfo>(@"{""os"":""windows""}").Separator);
        Assert.Equal("/", Json.Deserialize<SystemInfo>(@"{""os"":""darwin""}").Separator);
    }

    [Test]
    public void WebviewUrlRebasedToClient()
    {
        var c = new ApiClient(new Uri("http://127.0.0.1:51234"), "local-token");
        // 远程：相对路径，不带令牌
        var a = c.RebasedWebviewUrl("/plugins/web/index.html?projectId=p1&runId=r1")!;
        Assert.Equal("http://127.0.0.1:51234/plugins/web/index.html?token=local-token&projectId=p1&runId=r1", a.AbsoluteUri);
        // 本机：带服务器自己令牌的绝对地址 → 换成本客户端的地址与令牌
        var b = c.RebasedWebviewUrl("http://127.0.0.1:47213/plugins/web/index.html?token=server&moduleId=m")!;
        Assert.Equal("http://127.0.0.1:51234/plugins/web/index.html?token=local-token&moduleId=m", b.AbsoluteUri);
        Assert.Null(c.RebasedWebviewUrl("not a url"));
    }

    [Test]
    public void CredentialStoreRoundTrip()
    {
        var target = "LemonPaperplane-test:" + Guid.NewGuid().ToString("N");
        try
        {
            CredentialStore.Write(target, "ppk_1234_秘密");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1312)
        {
            // ssh（S4U 登录）会话没有凭据库：跳过（在桌面会话里跑才能验证）
            Console.WriteLine("    （当前登录会话没有凭据库，跳过：" + e.Message + "）");
            return;
        }
        try
        {
            Assert.Equal("ppk_1234_秘密", CredentialStore.Read(target));
            CredentialStore.Write(target, "second");
            Assert.Equal("second", CredentialStore.Read(target));
        }
        finally
        {
            Assert.True(CredentialStore.Delete(target));
        }
        Assert.Null(CredentialStore.Read(target));
        Assert.False(CredentialStore.Delete(target));
    }
}
