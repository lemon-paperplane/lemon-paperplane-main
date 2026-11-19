import Foundation

// docs/api.md 第 5 节全部操作的类型化封装。界面代码优先用这里的方法；
// 插件贡献的工具（<pluginId>.<tool>）用通用的 call(op, input)。

public extension APIClient {
    // MARK: 系统与设置

    func systemInfo(timeout: TimeInterval? = nil) async throws -> SystemInfo {
        try await call("system.info", timeout: timeout)
    }

    func shutdown(stopRuns: Bool) async throws {
        try await send("system.shutdown", ["stopRuns": .bool(stopRuns)], timeout: 10)
    }

    func settings() async throws -> AppSettings { try await call("settings.get") }

    /// JSON merge：只传要改的字段
    func updateSettings(_ patch: JSONValue) async throws -> AppSettings {
        try await call("settings.update", patch)
    }

    func mirrorsStatus() async throws -> MirrorStatus { try await call("mirrors.status") }
    func mirrorsProbe() async throws -> MirrorStatus { try await call("mirrors.probe", timeout: 60) }
    func contributions() async throws -> Contributions { try await call("ui.contributions") }

    func respondPrompt(id: String, cancelled: Bool, values: [String: JSONValue]) async throws {
        try await send("ui.respond", ["promptId": .string(id), "cancelled": .bool(cancelled), "values": .object(values)])
    }

    func pendingPrompts() async throws -> [Prompt] { try await call("ui.prompts") }

    func installCLI(dir: String? = nil) async throws -> PathResult {
        try await call("cli.install", jsonObject(["dir": dir.map(JSONValue.string)]))
    }

    func mcpConfig() async throws -> MCPConfig { try await call("mcp.config") }

    // MARK: 工作区

    func workspaces() async throws -> [Workspace] { try await call("workspace.list") }

    func createWorkspace(name: String) async throws -> Workspace {
        try await call("workspace.create", ["name": .string(name)])
    }

    func renameWorkspace(_ id: String, name: String) async throws -> Workspace {
        try await call("workspace.rename", ["workspaceId": .string(id), "name": .string(name)])
    }

    func deleteWorkspace(_ id: String, moveTo: String? = nil) async throws {
        try await send("workspace.delete", jsonObject(["workspaceId": .string(id), "moveTo": moveTo.map(JSONValue.string)]))
    }

    func reorderWorkspaces(_ ids: [String]) async throws {
        try await send("workspace.reorder", ["workspaceIds": JSONValue(ids)])
    }

    // MARK: 工程

    func projects(workspaceId: String? = nil) async throws -> [ProjectSummary] {
        try await call("project.list", jsonObject(["workspaceId": workspaceId.map(JSONValue.string)]))
    }

    func project(_ id: String) async throws -> Project {
        try await call("project.get", ["projectId": .string(id)])
    }

    func scan(path: String, maxDepth: Int? = nil) async throws -> [ScanEntry] {
        try await call("project.scan", jsonObject(["path": .string(path), "maxDepth": maxDepth.map { .number(Double($0)) }]), timeout: 120)
    }

    func importProject(path: String, workspaceId: String, name: String? = nil) async throws -> Project {
        try await call("project.import", jsonObject([
            "path": .string(path), "workspaceId": .string(workspaceId), "name": name.map(JSONValue.string),
        ]), timeout: 600)
    }

    func importProjects(paths: [String], workspaceId: String) async throws -> [Project] {
        try await call("project.importMany", ["paths": JSONValue(paths), "workspaceId": .string(workspaceId)], timeout: 1800)
    }

    func removeProject(_ id: String) async throws {
        try await send("project.remove", ["projectId": .string(id)])
    }

    func renameProject(_ id: String, name: String) async throws -> ProjectSummary {
        try await call("project.rename", ["projectId": .string(id), "name": .string(name)])
    }

    func moveProject(_ id: String, to workspaceId: String, index: Int? = nil) async throws {
        try await send("project.move", jsonObject([
            "projectId": .string(id), "workspaceId": .string(workspaceId), "index": index.map { .number(Double($0)) },
        ]))
    }

    func reorderProjects(workspaceId: String, projectIds: [String]) async throws {
        try await send("project.reorder", ["workspaceId": .string(workspaceId), "projectIds": JSONValue(projectIds)])
    }

    func reimport(_ id: String) async throws -> TaskInfo {
        try await call("project.reimport", ["projectId": .string(id)])
    }

