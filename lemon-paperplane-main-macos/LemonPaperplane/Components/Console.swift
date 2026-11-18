import AppKit
import Combine
import PaperplaneKit
import SwiftUI

// 控制台：NSTextView 实现，ANSI 着色、批量追加、只保留最近若干行（完整日志在磁盘）、搜索高亮、跟随滚动。
// 只有可见时才订阅日志流。

@MainActor
final class ConsoleController: ObservableObject {
    @Published var follow = true
    @Published var wrap = UserDefaults.standard.object(forKey: "PaperplaneConsoleWrap") as? Bool ?? true {
        didSet {
            UserDefaults.standard.set(wrap, forKey: "PaperplaneConsoleWrap")
            applyWrap()
        }
    }
    @Published var query = "" { didSet { scheduleSearch() } }
    @Published private(set) var matchCount = 0
    @Published private(set) var matchIndex = 0
    @Published private(set) var ended = false
    @Published private(set) var lineCount = 0

    let scrollView: NSScrollView
    let textView: NSTextView

    private(set) var consoleId: String?
    private var stream: ConsoleStream?
    private var parser = ANSIParser()
    private var buffer: [LogLine] = [] // 视图里的行（用于换主题时重绘）
    private var lineLengths: [Int] = []
    private var lineHead = 0
    private var pending: [LogLine] = []
    private var flushScheduled = false
    private var matches: [NSRange] = []
    private var searchWork: DispatchWorkItem?
    private var programmaticScroll = false
    private var theme: Theme = DesignSystem.shared.light
    private let maxLines = M.consoleMaxLines
    private let font = MonoFont.ns(size: PPText.mono.spec.size)
    private lazy var boldFont = MonoFont.ns(size: PPText.mono.spec.size, bold: true)
    private var generation = 0
    private var observers: [NSObjectProtocol] = []
    private var pinScheduled = false
    private static let urlRegex = try? NSRegularExpression(pattern: "https?://[^\\s\"'<>\\x1b]+", options: [])

    init() {
        let sv = NSScrollView()
        sv.hasVerticalScroller = true
        sv.hasHorizontalScroller = false
        sv.autohidesScrollers = true
        sv.borderType = .noBorder
        sv.drawsBackground = true
        let tv = NSTextView(frame: .zero)
        tv.isEditable = false
        tv.isSelectable = true
        tv.isRichText = true
        tv.allowsUndo = false
        tv.usesFindBar = false
        tv.isAutomaticLinkDetectionEnabled = false
        tv.textContainerInset = NSSize(width: 8, height: 6)
        tv.isVerticallyResizable = true
        tv.autoresizingMask = [.width]
        tv.layoutManager?.allowsNonContiguousLayout = true
        tv.minSize = NSSize(width: 0, height: 0)
        tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        sv.documentView = tv
        scrollView = sv
        textView = tv
        applyWrap()
        // 跟随末尾的开关只由用户自己的滚动决定（触控板/滚轮/拖滚动条会发 live scroll 通知，程序滚动不会）。
        // 以前监听 bounds 变化并异步判断：程序滚到底后新行/排版又改变了高度，会被误判成「用户离开了底部」而关掉跟随
        let nc = NotificationCenter.default
        for name in [NSScrollView.didLiveScrollNotification, NSScrollView.didEndLiveScrollNotification] {
            observers.append(nc.addObserver(forName: name, object: sv, queue: nil) { [weak self] _ in
                MainActor.assumeIsolated { self?.userScrolled() }
            })
        }
        // 跟随时，日志区尺寸变了（刚显示出来、窗口缩放、排版完成、切换换行）就重新贴到底部；
        // 否则刚进入页面时日志区还没有尺寸，滚到的「底部」是 0，会一直停在最顶上
        sv.contentView.postsFrameChangedNotifications = true
        tv.postsFrameChangedNotifications = true
        for view in [sv.contentView, tv] as [NSView] {
            observers.append(nc.addObserver(forName: NSView.frameDidChangeNotification, object: view, queue: nil) { [weak self] _ in
                MainActor.assumeIsolated { self?.pinIfFollowing() }
            })
        }
    }

