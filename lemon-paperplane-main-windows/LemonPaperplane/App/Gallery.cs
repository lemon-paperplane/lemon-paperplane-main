using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperplaneKit;

namespace LemonPaperplane;

// 组件陈列页（只在调试桥或 --gallery 参数下打开）：把全部组件的各种状态摆出来，深浅色各截一张图核对。
// 只给开发者看：区块标题直接用组件名（代码标识符），示例文案尽量用 i18n 里现成的 key。

public static class Gallery
{
    public static readonly string[] Tabs = { "controls", "display", "overlays", "icons" };

    private static readonly State<string> Tab = new("controls");

    /// <summary>在主窗口里打开陈列页（工具栏、侧栏、内容岛都换成陈列内容）。</summary>
    public static void Show(MainWindow w, string? tab = null)
    {
        if (tab != null && Tabs.Contains(tab)) Tab.Value = tab;
        var toggle = new PPIconButton(w.SidebarVisible ? "panel-left-close" : "panel-left-open", L.T("toolbar.toggleSidebar")).Id("toolbar.sidebar");
        toggle.Action = () =>
        {
            w.SidebarVisible = !w.SidebarVisible;
            toggle.Icon = IconKey.Builtin(w.SidebarVisible ? "panel-left-close" : "panel-left-open");
        };
        w.ToolbarLeft = UI.HStack(M.Xs, toggle, UI.Text("Component Gallery", Typo.Title).Margins(M.Xs, 0, 0, 0));
        var search = new State<string>("");
        var theme = Bind.Of(() => Theme.Setting, v => Theme.Apply(v, persist: false));
        var locale = Bind.Of(() => L10n.Shared.Setting, v => L10n.Shared.Apply(v, persist: false));
        var importLabel = UI.Box(UI.HStack(M.Xs, new PPIcon("plus"), UI.Text(L.T("toolbar.import"), Typo.Label).Shrink(false), new PPIcon("chevron-down", 12)))
            .Pad(M.Md, 0).H(M.ControlHeightSmall + 2).Rounded(M.RadiusMD).Bg(ColorToken.Accent).Fg(ColorToken.TextOnAccent);
        w.ToolbarRight = UI.HStack(M.Xs,
            new PPTextField(L.T("toolbar.search"), search, icon: "search", clearable: true, height: M.ControlHeightSmall + 2).W(220).Margins(0, 0, M.Xs, 0).Id("toolbar.search"),
            new PPSegmented<string>(new[] { ("system", L.T("settings.followSystem")), ("light", L.T("settings.light")), ("dark", L.T("settings.dark")) }, theme, PPButtonSize.Small).Id("gallery.theme"),
            new PPSegmented<string>(new[] { ("zh-Hans", "中文"), ("en", "EN") }, locale, PPButtonSize.Small).Id("gallery.locale"),
            new PPMenuButton(() => new[]
            {
                MenuEntry.Item(L.T("import.folder"), () => { }, icon: "folder-input", shortcut: "Ctrl+N"),
                MenuEntry.Item(L.T("import.scan"), () => { }, icon: "scan-search", shortcut: "Ctrl+Shift+N"),
                MenuEntry.Divider,
                MenuEntry.Item(L.T("import.clone"), () => { }, icon: "git-branch"),
            }, importLabel, PopupAlignment.Trailing, L.T("toolbar.import")).Id("toolbar.import"),
            UI.Box(UI.HStack(M.Xs, new PPIcon("activity", M.Icon, ColorToken.StatusRunning), UI.Text("2", Typo.Label).TabularDigits())).Pad(M.Sm, 0).H(M.IconButton).Tip(L.T("toolbar.runs")),
            new PPIconButton("loader-circle", L.T("toolbar.tasks")));
        w.Sidebar = new GallerySidebar();
        w.Page = new GalleryPage();
    }

    private sealed class GallerySidebar : PPView
    {
        public GallerySidebar() => Observe(Tab);