    func sync(_ id: String, moduleId: String? = nil) async throws -> TaskInfo {
        try await call("project.sync", jsonObject(["projectId": .string(id), "moduleId": moduleId.map(JSONValue.string)]))
    }

    /// env：整体替换工程环境变量；toolchains：kind → {id}/{constraint}，null 表示清除覆盖
    func updateProjectSettings(_ id: String, env: [String: String]? = nil,
                               toolchains: [String: ToolchainOverride?]? = nil) async throws -> Project {
        var input: [String: JSONValue] = ["projectId": .string(id)]
        if let env { input["env"] = JSONValue(env) }
        if let toolchains {
            var o: [String: JSONValue] = [:]
            for (k, v) in toolchains { o[k] = try v.map { try JSONValue(encoding: $0) } ?? .null }
            input["toolchains"] = .object(o)
        }
        return try await call("project.settings", .object(input))
    }

    /// with：finder | terminal | idea | vscode | default
    func openProject(_ id: String, moduleId: String? = nil, with app: String) async throws {
        try await send("project.open", jsonObject([
            "projectId": .string(id), "moduleId": moduleId.map(JSONValue.string), "with": .string(app),
        ]))
    }

    func problems(_ id: String) async throws -> [Diagnosis] {
        try await call("project.problems", ["projectId": .string(id)])
    }

    /// runId：挂在 run.toolbar 的动作触发时所在的运行
    func invokeAction(projectId: String, moduleId: String, plugin: String, action: String,
                      args: [String: JSONValue]? = nil, runId: String? = nil) async throws -> TaskInfo {
        try await call("project.action", jsonObject([
            "projectId": .string(projectId), "moduleId": .string(moduleId), "plugin": .string(plugin),
            "action": .string(action), "args": args.map(JSONValue.object), "runId": runId.map(JSONValue.string),
        ]))
    }

    func installDependencies(projectId: String, moduleId: String? = nil, clean: Bool = false) async throws -> TaskInfo {
        try await call("deps.install", jsonObject([
            "projectId": .string(projectId), "moduleId": moduleId.map(JSONValue.string), "clean": clean ? .bool(true) : nil,
        ]))
    }

    // MARK: 运行配置

    func runConfigTypes(projectId: String, moduleId: String) async throws -> [RunConfigTypeChoice] {
        try await call("runconfig.types", ["projectId": .string(projectId), "moduleId": .string(moduleId)])
    }

    func createRunConfig(projectId: String, moduleId: String, type: String, name: String,
                         options: [String: JSONValue], env: [String: String]) async throws -> RunConfig {
        try await call("runconfig.create", [
            "projectId": .string(projectId), "moduleId": .string(moduleId), "type": .string(type),
            "name": .string(name), "options": .object(options), "env": JSONValue(env),
        ])
    }

    func updateRunConfig(projectId: String, configId: String, name: String? = nil,
                         options: [String: JSONValue]? = nil, env: [String: String]? = nil) async throws -> RunConfig {
        try await call("runconfig.update", jsonObject([
            "projectId": .string(projectId), "configId": .string(configId), "name": name.map(JSONValue.string),
            "options": options.map(JSONValue.object), "env": env.map { JSONValue($0) },
        ]))
    }

    func deleteRunConfig(projectId: String, configId: String) async throws {
        try await send("runconfig.delete", ["projectId": .string(projectId), "configId": .string(configId)])
    }

    func resetRunConfig(projectId: String, configId: String) async throws -> RunConfig {
        try await call("runconfig.reset", ["projectId": .string(projectId), "configId": .string(configId)])
    }

    /// 实时取表单里 live 的下拉选项（如 ios.devices / android.devices）
    func liveOptions(projectId: String, moduleId: String?, keys: [String]) async throws -> [String: [Option]] {
        try await call("runconfig.options", jsonObject([
            "projectId": .string(projectId), "moduleId": moduleId.map(JSONValue.string), "keys": JSONValue(keys),
        ]), timeout: 30)
    }

    func setDefaultRunConfig(projectId: String, configId: String) async throws {
        try await send("runconfig.setDefault", ["projectId": .string(projectId), "configId": .string(configId)])
    }

    // MARK: 运行

    func startRun(projectId: String, configId: String? = nil) async throws -> Run {
        try await call("run.start", jsonObject(["projectId": .string(projectId), "configId": configId.map(JSONValue.string)]))
    }