    deinit {
        stream?.stop()
        for o in observers { NotificationCenter.default.removeObserver(o) }
    }

    // MARK: 主题

    func apply(theme t: Theme) {
        let changed = t.isDark != theme.isDark || buffer.isEmpty
        theme = t
        scrollView.backgroundColor = t.ns(.consoleBg)
        textView.backgroundColor = t.ns(.consoleBg)
        textView.insertionPointColor = t.ns(.consoleText)
        textView.selectedTextAttributes = [.backgroundColor: t.ns(.consoleSelection)]
        textView.linkTextAttributes = [.foregroundColor: t.ns(.textLink), .underlineStyle: NSUnderlineStyle.single.rawValue,
                                       .cursor: NSCursor.pointingHand]
        if changed && !buffer.isEmpty { rerender() }
    }

    private func rerender() {
        let lines = buffer
        buffer.removeAll()
        lineLengths.removeAll()
        lineHead = 0
        parser.reset()
        textView.textStorage?.setAttributedString(NSAttributedString())
        append(lines)
    }

    // MARK: 订阅

    func attach(_ id: String?) {
        guard id != consoleId else { return }
        detach()
        consoleId = id
        reset()
        follow = true // 每次进入/切换到一个运行，都从日志末尾开始跟随
        guard let id, let api = AppModel.shared.api else { return }
        generation += 1
        let gen = generation
        Task { [weak self] in
            guard let self else { return }
            // 先取最近若干行，再从 lastSeq 起实时订阅
            var since: Int64 = 0
            if let res = try? await api.logs(consoleId: id, tail: 2000) {
                guard gen == self.generation else { return }
                self.append(res.lines)
                since = res.lastSeq
            }
            guard gen == self.generation else { return }
            let s = ConsoleStream(consoleId: id, since: since)
            self.stream = s
            s.start(client: api) { [weak self] update in
                Task { @MainActor in
                    guard let self, self.generation == gen else { return }
                    switch update {
                    case .lines(let l): self.enqueue(l)
                    case .ended: self.ended = true
                    case .disconnected: break
                    }
                }
            }
        }
    }

    func detach() {
        generation += 1
        stream?.stop()
        stream = nil
        consoleId = nil
    }

    func reset() {
        buffer.removeAll()
        lineLengths.removeAll()
        lineHead = 0
        pending.removeAll()
        parser.reset()
        matches.removeAll()
        matchCount = 0
        matchIndex = 0
        lineCount = 0
        ended = false
        textView.textStorage?.setAttributedString(NSAttributedString())
    }

    // MARK: 追加