        protected override UIElement Build()
        {
            var col = UI.VStack(M.Xxs);
            col.Align = CrossAlign.Stretch;
            col.Margin = new Thickness(M.Md, M.Xs, M.Xs, M.Md);
            col.Children.Add(UI.HStack(0, UI.Text(L.T("sidebar.workspaces"), Typo.Caption, ColorToken.TextTertiary), UI.Spacer(),
                new PPIconButton("plus", L.T("workspace.new"), size: M.IconButtonSmall, iconSize: M.IconSmall)).Margins(M.Md, M.Xs, 0, M.Xxs));
            foreach (var (tab, icon) in new[] { ("controls", "sliders-horizontal"), ("display", "layout-grid"), ("overlays", "layers"), ("icons", "image") })
            {
                bool sel = Tab.Value == tab;
                var row = new PPRow(UI.HStack(M.Sm,
                        new PPIcon(icon, M.Icon, sel ? ColorToken.Accent : ColorToken.IconSecondary),
                        UI.Text(tab, sel ? Typo.BodyStrong : Typo.Body),
                        UI.Spacer(),
                        UI.Text(tab.Length.ToString(), Typo.Caption, ColorToken.TextTertiary)),
                    selected: sel, action: () => Tab.Value = tab).Id("gallery.tab." + tab);
                col.Children.Add(row);
            }
            col.Children.Add(new PPDivider(subtle: true).Margins(M.Md, M.Sm, M.Md, M.Sm));
            col.Children.Add(new PPRow(UI.HStack(M.Sm, new PPIcon("wrench", M.Icon, ColorToken.IconSecondary), UI.Text(L.T("sidebar.toolchains")))));
            col.Children.Add(new PPRow(UI.HStack(M.Sm, new PPIcon("puzzle", M.Icon, ColorToken.IconSecondary), UI.Text(L.T("sidebar.plugins")))));
            col.Children.Add(new PPRow(UI.HStack(M.Sm, new PPIcon("settings", M.Icon, ColorToken.IconSecondary), UI.Text(L.T("sidebar.settings")))));
            return col;
        }
    }

    private sealed class GalleryPage : PPView
    {
        public GalleryPage() => Observe(Tab);

        protected override UIElement Build()
        {
            var body = Tab.Value switch
            {
                "display" => Display(),
                "overlays" => Overlays(),
                "icons" => Icons(),
                _ => Controls(),
            };
            var col = UI.VStack(0, new PPPageHeader("Gallery · " + Tab.Value, Fonts(), new PPButton(L.T("common.copy"), () => { }, icon: "copy", size: PPButtonSize.Small)),
                new PPDivider(subtle: true), UI.Scroll(body.Pad(M.Xl).Id("gallery.body")).Key("gallery.scroll").Flex());
            col.Align = CrossAlign.Stretch;
            return col;
        }

        private static string Fonts() => UIFonts.UIName + " / " + UIFonts.MonoName + " · " + (Theme.IsDark ? "dark" : "light");
    }

    // MARK: 小工具

    private static Border Section(string title, params object?[] rows)
    {
        var col = UI.VStack(M.Md, UI.Text(title, Typo.Title));
        foreach (var r in UI.Flatten(rows)) col.Children.Add(r);
        return UI.Box(col).Pad(0, 0, 0, M.Xxl);
    }

    private static Stack Row(string label, params object?[] items)
    {
        var r = UI.HStack(M.Md, UI.Text(label, Typo.Small, ColorToken.TextTertiary).W(96).Shrink(false));
        foreach (var e in UI.Flatten(items)) r.Children.Add(e);
        return r;
    }

    private static Stack Labeled(string label, UIElement el)
    {
        var c = UI.VStack(M.Xs, el, UI.Text(label, Typo.Caption, ColorToken.TextTertiary));
        return c;
    }

    // MARK: 控件