    func stopRun(_ runId: String) async throws -> Run {
        try await call("run.stop", ["runId": .string(runId)])
    }

    func restartRun(_ runId: String) async throws -> Run {
        try await call("run.restart", ["runId": .string(runId)])
    }

    func restartRun(projectId: String, configId: String) async throws -> Run {
        try await call("run.restart", ["projectId": .string(projectId), "configId": .string(configId)])
    }

    // MARK: 启动面板

    @discardableResult
    func launchAdd(projectId: String, configIds: [String]) async throws -> LaunchIDs {
        try await call("launch.add", ["projectId": .string(projectId), "configIds": JSONValue(configIds)])
    }

    @discardableResult
    func launchRemove(projectId: String, configIds: [String]) async throws -> LaunchIDs {
        try await call("launch.remove", ["projectId": .string(projectId), "configIds": JSONValue(configIds)])
    }

    // MARK: 终端

    func terminalList(workspaceId: String? = nil) async throws -> [TerminalInfo] {
        var args: [String: JSONValue] = [:]
        if let workspaceId { args["workspaceId"] = .string(workspaceId) }
        return try await call("terminal.list", .object(args))
    }

    /// 新建终端：起始目录是工程根目录，名字默认是工程名
    func terminalCreate(projectId: String, cols: Int, rows: Int) async throws -> TerminalInfo {
        try await call("terminal.create", ["projectId": .string(projectId), "cols": .number(Double(cols)), "rows": .number(Double(rows))])
    }

    func terminalRename(id: String, name: String) async throws {
        try await send("terminal.rename", ["id": .string(id), "name": .string(name)])
    }

    func terminalRestart(id: String) async throws {
        try await send("terminal.restart", ["id": .string(id)])
    }

    /// 终端里是否有命令在前台运行（关闭前确认用）
    func terminalBusy(id: String) async throws -> TerminalBusy {
        try await call("terminal.busy", ["id": .string(id)])
    }

    func terminalClose(id: String) async throws {
        try await send("terminal.close", ["id": .string(id)])
    }

    /// 设置启动项别名（空字符串清除）
    func launchSetAlias(projectId: String, configId: String, alias: String) async throws {
        try await send("launch.setAlias", ["projectId": .string(projectId), "configId": .string(configId), "alias": .string(alias)])
    }

    /// 工程里能作为启动项运行的文件（脚本、程序；includeSources 时加上其他源码）
    func launchCandidates(projectId: String, includeSources: Bool) async throws -> RunnableList {
        try await call("launch.candidates", ["projectId": .string(projectId), "includeSources": .bool(includeSources)], timeout: 60)
    }

    /// 把工程里的脚本/可执行文件加到启动（同一文件已有运行配置就复用）
    func launchAddFile(projectId: String, path: String) async throws -> LaunchAddResult {
        try await call("launch.addFile", ["projectId": .string(projectId), "path": .string(path)])
    }

    /// 把一段自定义命令加到启动；cwd 为空表示工程根目录
    func launchAddCommand(projectId: String, name: String, command: String, cwd: String, shell: String? = nil) async throws -> LaunchAddResult {
        try await call("launch.addCommand", jsonObject([
            "projectId": .string(projectId), "name": .string(name), "command": .string(command), "cwd": .string(cwd),
            "shell": shell.map(JSONValue.string),
        ]))
    }

    func launchRun(workspaceId: String? = nil, projectId: String? = nil, configIds: [String]? = nil) async throws -> LaunchResult {
        try await call("launch.run", jsonObject([
            "workspaceId": workspaceId.map(JSONValue.string), "projectId": projectId.map(JSONValue.string),
            "configIds": configIds.map { JSONValue($0) },
        ]), timeout: 120)
    }

    func launchStop(workspaceId: String? = nil, projectId: String? = nil, configIds: [String]? = nil) async throws -> LaunchResult {
        try await call("launch.stop", jsonObject([
            "workspaceId": workspaceId.map(JSONValue.string), "projectId": projectId.map(JSONValue.string),
            "configIds": configIds.map { JSONValue($0) },
        ]))
    }

    func stopAll(workspaceId: String? = nil, projectId: String? = nil) async throws -> StopAllResult {
        try await call("run.stopAll", jsonObject([
            "workspaceId": workspaceId.map(JSONValue.string), "projectId": projectId.map(JSONValue.string),
        ]))
    }