    private func enqueue(_ lines: [LogLine]) {
        pending.append(contentsOf: lines)
        guard !flushScheduled else { return }
        flushScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in
            guard let self else { return }
            self.flushScheduled = false
            let batch = self.pending
            self.pending.removeAll()
            self.append(batch)
        }
    }

    func append(_ lines: [LogLine]) {
        guard !lines.isEmpty, let storage = textView.textStorage else { return }
        // 只渲染会留下来的最后 maxLines 行
        let slice = lines.count > maxLines ? Array(lines.suffix(maxLines)) : lines
        let out = NSMutableAttributedString()
        for l in slice {
            let start = out.length
            render(l, into: out)
            out.append(NSAttributedString(string: "\n"))
            lineLengths.append(out.length - start)
        }
        buffer.append(contentsOf: slice)
        storage.beginEditing()
        storage.append(out)
        // 超过上限时一次删掉多出的行（多删 10% 减少删除次数）
        let alive = lineLengths.count - lineHead
        if alive > maxLines {
            let drop = alive - maxLines + maxLines / 10
            var chars = 0
            for i in lineHead..<(lineHead + drop) { chars += lineLengths[i] }
            lineHead += drop
            storage.deleteCharacters(in: NSRange(location: 0, length: min(chars, storage.length)))
            buffer.removeFirst(min(drop, buffer.count))
            if lineHead > 20000 {
                lineLengths.removeFirst(lineHead)
                lineHead = 0
            }
        }
        storage.endEditing()
        lineCount = lineLengths.count - lineHead
        if !query.isEmpty { scheduleSearch() }
        if follow { scrollToEnd() }
    }

    private func render(_ line: LogLine, into out: NSMutableAttributedString) {
        let base: NSColor
        switch line.s {
        case "stderr": base = theme.ns(.consoleStderr)
        case "system": base = theme.ns(.consoleSystem)
        default: base = theme.ns(.consoleText)
        }
        let start = out.length
        for span in parser.parse(line.text) {
            out.append(NSAttributedString(string: span.text, attributes: attributes(span.style, base: base)))
        }
        // 可点击的地址
        if let re = Self.urlRegex {
            let s = out.string as NSString
            let range = NSRange(location: start, length: out.length - start)
            for m in re.matches(in: out.string, range: range) {
                var u = s.substring(with: m.range)
                while let last = u.last, ".,;:)]}".contains(last) { u.removeLast() }
                if let url = URL(string: u) {
                    out.addAttribute(.link, value: url, range: NSRange(location: m.range.location, length: (u as NSString).length))
                }
            }
        }
    }

    private func attributes(_ st: ANSIStyle, base: NSColor) -> [NSAttributedString.Key: Any] {
        var fg = color(st.fg, fallback: base, boldBright: st.bold)
        var bg: NSColor? = st.bg == .default ? nil : color(st.bg, fallback: .clear, boldBright: false)
        if st.inverse {
            let f = fg
            fg = bg ?? theme.ns(.consoleBg)
            bg = f
        }
        if st.dim { fg = fg.withAlphaComponent(0.6) }
        var a: [NSAttributedString.Key: Any] = [.font: st.bold ? boldFont : font, .foregroundColor: fg]
        if let bg { a[.backgroundColor] = bg }
        if st.underline { a[.underlineStyle] = NSUnderlineStyle.single.rawValue }
        if st.strikethrough { a[.strikethroughStyle] = NSUnderlineStyle.single.rawValue }
        if st.italic { a[.obliqueness] = 0.15 }
        return a
    }

    private func color(_ c: ANSIColor, fallback: NSColor, boldBright: Bool) -> NSColor {
        switch c {
        case .default: return fallback
        case .palette(let i):
            if i < 16 {
                let idx = Int(i)
                return theme.ansi.indices.contains(idx) ? theme.ansi[idx] : fallback
            }
            if let (r, g, b) = ANSIPalette.rgb(forIndex: i) {
                return NSColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: 1)
            }
            return fallback
        case .rgb(let r, let g, let b):
            return NSColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: 1)
        }
    }

    // MARK: 滚动

    func scrollToEnd() {
        programmaticScroll = true
        if let doc = scrollView.documentView {
            textView.layoutManager?.ensureLayout(for: textView.textContainer!)
            let y = max(0, doc.frame.height - scrollView.contentView.bounds.height)
            scrollView.contentView.scroll(to: NSPoint(x: scrollView.contentView.bounds.minX, y: y))
            scrollView.reflectScrolledClipView(scrollView.contentView)
        }
        programmaticScroll = false
    }

    /// 用户滚动后：离开底部就停止跟随，滚回底部就恢复跟随
    private func userScrolled() {
        guard !programmaticScroll, let doc = scrollView.documentView else { return }
        let bounds = scrollView.contentView.bounds
        let atBottom = bounds.maxY >= doc.frame.height - 6
        if atBottom != follow { follow = atBottom }
    }

    /// 跟随时把视图贴到底部（合并到下一轮主循环，等这一轮的排版和尺寸变化都完成）
    private func pinIfFollowing() {
        guard follow, !pinScheduled else { return }
        pinScheduled = true
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.pinScheduled = false
            if self.follow { self.scrollToEnd() }
        }
    }

    func setFollow(_ on: Bool) {
        follow = on
        if on { scrollToEnd() }
    }

    private func applyWrap() {
        guard let container = textView.textContainer else { return }
        if wrap {
            scrollView.hasHorizontalScroller = false
            textView.isHorizontallyResizable = false
            container.widthTracksTextView = true
            container.containerSize = NSSize(width: scrollView.contentSize.width, height: .greatestFiniteMagnitude)
            textView.frame.size.width = scrollView.contentSize.width
        } else {
            scrollView.hasHorizontalScroller = true
            textView.isHorizontallyResizable = true
            container.widthTracksTextView = false
            container.containerSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: .greatestFiniteMagnitude)
        }
    }

    // MARK: 搜索

    private func scheduleSearch() {
        searchWork?.cancel()
        let w = DispatchWorkItem { [weak self] in Task { @MainActor in self?.runSearch(keepIndex: true) } }
        searchWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12, execute: w)
    }

    private func runSearch(keepIndex: Bool) {
        guard let lm = textView.layoutManager else { return }
        let full = NSRange(location: 0, length: (textView.string as NSString).length)
        lm.removeTemporaryAttribute(.backgroundColor, forCharacterRange: full)
        matches.removeAll()
        guard !query.isEmpty else { matchCount = 0; matchIndex = 0; return }
        let s = textView.string as NSString
        var r = NSRange(location: 0, length: s.length)
        while r.length > 0 {
            let f = s.range(of: query, options: [.caseInsensitive], range: r)
            if f.location == NSNotFound { break }
            matches.append(f)
            let next = f.location + max(f.length, 1)
            r = NSRange(location: next, length: s.length - next)
            if matches.count >= 5000 { break }
        }
        matchCount = matches.count
        if !keepIndex || matchIndex >= matches.count { matchIndex = max(0, matches.count - 1) }
        highlight(scroll: !keepIndex)
    }

    private func highlight(scroll: Bool) {
        guard let lm = textView.layoutManager else { return }
        for (i, m) in matches.enumerated() {
            lm.addTemporaryAttribute(.backgroundColor, value: i == matchIndex ? theme.ns(.searchMatchCurrent) : theme.ns(.searchMatch),
                                     forCharacterRange: m)
        }
        if scroll, matches.indices.contains(matchIndex) {
            follow = false
            textView.scrollRangeToVisible(matches[matchIndex])
        }
    }

    func findNext(_ delta: Int) {
        if matches.isEmpty { runSearch(keepIndex: false); return }
        matchIndex = (matchIndex + delta + matches.count) % matches.count
        highlight(scroll: true)
    }

    // MARK: 其他

    func copyAll() {
        let sel = textView.selectedRange()
        let text = sel.length > 0 ? (textView.string as NSString).substring(with: sel) : textView.string
        Actions.copy(text)
    }

    func clearLocal() { reset() }
}