    private static Stack Controls()
    {
        var kinds = new[] { PPButtonKind.Primary, PPButtonKind.Secondary, PPButtonKind.Ghost, PPButtonKind.Danger, PPButtonKind.Link };
        (string Title, string? Icon) Sample(PPButtonKind k) => k switch
        {
            PPButtonKind.Primary => (L.T("action.run"), "play"),
            PPButtonKind.Secondary => (L.T("common.cancel"), null),
            PPButtonKind.Ghost => (L.T("action.restart"), "rotate-cw"),
            PPButtonKind.Danger => (L.T("common.delete"), "trash-2"),
            _ => (L.T("card.open"), null),
        };
        var text = new State<string>("vrtalk-api");
        var empty = new State<string>("");
        var bad = new State<string>("8o80");
        var secret = new State<string>("hunter2");
        var area = new State<string>("JAVA_OPTS=-Xmx512m\nSPRING_PROFILES_ACTIVE=dev");
        double? num = 12;
        var sel = new State<string>("app");
        var opts = new[]
        {
            new DropdownOption<string>("app", "Application", icon: "play", detail: "Spring Boot"),
            new DropdownOption<string>("test", "Unit Tests", icon: "list-checks"),
            new DropdownOption<string>("docs", "Docs Server", icon: "globe", detail: ":3000"),
        };
        var on = new State<bool>(true);
        var off = new State<bool>(false);
        var seg = new State<string>("list");
        var tab = new State<string>("overview");

        return UI.VStack(0,
            Section("PPButton",
                Row("regular", kinds.Select(k => new PPButton(Sample(k).Title, () => { }, icon: Sample(k).Icon, kind: k).Id("btn." + k))),
                Row("small", kinds.Select(k => new PPButton(Sample(k).Title, () => { }, icon: Sample(k).Icon, kind: k, size: PPButtonSize.Small))),
                Row("hover", new PPButton(L.T("action.run"), icon: "play", kind: PPButtonKind.Primary).Force("hover"), new PPButton(L.T("common.cancel")).Force("hover"),
                    new PPButton(L.T("action.restart"), icon: "rotate-cw", kind: PPButtonKind.Ghost).Force("hover"), new PPButton(L.T("common.delete"), kind: PPButtonKind.Danger).Force("hover"),
                    new PPButton(L.T("card.open"), kind: PPButtonKind.Link).Force("hover")),
                Row("pressed", new PPButton(L.T("action.run"), icon: "play", kind: PPButtonKind.Primary).Force("pressed"), new PPButton(L.T("common.cancel")).Force("pressed"),
                    new PPButton(L.T("action.restart"), icon: "rotate-cw", kind: PPButtonKind.Ghost).Force("pressed")),
                Row("focus", new PPButton(L.T("action.run"), icon: "play", kind: PPButtonKind.Primary).Force("focus"), new PPButton(L.T("common.cancel")).Force("focus")),
                Row("disabled", kinds.Take(4).Select(k => new PPButton(Sample(k).Title, icon: Sample(k).Icon, kind: k).Disabled())),
                Row("busy", new PPButton(L.T("action.run"), kind: PPButtonKind.Primary, busy: true), new PPButton(L.T("common.save"), busy: true, size: PPButtonSize.Small)),
                Row("menu", new PPButtonMenu(L.T("toolbar.import"), () => new[] { MenuEntry.Item(L.T("import.folder"), () => { }, icon: "folder-input") }, icon: "plus", kind: PPButtonKind.Primary),
                    new PPButtonMenu(L.T("common.more"), () => new[] { MenuEntry.Item(L.T("common.copy"), () => { }, icon: "copy") }, size: PPButtonSize.Small)),
                Row("full width", new PPButton(L.T("common.continue"), kind: PPButtonKind.Primary, fullWidth: true).W(320))),
            Section("PPIconButton · PPLink · PPToolbarGroup",
                Row("28", new PPIconButton("play", L.T("action.run")), new PPIconButton("refresh-cw", L.T("action.restart")).Force("hover"),
                    new PPIconButton("square", L.T("action.stop"), tint: ColorToken.Danger), new PPIconButton("wrap-text", L.T("console.wrap"), active: true),
                    new PPIconButton("copy", L.T("common.copy")).Force("pressed"), new PPIconButton("trash-2", L.T("common.delete")).Disabled(),
                    new PPIconButton("x", busy: true), new PPIconButton("settings", L.T("sidebar.settings")).Force("focus")),
                Row("22", new PPIconButton("play", L.T("action.run"), size: M.IconButtonSmall, iconSize: M.IconSmall),
                    new PPIconButton("rotate-cw", L.T("action.restart"), size: M.IconButtonSmall, iconSize: M.IconSmall).Force("hover"),
                    new PPIconButton("square", L.T("action.stop"), size: M.IconButtonSmall, iconSize: M.IconSmall, tint: ColorToken.Danger),
                    new PPIconButton("ellipsis", L.T("common.more"), size: M.IconButtonSmall, iconSize: M.IconSmall)),
                Row("group", PPToolbarGroup.Of(new PPIconButton("search", L.T("console.search")), new PPIconButton("wrap-text", L.T("console.wrap"), active: true),
                    new PPIconButton("arrow-down-to-line", L.T("console.follow")), new PPIconButton("copy", L.T("console.copy")), new PPIconButton("eraser", L.T("console.clear")))),
                Row("link", new PPLink("localhost:8080", mono: true, icon: "external-link"), new PPLink("http://localhost:5173/").Force("hover"),
                    new PPLink(@"C:\Users\me\Code\shop\shop-api\src\main\resources\application-dev.yml", mono: true).W(260))),
            Section("PPTextField · PPTextArea · PPNumberField",
                Row("text", new PPTextField(L.T("common.filter"), empty, icon: "search", clearable: true).W(220),
                    new PPTextField(L.T("common.name"), text, clearable: true).W(200).Id("gallery.textfield"),
                    new PPTextField(L.T("common.name"), text).W(160).Force("focus")),
                Row("state", new PPTextField("8080", bad, mono: true, invalid: true).W(120), new PPTextField(L.T("common.name"), text).W(160).Disabled(),
                    new PPTextField("", secret, secure: true).W(140), PPNumberField.Make("", Bind.Of(() => num, v => num = v)).W(100)),
                Row("area", new PPTextArea(area, mono: true).W(360), new PPTextArea(empty, L.T("common.name")).W(220))),
            Section("PPDropdown",
                Row("regular", new PPDropdown<string>(opts, sel).W(200).Id("gallery.dropdown"), new PPDropdown<string>(opts, new State<string>("none"), L.T("card.noConfig")).W(200),
                    new PPDropdown<string>(opts, sel).W(160).Force("open")),
                Row("small", new PPDropdown<string>(opts, sel, size: PPButtonSize.Small).W(160), new PPDropdown<string>(opts, sel, size: PPButtonSize.Small, borderless: true),
                    new PPDropdown<string>(opts, sel).W(160).Disabled())),
            Section("PPSwitch · PPCheckbox · PPRadio · PPSegmented · PPTabBar",
                Row("switch", new PPSwitch(on), new PPSwitch(off), new PPSwitch(new State<bool>(true)).Disabled(), new PPSwitch(new State<bool>(false), "LTS").Force("focus")),
                Row("checkbox", new PPCheckbox(on, "LTS"), new PPCheckbox(off, L.T("console.wrap")), new PPCheckbox(new State<bool>(true), L.T("console.follow")).Disabled(),
                    new PPCheckbox(new State<bool>(false), "focus").Force("focus")),
                Row("radio", new PPRadio(true, "Gradle"), new PPRadio(false, "Maven"), new PPRadio(false, "Ant").Disabled()),
                Row("segmented", new PPSegmented<string>(new[] { ("list", "List"), ("tree", "Tree"), ("graph", "Graph") }, seg),
                    new PPSegmented<string>(new[] { ("default", "Default"), ("memory", "Memory") }, new State<string>("memory"), PPButtonSize.Small)),
                UI.Box(new PPTabBar<string>(new[]
                {
                    new PPTab<string>("overview", "Overview", "layout-grid"),
                    new PPTab<string>("run", "Run", "play", 2),
                    new PPTab<string>("files", "Files", "folder"),
                    new PPTab<string>("vcs", "Git", "git-branch", 12),
                    new PPTab<string>("config", "Config", "settings-2"),
                }, tab)).Pad(M.Md, 0).Bg(ColorToken.BgContent).Outline(ColorToken.BorderSubtle)));
    }