    func runs(projectId: String? = nil, active: Bool? = nil) async throws -> [Run] {
        try await call("run.list", jsonObject(["projectId": projectId.map(JSONValue.string), "active": active.map(JSONValue.bool)]))
    }

    func run(_ id: String) async throws -> Run { try await call("run.get", ["runId": .string(id)]) }

    func logs(consoleId: String, tail: Int? = nil, since: Int64? = nil, grep: String? = nil) async throws -> LogsResult {
        try await call("run.logs", jsonObject([
            "consoleId": .string(consoleId), "tail": tail.map { .number(Double($0)) },
            "since": since.map { .number(Double($0)) }, "grep": grep.map(JSONValue.string),
        ]))
    }

    func runInput(_ runId: String, text: String) async throws {
        try await send("run.input", ["runId": .string(runId), "text": .string(text)])
    }

    func clearRun(_ runId: String) async throws {
        try await send("run.clear", ["runId": .string(runId)])
    }

    /// 运行中的诊断：传 runId（+ diagnosisId）；静态问题：传 projectId + diagnosisId
    func applyFix(runId: String? = nil, projectId: String? = nil, diagnosisId: String? = nil, fixId: String) async throws -> FixResult {
        try await call("fix.apply", jsonObject([
            "runId": runId.map(JSONValue.string), "projectId": projectId.map(JSONValue.string),
            "diagnosisId": diagnosisId.map(JSONValue.string), "fixId": .string(fixId),
        ]), timeout: 600)
    }

    func killPort(_ port: Int) async throws -> PortKillResult {
        try await call("port.kill", ["port": .number(Double(port))])
    }

    // MARK: 任务

    func tasks(active: Bool? = nil) async throws -> [TaskInfo] {
        try await call("task.list", jsonObject(["active": active.map(JSONValue.bool)]))
    }

    func task(_ id: String) async throws -> TaskInfo { try await call("task.get", ["taskId": .string(id)]) }

    func cancelTask(_ id: String) async throws { try await send("task.cancel", ["taskId": .string(id)]) }

    // MARK: 工具链

    func toolchainKinds() async throws -> [ToolchainKind] { try await call("toolchain.kinds") }

    func toolchains(kind: String? = nil) async throws -> [Toolchain] {
        try await call("toolchain.list", jsonObject(["kind": kind.map(JSONValue.string)]))
    }

    func availableToolchains(kind: String) async throws -> [AvailableVersion] {
        try await call("toolchain.available", ["kind": .string(kind)], timeout: 60)
    }

    func installToolchain(kind: String, version: String) async throws -> TaskInfo {
        try await call("toolchain.install", ["kind": .string(kind), "version": .string(version)])
    }

    func addToolchain(kind: String, path: String) async throws -> Toolchain {
        try await call("toolchain.add", ["kind": .string(kind), "path": .string(path)])
    }

    func removeToolchain(_ id: String) async throws {
        try await send("toolchain.remove", ["toolchainId": .string(id)])
    }

    func refreshToolchains() async throws { try await send("toolchain.refresh", timeout: 120) }

    // MARK: 插件

    func plugins() async throws -> [PluginInfo] { try await call("plugin.list") }

    func setPluginEnabled(_ id: String, _ enabled: Bool) async throws {
        try await send(enabled ? "plugin.enable" : "plugin.disable", ["pluginId": .string(id)])
    }

    func installPlugin(path: String) async throws -> PluginInfo {
        try await call("plugin.install", ["path": .string(path)], timeout: 120)
    }

    func uninstallPlugin(_ id: String) async throws {
        try await send("plugin.uninstall", ["pluginId": .string(id)])
    }

    func updatePluginSettings(_ id: String, values: [String: JSONValue]) async throws {
        try await send("plugin.settings", ["pluginId": .string(id), "values": .object(values)])
    }

    func store(refresh: Bool = false) async throws -> [StoreEntry] {
        try await call("store.list", jsonObject(["refresh": refresh ? .bool(true) : nil]), timeout: 60)
    }

    func installFromStore(_ id: String) async throws -> TaskInfo {
        try await call("store.install", ["pluginId": .string(id)])
    }

    // MARK: 插件界面

    func renderView(_ ctx: ViewContext, state: [String: JSONValue]?) async throws -> ViewRenderResult {
        var input = ctx.input
        if let state { input["state"] = .object(state) }
        return try await call("view.render", .object(input))
    }