/// NSScrollView 宿主（视图被重建时仍复用 controller 里的同一个 NSTextView，内容不丢）
struct ConsoleTextView: NSViewRepresentable {
    let controller: ConsoleController

    func makeNSView(context: Context) -> NSView {
        let container = NSView()
        let sv = controller.scrollView
        sv.removeFromSuperview()
        sv.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(sv)
        NSLayoutConstraint.activate([
            sv.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            sv.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            sv.topAnchor.constraint(equalTo: container.topAnchor),
            sv.bottomAnchor.constraint(equalTo: container.bottomAnchor),
        ])
        return container
    }

    func updateNSView(_ nsView: NSView, context: Context) {}
}

/// 控制台面板：工具栏（前面可放运行控件）+ 搜索条 + 文本区 + 可选的输入行
struct ConsolePanel<Leading: View>: View {
    let consoleId: String?
    var runId: String?
    var logFile: String?
    var compact = false
    var inputEnabled = false
    var leading: Leading

    @StateObject private var c = ConsoleController()
    @State private var searching = false
    @State private var input = ""
    @FocusState private var searchFocused: Bool
    @Environment(\.theme) private var theme
    @ObservedObject private var nav = AppModel.shared.nav

    init(consoleId: String?, runId: String?, logFile: String?, compact: Bool = false, inputEnabled: Bool = false,
         @ViewBuilder leading: () -> Leading) {
        self.consoleId = consoleId
        self.runId = runId
        self.logFile = logFile
        self.compact = compact
        self.inputEnabled = inputEnabled
        self.leading = leading()
    }

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            if searching { searchBar }
            PPDivider(subtle: true)
            ConsoleTextView(controller: c)
            if inputEnabled, runId != nil {
                PPDivider(subtle: true)
                HStack(spacing: M.sm) {
                    PPIcon("chevron-right", size: M.iconSmall, color: theme.iconSecondary)
                    TextField("", text: $input)
                        .textFieldStyle(.plain)
                        .font(.pp(.mono))
                        .onSubmit(sendInput)
                    if !input.isEmpty {
                        PPIconButton("corner-down-left", help: tr("console.send"), size: M.iconButtonSmall, iconSize: M.iconSmall, action: sendInput)
                    }
                }
                .padding(.horizontal, M.md)
                .frame(height: M.rowHeight)
                .background(theme.consoleBg)
            }
        }
        .background(theme.consoleBg)
        .onAppear {
            c.apply(theme: theme)
            c.attach(consoleId)
            nav.visibleConsoles += 1
        }
        .onDisappear {
            c.detach()
            nav.visibleConsoles = max(0, nav.visibleConsoles - 1)
        }
        .onChange(of: consoleId) { id in c.attach(id) }
        .onChange(of: theme.isDark) { _ in c.apply(theme: theme) }
        .onReceive(nav.consoleFind) { _ in
            searching = true
            DispatchQueue.main.async { searchFocused = true }
        }
    }

    private var toolbar: some View {
        HStack(spacing: M.xxs) {
            leading
            Spacer(minLength: M.md)
            PPIconButton("search", help: tr("console.search") + "  ⌘F", active: searching) {
                searching.toggle()
                if searching { DispatchQueue.main.async { searchFocused = true } } else { c.query = "" }
            }
            PPIconButton("wrap-text", help: tr("console.wrap"), active: c.wrap) { c.wrap.toggle() }
            PPIconButton("arrow-down-to-line", help: tr("console.follow"), active: c.follow) { c.setFollow(!c.follow) }
            PPIconButton("copy", help: tr("console.copy")) { c.copyAll() }
            PPIconButton("eraser", help: tr("console.clear")) {
                if let runId { AppModel.shared.perform { api in try await api.clearRun(runId) } }
                c.clearLocal()
            }
            if let logFile, !logFile.isEmpty, !compact {
                PPIconButton("file-text", help: tr("console.openLog")) { Actions.openFile(logFile) }
            }
        }
        .padding(.horizontal, M.sm)
        .frame(height: M.toolbarHeight)
        .background(theme.bgContent)
    }

    private var searchBar: some View {
        HStack(spacing: M.sm) {
            PPIcon("search", size: M.iconSmall, color: theme.iconSecondary)
            TextField(tr("console.search"), text: $c.query)
                .textFieldStyle(.plain)
                .focused($searchFocused)
                .onSubmit { c.findNext(1) }
                .onExitCommand {
                    searching = false
                    c.query = ""
                }
            Text(c.query.isEmpty ? "" : (c.matchCount == 0 ? tr("console.noMatch") : "\(c.matchIndex + 1)/\(c.matchCount)"))
                .font(.pp(.small).monospacedDigit())
                .foregroundColor(theme.textTertiary)
            PPIconButton("chevron-up", help: tr("console.prev"), size: M.iconButtonSmall, iconSize: M.iconSmall) { c.findNext(-1) }
            PPIconButton("chevron-down", help: tr("console.next"), size: M.iconButtonSmall, iconSize: M.iconSmall) { c.findNext(1) }
            PPIconButton("x", help: tr("common.close"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                searching = false
                c.query = ""
            }
        }
        .padding(.horizontal, M.md)
        .frame(height: 32)
        .background(theme.bgSubtle)
    }

    private func sendInput() {
        guard let runId, !input.isEmpty else { return }
        let text = input
        input = ""
        AppModel.shared.perform { api in try await api.runInput(runId, text: text) }
    }
}

extension ConsolePanel where Leading == EmptyView {
    init(consoleId: String?, runId: String?, logFile: String?, compact: Bool = false, inputEnabled: Bool = false) {
        self.init(consoleId: consoleId, runId: runId, logFile: logFile, compact: compact, inputEnabled: inputEnabled) { EmptyView() }
    }
}
