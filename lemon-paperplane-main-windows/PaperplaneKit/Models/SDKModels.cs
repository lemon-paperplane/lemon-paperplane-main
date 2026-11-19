using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace PaperplaneKit;

// 对应插件 SDK（lemon-paperplane-main-plugin-sdk）的 protocol.go / manifest.go 中界面会用到的类型。
// 字段名以 Go json tag 为准。TableColumn/TableRow 同 Mac 改名为 ViewTableColumn/ViewTableRow。

public sealed record Facet
{
    public string Id { get; set; } = "";
    public string? Plugin { get; set; }
    public Dictionary<string, JToken>? Data { get; set; }
}

public sealed record Toolchain
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Vendor { get; set; }
    public string? Arch { get; set; }
    public string Path { get; set; } = "";
    public string? Executable { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
    public List<string> PathPrepend { get; set; } = new();
    public string? Source { get; set; }
    public bool Managed { get; set; }
    public string? Label { get; set; }

    /// <summary>界面显示名：优先 label，否则「厂商 版本」</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Label)) return Label!;
            if (!string.IsNullOrEmpty(Vendor)) return $"{Vendor} {Version}";
            return Version;
        }
    }
}

public sealed record ToolchainRequirement
{
    public string Kind { get; set; } = "";
    public string? Constraint { get; set; }
    public string? Preferred { get; set; }
    public string? Source { get; set; }
    public string? Purpose { get; set; }
    public bool Optional { get; set; }
}

public sealed record DependencyState
{
    /// <summary>ok | missing | outdated | unknown | none</summary>
    public string State { get; set; } = "";
    public string? Manager { get; set; }
    public string? Message { get; set; }
}

public sealed record InfoItem
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Icon { get; set; }
}

public sealed record Option
{
    public string Value { get; set; } = "";
    public string? Label { get; set; }
    public string DisplayLabel => string.IsNullOrEmpty(Label) ? Value : Label!;
}

public sealed record Fix
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public Dictionary<string, string> Params { get; set; } = new();
    public bool Recommended { get; set; }
    public string? Confirm { get; set; }
    public string? Plugin { get; set; }
}

/// <summary>修复动作的 kind。</summary>
public static class FixKind
{
    public const string SetToolchain = "setToolchain";
    public const string SetEnv = "setEnv";
    public const string AppendEnv = "appendEnv";
    public const string UnsetEnv = "unsetEnv";
    public const string SetRunOption = "setRunOption";
    public const string InstallDependencies = "installDependencies";
    public const string KillPort = "killPort";
    public const string Reimport = "reimport";
    public const string Restart = "restart";
    public const string OpenURL = "openUrl";
    public const string Custom = "custom";
}

public sealed record Diagnosis
{
    public string? Id { get; set; }
    public string? RuleId { get; set; }
    public string? Plugin { get; set; }
    /// <summary>error | warning | info</summary>
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Message { get; set; }
    public string? Detail { get; set; }
    public List<Fix> Fixes { get; set; } = new();
    public string? Line { get; set; }

    /// <summary>用于列表 diff 的稳定键</summary>
    public string StableKey => Id ?? $"{RuleId ?? ""}|{Title}|{Line ?? ""}";
}

public static class Severity
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";

    /// <summary>严重度排序：error &gt; warning &gt; info</summary>
    public static int Rank(string? s) => s switch
    {
        Error => 3,
        Warning => 2,
        Info => 1,
        _ => 0,
    };
}

/// <summary>
/// 声明式表单字段。type：string | text | boolean | number | select | multiselect | keyvalue | path | list | code，
/// 另外界面识别 password / secret 为密码框。
/// </summary>
public sealed record FormField
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public JToken? Default { get; set; }
    public List<Option> Options { get; set; } = new();
    public string? OptionsFrom { get; set; }
    /// <summary>type=code 时代码输入框的语言（如 shell）</summary>
    public string? Language { get; set; }
    public string? Placeholder { get; set; }
    public string? Info { get; set; }
    public bool Required { get; set; }
    public bool Advanced { get; set; }
    /// <summary>选项随时会变（如已连接设备）：表单打开时及打开期间定期经 runconfig.options 实时取</summary>
    public bool Live { get; set; }

    public string Id => Key;

    /// <summary>是否按密码框显示</summary>
    public bool IsSecret
    {
        get
        {
            if (Type == "password" || Type == "secret") return true;
            var k = Key.ToLowerInvariant();
            return Type == "string" && (k.Contains("password") || k.Contains("passphrase") || k.Contains("secret") || k == "token");
        }
    }
}