    /// webview 视图：view.render 不调插件，直接返回 {"webviewUrl": "..."}（docs/api.md 第 6 节）
    func webviewURL(_ ctx: ViewContext) async throws -> URL? {
        let r: JSONValue = try await call("view.render", .object(ctx.input))
        // 远程服务器给的是它自己的回环地址与令牌：换成本客户端（网关）的
        return r["webviewUrl"]?.stringValue.flatMap { URL(string: $0) }.map(rebasedWebviewURL)
    }

    func viewEvent(_ ctx: ViewContext, event: String, values: [String: JSONValue],
                   state: [String: JSONValue]?) async throws -> ViewEventResult {
        var input = ctx.input
        input["event"] = .string(event)
        input["values"] = .object(values)
        if let state { input["state"] = .object(state) }
        return try await call("view.event", .object(input))
    }

    // MARK: 版本控制（vcs.<op>，Args 平铺进输入）

    func vcs<A: Encodable, T: Decodable>(_ op: String, projectId: String, root: String? = nil,
                                         args: A, as type: T.Type = T.self, timeout: TimeInterval? = nil) async throws -> T {
        var input: [String: JSONValue] = [:]
        if case .object(let o) = try JSONValue(encoding: args) { input = o }
        input["projectId"] = .string(projectId)
        if let root { input["root"] = .string(root) }
        return try await call("vcs.\(op)", .object(input), timeout: timeout ?? Self.vcsTimeout(op))
    }

    func vcs<T: Decodable>(_ op: String, projectId: String, root: String? = nil, as type: T.Type = T.self) async throws -> T {
        try await vcs(op, projectId: projectId, root: root, args: VCSNoArgs(), as: type)
    }

    /// 各操作的超时：远程操作、钩子、变基可能很久；读操作用默认值
    static func vcsTimeout(_ op: String) -> TimeInterval? {
        switch op {
        case VCSOp.push, VCSOp.pull, VCSOp.fetch, VCSOp.clone, VCSOp.submoduleUpdate, VCSOp.tagPush:
            return 3600
        case VCSOp.commit, VCSOp.merge, VCSOp.rebase, VCSOp.rebaseInteractive, VCSOp.continue, VCSOp.skip,
             VCSOp.cherryPick, VCSOp.revert, VCSOp.checkout, VCSOp.stashSave, VCSOp.stashApply, VCSOp.reword,
             VCSOp.branchDelete, VCSOp.tagCreate, VCSOp.tagDelete, VCSOp.mergeTool:
            return 900
        default:
            return nil
        }
    }

    /// vcs.overview：工作区内全部仓库及状态（workspaceId 为空表示全部工作区）
    func vcsOverview(workspaceId: String?) async throws -> [VCSRepoOverview] {
        try await call("vcs.overview", jsonObject(["workspaceId": workspaceId.map(JSONValue.string)]), timeout: 300)
    }

    func vcsRoots(projectId: String) async throws -> [VCSRoot] {
        try await vcs(VCSOp.roots, projectId: projectId)
    }

    func vcsStatus(projectId: String, root: String? = nil) async throws -> VCSStatus {
        try await vcs(VCSOp.status, projectId: projectId, root: root)
    }

    func vcsDiff(projectId: String, root: String? = nil, _ args: VCSDiffArgs) async throws -> VCSDiff {
        try await vcs(VCSOp.diff, projectId: projectId, root: root, args: args)
    }

    func vcsLog(projectId: String, root: String? = nil, _ args: VCSLogArgs) async throws -> VCSLog {
        try await vcs(VCSOp.log, projectId: projectId, root: root, args: args)
    }

    func vcsFileHistory(projectId: String, root: String? = nil, _ args: VCSFileHistoryArgs) async throws -> VCSLog {
        try await vcs(VCSOp.fileHistory, projectId: projectId, root: root, args: args)
    }

    func vcsShow(projectId: String, root: String? = nil, hash: String, parent: Int? = nil) async throws -> VCSCommitDetail {
        try await vcs(VCSOp.show, projectId: projectId, root: root, args: VCSShowArgs(hash: hash, parent: parent))
    }

    func vcsBranches(projectId: String, root: String? = nil) async throws -> VCSBranches {
        try await vcs(VCSOp.branches, projectId: projectId, root: root)
    }