    // MARK: 展示

    private static Stack Display()
    {
        var tones = new[] { "neutral", "info", "success", "warning", "danger" };
        var states = new[] { "idle", "preparing", "starting", "running", "stopping", "failed", "exited", "partial" };
        double split = 200;
        var collapsedA = new State<bool>(false);
        var collapsedB = new State<bool>(true);
        return UI.VStack(0,
            Section("PPBadge · PPCountBadge · FacetBadgeView",
                Row("badge", tones.Select(t => new PPBadge(t, tone: t)), new PPBadge("LTS", tone: "success", icon: "badge-check"), new PPBadge("17.0.12", mono: true)),
                Row("count", new PPCountBadge(3), new PPCountBadge(12, "danger"), new PPCountBadge(128, "info"), new PPCountBadge(7, "warning")),
                Row("facet", new FacetBadgeView("Gradle", "hammer"), new FacetBadgeView("Spring Boot", "builtin:zap"), new FacetBadgeView("Java", "plugin:java/icons/java.svg"),
                    new FacetBadgeView("Node.js", null))),
            Section("StatusDot · PPSpinner · PPProgressBar",
                Row("status", states.Select(s => UI.HStack(M.Xs, new StatusDot(s), UI.Text(RunStatusStyle.Title(s), Typo.Small, ColorToken.TextSecondary)))),
                Row("spinner", new PPSpinner(M.IconSmall), new PPSpinner(), new PPSpinner(M.IconLarge, ColorToken.Accent), new PPSpinner(M.IconEmpty, ColorToken.IconSecondary)),
                Row("progress", Labeled("0", new PPProgressBar(0).W(120)), Labeled("45", new PPProgressBar(45).W(120)), Labeled("100", new PPProgressBar(100).W(120)),
                    Labeled("-1", new PPProgressBar(-1).W(120)), Labeled("warning", new PPProgressBar(70, tone: "warning").W(120)))),
            Section("Typo",
                new[] { Typo.Caption, Typo.Small, Typo.Body, Typo.BodyStrong, Typo.Label, Typo.Title, Typo.Heading, Typo.Display, Typo.Stat, Typo.Mono, Typo.MonoSmall }
                    .Select(t => Row(t.Name, UI.Text($"{t.Size:0}/{t.Weight.ToOpenTypeWeight()}/{t.LineHeight:0}", Typo.MonoSmall, ColorToken.TextTertiary).W(70),
                        UI.Text(L.T("app.name") + " Lemon Paperplane 0123456789 ~/Code/vrtalk", t)))),
            Section("PPKeyValueList · PPAlert",
                new PPKeyValueList(new[]
                {
                    new PPKeyValueRow(L.T("common.name"), "vrtalk-api"),
                    new PPKeyValueRow("Port", "8080", mono: true),
                    new PPKeyValueRow("URL", "http://localhost:8080/", mono: true, href: "http://localhost:8080/"),
                    new PPKeyValueRow(L.T("status.running"), RunStatusStyle.Title("running"), tone: "success"),
                    new PPKeyValueRow("JAVA_HOME", @"C:\Users\me\AppData\Roaming\LemonPaperplane\toolchains\jdk\temurin-17.0.12+7\bin\java.exe --add-opens java.base/java.lang=ALL-UNNAMED", mono: true),
                }).W(560),
                new PPAlert("danger", L.T("action.run.failed"), "Port 8080 is already in use by java.exe (PID 1234).", new PPButton(L.T("action.restart"), size: PPButtonSize.Small, kind: PPButtonKind.Primary)).W(560),
                new PPAlert("warning", L.T("app.noPlugins")).W(560),
                new PPAlert("success", L.T("common.saved"), L.T("common.copied")).W(560),
                new PPAlert("info", L.T("connection.reconnecting"), L.T("connection.connecting")).W(560)),
            Section("PPCard · PPRow · PPSection",
                Row("card", Card(false, false).W(300), Card(true, false).Force("hover").W(300)),
                Row("selected", Card(false, true).W(300)),
                Row("row", UI.VStack(M.Xxs,
                        new PPRow(UI.HStack(M.Sm, new StatusDot("running"), UI.Text("Application"), UI.Spacer(), UI.Text("8080", Typo.MonoSmall, ColorToken.TextTertiary))),
                        new PPRow(UI.HStack(M.Sm, new StatusDot("idle"), UI.Text("Unit Tests"))).Force("hover"),
                        new PPRow(UI.HStack(M.Sm, new StatusDot("failed"), UI.Text("Docs Server", Typo.BodyStrong)), selected: true)).W(280)),
                new PPSection("Modules", UI.Paragraph("vrtalk-api · vrtalk-common · vrtalk-admin", Typo.Body, ColorToken.TextSecondary), info: L.T("config.env.info"),
                    accessory: new PPButton(L.T("common.create"), icon: "plus", size: PPButtonSize.Small, kind: PPButtonKind.Ghost), collapsible: true, collapsedBind: collapsedA).W(560),
                new PPSection(L.T("config.env"), UI.Text("hidden"), collapsible: true, collapsedBind: collapsedB).W(560)),
            Section("PPPageHeader · PPEmptyState · PPSplitView · FlowRow · PPDivider",
                UI.Box(new PPPageHeader(L.T("sidebar.toolchains"), "3", new PPButton(L.T("common.create"), icon: "plus", kind: PPButtonKind.Primary))).Outline(ColorToken.BorderSubtle).W(560),
                UI.Box(new PPEmptyState("folder-open", L.T("import.scanEmpty"), L.T("import.folder"), () => { }, "folder-input")).H(220).W(560).Outline(ColorToken.BorderSubtle).Rounded(M.RadiusLG),
                UI.Box(new PPSplitView(UI.Box(UI.Text("left")).Pad(M.Md), UI.Box(UI.Text("right")).Pad(M.Md), Bind.Of(() => split, v => split = v))).H(80).W(560).Outline(ColorToken.BorderSubtle),
                UI.Flow(M.Xs, Enumerable.Range(0, 14).Select(i => (object)new PPBadge("facet-" + i * 7, tone: tones[i % 5]))).W(420),
                new PPDivider().W(560), new PPDivider(subtle: true).W(560)),
            Section("Color tokens", UI.Flow(M.Xs, ColorToken.All.Select(k => (object)Labeled(k, new Border { Width = 96, Height = 24, CornerRadius = new CornerRadius(M.RadiusSM) }.Bg(k).Outline(ColorToken.BorderSubtle)))).W(1000)));
    }

