import AppKit
import Combine
import PaperplaneKit
import SwiftUI

// 控制台：NSTextView 实现，ANSI 着色、批量追加、只保留最近若干行（完整日志在磁盘）、搜索高亮、跟随滚动。
// 只有可见时才订阅日志流。
//
// 文本分两段：前面是已定型的行（有 seq，只追加，超过上限从头部删）；末尾是实时尾部（内核终端模拟里
// 还在被程序原地改写的屏幕末尾若干行，如 docker push / gradle / buildkit 的进度条），每次整体替换。
// 实时尾部只占 textStorage 末尾 liveLength 个字符，不计入 maxLines / lineCount，也不进 buffer。

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
    /// 合并批里最后一次收到的实时尾部（nil = 这一批里没有变化）
    private var pendingLive: [LogLine]?
    private var flushScheduled = false
    /// 当前实时尾部（换主题时重绘）及它在 textStorage 末尾占的字符数
    private var liveLines: [LogLine] = []
    private var liveLength = 0
    private var searchScheduled = false
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
    /// 日志里的地址：连着远程服务器时 localhost 地址先转发到本机再打开
    private let linkDelegate = ConsoleLinkDelegate()

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
        tv.delegate = linkDelegate
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
        let changed = t.isDark != theme.isDark
        theme = t
        scrollView.backgroundColor = t.ns(.consoleBg)
        textView.backgroundColor = t.ns(.consoleBg)
        textView.insertionPointColor = t.ns(.consoleText)
        textView.selectedTextAttributes = [.backgroundColor: t.ns(.consoleSelection)]
        textView.linkTextAttributes = [.foregroundColor: t.ns(.textLink), .underlineStyle: NSUnderlineStyle.single.rawValue,
                                       .cursor: NSCursor.pointingHand]
        if changed && (!buffer.isEmpty || !liveLines.isEmpty) { rerender() }
    }

    private func rerender() {
        let lines = buffer
        let live = liveLines
        buffer.removeAll()
        lineLengths.removeAll()
        lineHead = 0
        liveLines.removeAll()
        liveLength = 0
        parser.reset()
        textView.textStorage?.setAttributedString(NSAttributedString())
        update(lines, live: live.isEmpty ? nil : live)
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
                self.update(res.lines, live: nil)
                since = res.lastSeq
            }
            guard gen == self.generation else { return }
            let s = ConsoleStream(consoleId: id, since: since)
            self.stream = s
            s.start(client: api) { [weak self] update in
                Task { @MainActor in
                    guard let self, self.generation == gen else { return }
                    switch update {
                    case .lines(let l, let live): self.enqueue(l, live: live)
                    case .ended:
                        self.ended = true
                        // 结束时清空实时尾部（排在已收到、还没刷出的更新后面）
                        self.enqueue([], live: [])
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
        pendingLive = nil
        liveLines.removeAll()
        liveLength = 0
        parser.reset()
        matches.removeAll()
        matchCount = 0
        matchIndex = 0
        lineCount = 0
        ended = false
        textView.textStorage?.setAttributedString(NSAttributedString())
    }

    // MARK: 追加

    /// 50ms 合并一批：已定型的行依次累积，实时尾部以最后一次为准
    /// （实时尾部总在所有已定型行之后、每次整体替换，所以这样合并与逐条应用结果相同）
    private func enqueue(_ lines: [LogLine], live: [LogLine]?) {
        pending.append(contentsOf: lines)
        if let live { pendingLive = live }
        guard !flushScheduled else { return }
        flushScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in
            guard let self else { return }
            self.flushScheduled = false
            let batch = self.pending
            let live = self.pendingLive
            self.pending.removeAll()
            self.pendingLive = nil
            self.update(batch, live: live)
        }
    }

    /// 一次更新：先把已定型的行插到实时尾部前面（超过上限时从头部删行），再整体替换实时尾部。
    /// live 为 nil 表示实时尾部不变，[] 表示清空
    func update(_ lines: [LogLine], live: [LogLine]?) {
        guard let storage = textView.textStorage else { return }
        // 内容没变的实时尾部不重绘（进度条没走时内核也可能照常推送）
        let newLive = live.flatMap { sameAsLive($0) ? nil : $0 }
        guard !lines.isEmpty || newLive != nil else { return }
        storage.beginEditing()
        if !lines.isEmpty { appendSettled(lines, to: storage) }
        if let newLive { replaceLive(newLive, in: storage) }
        storage.endEditing()
        lineCount = lineLengths.count - lineHead
        if !query.isEmpty {
            // 已定型部分变了（头部删行会让位置整体平移）就重算全部匹配；只有实时尾部变了只重算尾部
            if lines.isEmpty { refreshLiveMatches() } else { scheduleSearch(restart: false) }
        }
        if follow { scrollToEnd() }
    }

    private func appendSettled(_ lines: [LogLine], to storage: NSTextStorage) {
        // 只渲染会留下来的最后 maxLines 行
        let slice = lines.count > maxLines ? Array(lines.suffix(maxLines)) : lines
        let out = NSMutableAttributedString()
        for l in slice {
            let start = out.length
            render(l, parser: &parser, into: out)
            out.append(NSAttributedString(string: "\n"))
            lineLengths.append(out.length - start)
        }
        buffer.append(contentsOf: slice)
        // 插在实时尾部前面
        storage.insert(out, at: storage.length - liveLength)
        // 超过上限时一次删掉多出的行（多删 10% 减少删除次数）；只删已定型部分，不碰末尾的实时尾部
        let alive = lineLengths.count - lineHead
        if alive > maxLines {
            let drop = alive - maxLines + maxLines / 10
            var chars = 0
            for i in lineHead..<(lineHead + drop) { chars += lineLengths[i] }
            lineHead += drop
            storage.deleteCharacters(in: NSRange(location: 0, length: min(chars, storage.length - liveLength)))
            buffer.removeFirst(min(drop, buffer.count))
            if lineHead > 20000 {
                lineLengths.removeFirst(lineHead)
                lineHead = 0
            }
        }
    }

    /// 整体替换末尾的实时尾部（最多几十行，只动这一段字符，不重绘整篇）
    private func replaceLive(_ live: [LogLine], in storage: NSTextStorage) {
        let out = NSMutableAttributedString()
        for l in live {
            // 每行实时行的样式都是自包含的：用全新的解析器，不改动已定型行那个有状态的解析器
            var p = ANSIParser()
            render(l, parser: &p, into: out)
            out.append(NSAttributedString(string: "\n"))
        }
        storage.replaceCharacters(in: NSRange(location: storage.length - liveLength, length: liveLength), with: out)
        liveLength = out.length
        liveLines = live
    }

    private func sameAsLive(_ live: [LogLine]) -> Bool {
        live.count == liveLines.count && zip(live, liveLines).allSatisfy { $0.text == $1.text && $0.s == $1.s }
    }

    private func render(_ line: LogLine, parser: inout ANSIParser, into out: NSMutableAttributedString) {
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

    /// restart：输入变化时防抖（重新计时）；内容变化时传 false 改为节流（已排上的不再推迟，
    /// 否则持续输出时每次都被往后推，搜索结果一直刷不出来）
    private func scheduleSearch(restart: Bool = true) {
        if !restart && searchScheduled { return }
        searchWork?.cancel()
        searchScheduled = true
        let w = DispatchWorkItem { [weak self] in
            Task { @MainActor in
                guard let self else { return }
                self.searchScheduled = false
                self.runSearch(keepIndex: true)
            }
        }
        searchWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12, execute: w)
    }

    /// 只有实时尾部变了：已定型部分的匹配和高亮不动，只重算尾部里的（尾部最多几十行，代价很小）
    private func refreshLiveMatches() {
        // 已排上整体重算时交给它
        guard !searchScheduled, let lm = textView.layoutManager, let storage = textView.textStorage else { return }
        let liveStart = storage.length - liveLength
        matches.removeAll { NSMaxRange($0) > liveStart }
        let firstLive = matches.count
        let liveRange = NSRange(location: liveStart, length: liveLength)
        lm.removeTemporaryAttribute(.backgroundColor, forCharacterRange: liveRange)
        let s = storage.mutableString
        var r = liveRange
        while r.length > 0, matches.count < 5000 {
            let f = s.range(of: query, options: [.caseInsensitive], range: r)
            if f.location == NSNotFound { break }
            matches.append(f)
            let next = f.location + max(f.length, 1)
            r = NSRange(location: next, length: s.length - next)
        }
        matchCount = matches.count
        if matchIndex >= matches.count { matchIndex = max(0, matches.count - 1) }
        for i in firstLive..<matches.count {
            lm.addTemporaryAttribute(.backgroundColor, value: i == matchIndex ? theme.ns(.searchMatchCurrent) : theme.ns(.searchMatch),
                                     forCharacterRange: matches[i])
        }
        // 当前项原来在尾部、现在落回已定型部分时补上当前色
        if matchIndex < firstLive, matches.indices.contains(matchIndex) {
            lm.addTemporaryAttribute(.backgroundColor, value: theme.ns(.searchMatchCurrent), forCharacterRange: matches[matchIndex])
        }
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

    /// 没有选中内容时复制看到的全部内容（包括末尾的实时尾部）
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
            // 日志文件在服务器上：连着远程服务器时不显示
            if let logFile, !logFile.isEmpty, !compact, !Actions.isRemote {
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

/// 控制台链接点击：连着远程服务器时，日志里的 localhost / 127.0.0.1 地址指的是服务器上的端口，
/// 先经网关转发到本机再打开；其余交给系统默认处理
final class ConsoleLinkDelegate: NSObject, NSTextViewDelegate {
    func textView(_ textView: NSTextView, clickedOnLink link: Any, at charIndex: Int) -> Bool {
        let s = (link as? URL)?.absoluteString ?? (link as? String) ?? ""
        guard !s.isEmpty, ServerURL.loopbackPort(s) != nil else { return false }
        return MainActor.assumeIsolated {
            guard AppModel.shared.isRemote else { return false }
            RemoteLinkActions.openForwarded(s)
            return true
        }
    }
}