public sealed record Engines
{
    public string Paperplane { get; set; } = "";
}

public sealed record Dependency
{
    public string Id { get; set; } = "";
    public string? Version { get; set; }
    public bool Optional { get; set; }
}

public sealed record ActionDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Icon { get; set; }
    public List<string> Facets { get; set; } = new();
    public string? Confirm { get; set; }
    public string? Group { get; set; }
    public List<string> Locations { get; set; } = new();
    public List<FormField> Form { get; set; } = new();
}

/// <summary>界面挂载位置。</summary>
public static class ViewLocation
{
    public const string ProjectTab = "project.tab";
    public const string ProjectSection = "project.section";
    public const string Sidebar = "sidebar";
    public const string RunPanel = "run.panel";
    public const string VcsTab = "vcs.tab";
    public const string Settings = "settings";
}

/// <summary>动作挂载位置。</summary>
public static class ActionLocation
{
    public const string ProjectToolbar = "project.toolbar";
    public const string ProjectMenu = "project.menu";
    public const string CardMenu = "card.menu";
    public const string RunToolbar = "run.toolbar";
    public const string VcsToolbar = "vcs.toolbar";
    public const string VcsFileMenu = "vcs.fileMenu";
    public const string VcsCommitMenu = "vcs.commitMenu";
    public const string GlobalMenu = "global.menu";
}

public sealed record NotifyParams
{
    /// <summary>info | warning | error</summary>
    public string Level { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Message { get; set; }
}

public sealed record AvailableVersion
{
    public string Version { get; set; } = "";
    public string? Label { get; set; }
    public string? Vendor { get; set; }
    public bool Lts { get; set; }
    public bool Recommended { get; set; }
    public string Id => $"{Vendor ?? ""}|{Version}";
}

// MARK: - 插件界面描述语言（view.go）

public sealed record ViewNode
{
    public string Type { get; set; } = "";
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? Icon { get; set; }
    public string? Tone { get; set; }
    public string? Info { get; set; }
    public bool Mono { get; set; }
    public string? Style { get; set; }
    public string? Action { get; set; }
    public string? Href { get; set; }
    public string? Confirm { get; set; }
    public bool Disabled { get; set; }
    public double Value { get; set; }
    public List<KV> Items { get; set; } = new();
    public List<ViewTableColumn> Columns { get; set; } = new();
    public List<ViewTableRow> Rows { get; set; } = new();
    public bool Selectable { get; set; }
    public List<FormField> Fields { get; set; } = new();
    public Dictionary<string, JToken>? Values { get; set; }
    public string? SubmitText { get; set; }
    public string? Console { get; set; }
    public bool Collapsed { get; set; }
    public List<ViewNode> Children { get; set; } = new();
    public Dictionary<string, string>? Data { get; set; }
}

public sealed record KV
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Mono { get; set; }
    public string? Href { get; set; }
    public string? Tone { get; set; }
}

public sealed record ViewTableColumn
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public int Width { get; set; }
    public string? Align { get; set; }
    public bool Mono { get; set; }
}

public sealed record ViewTableRow
{
    public string Id { get; set; } = "";
    public Dictionary<string, string> Cells { get; set; } = new();
    public string? Icon { get; set; }
    public string? Tone { get; set; }
    public string? Action { get; set; }
    public List<ViewNode> Actions { get; set; } = new();
    public List<ViewTableRow> Children { get; set; } = new();
    public bool Expanded { get; set; }
}

public sealed record ViewRenderResult
{
    public ViewNode Root { get; set; } = new();
    public Dictionary<string, JToken>? State { get; set; }
    public int RefreshMs { get; set; }
}

public sealed record ViewEventResult
{
    public bool Refresh { get; set; }
    public Dictionary<string, JToken>? State { get; set; }
    public string? Message { get; set; }
    public string? OpenUrl { get; set; }
    public bool Reimport { get; set; }
}