    private static PPCard Card(bool hoverable, bool selected)
    {
        var more = new PPIconButton("ellipsis", L.T("common.more"), size: M.IconButtonSmall, iconSize: M.IconSmall);
        var content = UI.VStack(M.Sm,
            UI.HStack(M.Sm, new StatusDot("running"), UI.Text("vrtalk-api", Typo.Title), UI.Spacer(), new PPBadge("2", tone: "warning", icon: "triangle-alert"), more),
            UI.TextMiddle(@"~\Code\vrtalk\vrtalk-api\modules\server", Typo.Small, ColorToken.TextTertiary),
            UI.Flow(M.Xs, new FacetBadgeView("Gradle", "hammer"), new FacetBadgeView("Spring Boot", "zap"), new FacetBadgeView("Java", "coffee")),
            new PPDivider(subtle: true),
            UI.HStack(M.Xs, new PPDropdown<string>(new[] { new DropdownOption<string>("app", "Application") }, new State<string>("app"), size: PPButtonSize.Small).W(150), UI.Spacer(),
                new PPIconButton("rotate-cw", L.T("action.restart"), size: M.IconButtonSmall, iconSize: M.IconSmall),
                new PPIconButton("square", L.T("action.stop"), size: M.IconButtonSmall, iconSize: M.IconSmall, tint: ColorToken.Danger)),
            UI.HStack(M.Md, new PPLink("localhost:8080", mono: true, icon: "arrow-up-right"), UI.Spacer(),
                UI.Text("3.5%", Typo.MonoSmall, ColorToken.TextSecondary), UI.Text("512 MB", Typo.MonoSmall, ColorToken.TextSecondary)));
        content.Align = CrossAlign.Stretch;
        return new PPCard(content, hoverable: hoverable, selected: selected);
    }