    func vcsCommit(projectId: String, root: String? = nil, _ args: VCSCommitArgs) async throws -> VCSCommitResult {
        try await vcs(VCSOp.commit, projectId: projectId, root: root, args: args)
    }

    func vcsPush(projectId: String, root: String? = nil, _ args: VCSPushArgs) async throws -> VCSOpResult {
        try await vcs(VCSOp.push, projectId: projectId, root: root, args: args)
    }

    func vcsPull(projectId: String, root: String? = nil, _ args: VCSPullArgs) async throws -> VCSOpResult {
        try await vcs(VCSOp.pull, projectId: projectId, root: root, args: args)
    }

    func vcsFetch(projectId: String, root: String? = nil, _ args: VCSFetchArgs) async throws -> VCSOpResult {
        try await vcs(VCSOp.fetch, projectId: projectId, root: root, args: args)
    }

    func vcsPushPreview(projectId: String, root: String? = nil, _ args: VCSPushArgs) async throws -> VCSPushPreview {
        try await vcs(VCSOp.pushPreview, projectId: projectId, root: root, args: args)
    }

    func vcsStashList(projectId: String, root: String? = nil) async throws -> VCSStashList {
        try await vcs(VCSOp.stashList, projectId: projectId, root: root)
    }

    func vcsStashShow(projectId: String, root: String? = nil, index: Int) async throws -> VCSStashShow {
        try await vcs(VCSOp.stashShow, projectId: projectId, root: root, args: VCSStashArgs(index: index))
    }

    func vcsFileContent(projectId: String, root: String? = nil, _ args: VCSFileContentArgs) async throws -> VCSFileContent {
        try await vcs(VCSOp.fileContent, projectId: projectId, root: root, args: args)
    }

    func vcsContainingBranches(projectId: String, root: String? = nil, hash: String) async throws -> VCSContainingBranches {
        try await vcs(VCSOp.containingBranches, projectId: projectId, root: root, args: VCSShowArgs(hash: hash))
    }

    func vcsMergeTools(projectId: String, root: String? = nil) async throws -> VCSMergeTools {
        try await vcs(VCSOp.mergeTools, projectId: projectId, root: root)
    }

    func vcsCompare(projectId: String, root: String? = nil, _ args: VCSCompareArgs) async throws -> VCSCompare {
        try await vcs(VCSOp.compare, projectId: projectId, root: root, args: args)
    }

    func vcsConfigGet(projectId: String, root: String? = nil, keys: [String], global: Bool? = nil) async throws -> VCSConfig {
        try await vcs(VCSOp.configGet, projectId: projectId, root: root, args: VCSConfigGetArgs(keys: keys, global: global))
    }

    /// 写操作的通用形式：返回 VCSOpResult
    func vcsRun<A: Encodable>(_ op: String, projectId: String, root: String? = nil, args: A) async throws -> VCSOpResult {
        try await vcs(op, projectId: projectId, root: root, args: args)
    }

    /// project.clone：克隆并导入（同步等克隆完成；克隆输出在任务控制台）
    func cloneProject(url: String, dest: String, branch: String? = nil, workspaceId: String? = nil) async throws -> Project {
        try await call("project.clone", jsonObject([
            "url": .string(url), "dest": .string(dest), "branch": branch.map(JSONValue.string),
            "workspaceId": workspaceId.map(JSONValue.string),
        ]), timeout: 3600)
    }
}

/// 插件视图调用上下文（view.render / view.event 的公共字段）。
public struct ViewContext: Hashable {
    public var plugin: String
    public var view: String
    public var projectId: String?
    public var moduleId: String?
    public var runId: String?
    /// 首次 render 时带给插件的 state（如版本控制页把当前仓库 {"root": …} 传给插件子页）
    public var initialState: [String: JSONValue]?

    public init(plugin: String, view: String, projectId: String? = nil, moduleId: String? = nil, runId: String? = nil, initialState: [String: JSONValue]? = nil) {
        self.plugin = plugin; self.view = view; self.projectId = projectId; self.moduleId = moduleId; self.runId = runId
        self.initialState = initialState
    }

    var input: [String: JSONValue] {
        var o: [String: JSONValue] = ["plugin": .string(plugin), "view": .string(view)]
        if let projectId { o["projectId"] = .string(projectId) }
        if let moduleId { o["moduleId"] = .string(moduleId) }
        if let runId { o["runId"] = .string(runId) }
        return o
    }
}
