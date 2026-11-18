import PaperplaneKit
import SwiftUI

@main
struct LemonPaperplaneApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @ObservedObject private var l10n = L10n.shared

    var body: some Scene {
        MenuBarExtra {
            Themed { MenuBarContent() }
        } label: {
            Image(nsImage: IconStore.shared.menuBarIcon)
                .accessibilityLabel(tr("app.name"))
        }
        .menuBarExtraStyle(.window)
        .commands { AppCommands(l10n: l10n) }
    }
}

/// 应用菜单（快捷键：⌘R 运行、⌘. 停止、⌘F 搜索、⌘, 设置、⌘N 导入）
struct AppCommands: Commands {
    @ObservedObject var l10n: L10n

    var body: some Commands {
        CommandGroup(replacing: .appSettings) {
            Button(tr("menu.settings")) { MainCommands.openSettings() }
                .keyboardShortcut(",", modifiers: .command)
        }
        CommandGroup(replacing: .newItem) {
            Button(tr("import.folder")) { MainCommands.importFolder() }
                .keyboardShortcut("n", modifiers: .command)
            Button(tr("import.scan")) { MainCommands.scanFolder() }
                .keyboardShortcut("n", modifiers: [.command, .shift])
            Divider()
            Button(tr("workspace.new")) { MainCommands.newWorkspace() }
        }
        CommandGroup(after: .textEditing) {
            Button(tr("menu.find")) { MainCommands.find() }
                .keyboardShortcut("f", modifiers: .command)
        }
        CommandMenu(tr("menu.run")) {
            Button(tr("menu.runProject")) { MainCommands.run() }
                .keyboardShortcut("r", modifiers: .command)
            Button(tr("menu.stopProject")) { MainCommands.stop() }
                .keyboardShortcut(".", modifiers: .command)
            Divider()
            Button(tr("menu.stopAll")) { Actions.stopAll() }
        }
        CommandGroup(replacing: .appTermination) {
            Button(tr("menu.quitUI")) { AppModel.shared.quit(stopServices: false) }
                .keyboardShortcut("q", modifiers: .command)
            Button(tr("menu.quitAll")) { AppModel.shared.quit(stopServices: true) }
                .keyboardShortcut("q", modifiers: [.command, .option])
        }
    }
}

@MainActor
enum MainCommands {
    static func openSettings() {
        AppDelegate.shared?.showMainWindow()
        AppModel.shared.nav.page = .settings
    }

    static func importFolder() {
        AppDelegate.shared?.showMainWindow()
        ImportFlow.importFolder()
    }

    static func scanFolder() {
        AppDelegate.shared?.showMainWindow()
        ImportFlow.scanFolder()
    }

    static func newWorkspace() {
        AppDelegate.shared?.showMainWindow()
        Actions.createWorkspace()
    }

    static func find() {
        let nav = AppModel.shared.nav
        if nav.visibleConsoles > 0 { nav.consoleFind.send() } else { nav.focusSearch.send() }
    }

    static func run() { Actions.runCurrent() }
    static func stop() { Actions.stopCurrent() }
}