    // MARK: 弹出层

    private static Stack Overlays()
    {
        var many = Enumerable.Range(1, 20).Select(i => new DropdownOption<string>("v" + i, "Option " + i, detail: i % 3 == 0 ? "LTS" : null)).ToList();
        var pick = new State<string>("v3");
        var popoverOpen = new State<bool>(false);
        var anchor = new PPButton("Popover", () => popoverOpen.Value = !popoverOpen.Value, icon: "info").Id("gallery.popover");
        anchor.PPPopover(popoverOpen, () => new PopoverContent(new PPKeyValueList(new[]
        {
            new PPKeyValueRow("PID", "1234", mono: true),
            new PPKeyValueRow("CPU", "3.5%", mono: true),
            new PPKeyValueRow(L.T("status.running"), "2m 5s"),
        }, 80), 260), PopupAlignment.Leading);

        var contextArea = UI.Box(UI.Text("Right-click", Typo.Body, ColorToken.TextSecondary).Center()).Size(240, 64).Rounded(M.RadiusLG)
            .Bg(ColorToken.BgSubtle).Outline(ColorToken.Border).Id("gallery.context")
            .PPContextMenu(MenuEntries);

        return UI.VStack(0,
            Section("PPMenuButton · PPContextMenu",
                Row("menu", new PPMenuButton(MenuEntries, UI.Box(new PPIcon("ellipsis").Center()).Size(M.IconButton, M.IconButton).Rounded(M.RadiusMD).Bg(ColorToken.BgHover), help: L.T("common.more")).Id("gallery.menu"),
                    new PPButtonMenu(L.T("toolbar.import"), MenuEntries, icon: "plus", kind: PPButtonKind.Primary).Id("gallery.menu2"), contextArea)),
            Section("Popover · PPInfoButton · PPDropdown(>12)",
                Row("popover", anchor, UI.HStack(M.Xs, UI.Text(L.T("config.env")), new PPInfoButton(L.T("config.env.info")).Id("gallery.info")),
                    new PPDropdown<string>(many, pick).W(180).Id("gallery.dropdown20"))),
            Section("Dialogs",
                Row("dialog",
                    new PPButton("Confirm", () => Dialogs.Confirm(L.T("card.stopAll"), L.T("deps.cleanInstall.confirm"), async () => await Task.Delay(400))).Id("gallery.confirm"),
                    new PPButton("Danger", () => Dialogs.Confirm(L.T("common.delete") + " vrtalk-api?", L.T("deps.cleanInstall.confirm"),
                        async () => { await Task.Delay(300); throw new InvalidOperationException("Port 8080 is already in use"); }, ok: L.T("common.delete"), danger: true), kind: PPButtonKind.Danger).Id("gallery.danger"),
                    new PPButton("Input", () => Dialogs.Input(L.T("workspace.new"), L.T("common.name"), async name => { await Task.Delay(200); ToastStore.Shared.Show("success", L.T("common.saved"), name); }, "Workspace 2")).Id("gallery.input"),
                    new PPButton("Form shell", ShowForm).Id("gallery.form"))),
            Section("Toasts",
                Row("toast",
                    new PPButton("info", () => ToastStore.Shared.Show("info", L.T("connection.reconnecting"))).Id("gallery.toast.info"),
                    new PPButton("success", () => ToastStore.Shared.Show("success", L.T("common.copied"))).Id("gallery.toast.success"),
                    new PPButton("warning", () => ToastStore.Shared.Show("warning", L.T("app.noPlugins"), "plugins: 0")).Id("gallery.toast.warning"),
                    new PPButton("error", () => ToastStore.Shared.Show("error", L.T("action.run.failed"), "Port 8080 is already in use by java.exe (PID 1234).\nexit code 1", L.T("console.openLog"), () => { })).Id("gallery.toast.error"))));
    }

    private static IEnumerable<MenuEntry> MenuEntries() => new[]
    {
        MenuEntry.Header("vrtalk-api"),
        MenuEntry.Item(L.T("card.open"), () => ToastStore.Shared.Show("info", L.T("card.open")), icon: "square-arrow-out-up-right", shortcut: "Enter"),
        MenuEntry.Item(L.T("common.copy"), () => { }, icon: "copy", shortcut: "Ctrl+C"),
        MenuEntry.Item(L.T("console.wrap"), () => { }, @checked: true),
        MenuEntry.Item(L.T("console.follow"), () => { }, icon: "arrow-down-to-line", disabled: true),
        MenuEntry.Divider,
        MenuEntry.Submenu(L.T("card.moveTo"), new[]
        {
            MenuEntry.Item("Default", () => { }, @checked: true),
            MenuEntry.Item("Client B", () => { }),
            MenuEntry.Item("Archive", () => { }, icon: "archive"),
        }, icon: "folder-input"),
        MenuEntry.Divider,
        MenuEntry.Item(L.T("common.remove"), () => { }, icon: "trash-2", destructive: true),
    };

    private static void ShowForm()
    {
        var id = Guid.NewGuid().ToString("N");
        var name = new State<string>("");
        var lts = new State<bool>(true);
        PPTextField? field = null;
        Dialogs.Present(id, new FormDialogShell(id, L.T("workspace.new"), null,
            () =>
            {
                field = new PPTextField(L.T("common.name"), name).Id("form.name");
                var g = UI.Grid($"{M.FormLabelWidth},*", "Auto,Auto",
                    UI.Text(L.T("common.name"), Typo.Body).VCenter().At(0, 0), field.FillW().At(0, 1),
                    UI.Gap(0).At(1, 0), new PPCheckbox(lts, "LTS").Margins(0, M.Md, 0, 0).At(1, 1));
                return g;
            },
            () =>
            {
                bool ok = name.Value.Trim().Length > 0;
                if (field != null) field.Invalid = !ok;
                return ok;
            },
            async () => { await Task.Delay(200); ToastStore.Shared.Show("success", L.T("common.saved"), name.Value); }));
    }

    // MARK: 图标

    private static Stack Icons()
    {
        var names = SharedAssets.Files("icons", ".svg").Select(f => f.Substring(0, f.Length - 4)).ToList();
        var grid = UI.Flow(0, names.Select(n => (object)UI.Box(new PPIcon(n).Center()).Size(32, 32).Tip(n).Id("icon." + n)));
        var sizes = new[] { 14.0, 16, 20, 24, 32 };
        var sample = new[] { "folder", "play", "git-branch", "settings", "circle-alert", "loader-circle" };
        var sizeRows = sizes.Select(s => Row(s.ToString("0") + "px", sample.Select(n => (object)new PPIcon(n, s))));
        var app = UI.HStack(M.Lg, new[] { 16, 24, 32, 48, 64, 128 }.Select(px =>
            (object)Labeled(px.ToString(), new Image { Source = IconStore.Shared.AppIconBitmap(px), Width = px, Height = px, SnapsToDevicePixels = true })));
        var tray = UI.HStack(M.Lg,
            Labeled("tray light", UI.Box(new Image { Source = IconStore.Shared.TrayBitmap(16, Theme.Color(ColorToken.TextPrimary, dark: false)), Width = 16, Height = 16 }).Pad(M.Sm).Background_(Colors.White)),
            Labeled("tray dark", UI.Box(new Image { Source = IconStore.Shared.TrayBitmap(16, Colors.White), Width = 16, Height = 16 }).Pad(M.Sm).Background_(Color.FromRgb(0x20, 0x20, 0x20))),
            Labeled("tray 32", UI.Box(new Image { Source = IconStore.Shared.TrayBitmap(32, Colors.White), Width = 32, Height = 32 }).Pad(M.Sm).Background_(Color.FromRgb(0x20, 0x20, 0x20))));
        return UI.VStack(0,
            Section("shared/icons (" + names.Count + ")", grid.W(960)),
            Section("strokeScale", sizeRows),
            Section("app-icon.svg · menubar.svg", app, tray));
    }

    /// <summary>陈列页里画品牌图标底色用（托盘预览需要固定的任务栏颜色，不跟主题）。</summary>
    private static Border Background_(this Border b, Color c)
    {
        var br = new SolidColorBrush(c);
        br.Freeze();
        b.Background = br;
        return b;
    }
}
