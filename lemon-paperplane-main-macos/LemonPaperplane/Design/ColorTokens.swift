import SwiftUI

// 由 shared/design-tokens.json 的颜色名生成（与 PaperplaneKit DesignTokens.requiredColors 一致）。

enum ColorToken: String, CaseIterable {
    case bgChrome
    case bgContent
    case bgSurface
    case bgSubtle
    case bgElevated
    case bgInput
    case bgInputDisabled
    case bgHover
    case bgPressed
    case bgSelected
    case bgSelectedInactive
    case bgCode
    case border
    case borderSubtle
    case borderStrong
    case borderFocus
    case focusRing
    case textPrimary
    case textSecondary
    case textTertiary
    case textDisabled
    case textOnAccent
    case textLink
    case iconPrimary
    case iconSecondary
    case accent
    case accentHover
    case accentPressed
    case accentSubtle
    case accentSubtleBorder
    case success
    case successSubtle
    case successBorder
    case warning
    case warningSubtle
    case warningBorder
    case danger
    case dangerHover
    case dangerSubtle
    case dangerBorder
    case info
    case infoSubtle
    case infoBorder
    case neutralSubtle
    case neutralBorder
    case statusIdle
    case statusStarting
    case statusRunning
    case statusFailed
    case statusStopping
    case buttonSecondaryBg
    case buttonSecondaryHover
    case buttonSecondaryBorder
    case switchOff
    case scrim
    case shadow
    case consoleBg
    case consoleText
    case consoleSystem
    case consoleStderr
    case consoleSelection
    case searchMatch
    case searchMatchCurrent
    case scrollbar
    case graphLane0
    case graphLane1
    case graphLane2
    case graphLane3
    case graphLane4
    case graphLane5
    case graphLane6
    case graphLane7
    case diffAddedBg
    case diffAddedText
    case diffRemovedBg
    case diffRemovedText
    case diffHunkBg
    case brandLemon
}

extension Theme {
    var bgChrome: Color { self[.bgChrome] }
    var bgContent: Color { self[.bgContent] }
    var bgSurface: Color { self[.bgSurface] }
    var bgSubtle: Color { self[.bgSubtle] }
    var bgElevated: Color { self[.bgElevated] }
    var bgInput: Color { self[.bgInput] }
    var bgInputDisabled: Color { self[.bgInputDisabled] }
    var bgHover: Color { self[.bgHover] }
    var bgPressed: Color { self[.bgPressed] }
    var bgSelected: Color { self[.bgSelected] }
    var bgSelectedInactive: Color { self[.bgSelectedInactive] }
    var bgCode: Color { self[.bgCode] }
    var border: Color { self[.border] }
    var borderSubtle: Color { self[.borderSubtle] }
    var borderStrong: Color { self[.borderStrong] }
    var borderFocus: Color { self[.borderFocus] }
    var focusRing: Color { self[.focusRing] }
    var textPrimary: Color { self[.textPrimary] }
    var textSecondary: Color { self[.textSecondary] }
    var textTertiary: Color { self[.textTertiary] }
    var textDisabled: Color { self[.textDisabled] }
    var textOnAccent: Color { self[.textOnAccent] }
    var textLink: Color { self[.textLink] }
    var iconPrimary: Color { self[.iconPrimary] }
    var iconSecondary: Color { self[.iconSecondary] }
    var accent: Color { self[.accent] }
    var accentHover: Color { self[.accentHover] }
    var accentPressed: Color { self[.accentPressed] }
    var accentSubtle: Color { self[.accentSubtle] }
    var accentSubtleBorder: Color { self[.accentSubtleBorder] }
    var success: Color { self[.success] }
    var successSubtle: Color { self[.successSubtle] }
    var successBorder: Color { self[.successBorder] }
    var warning: Color { self[.warning] }
    var warningSubtle: Color { self[.warningSubtle] }
    var warningBorder: Color { self[.warningBorder] }
    var danger: Color { self[.danger] }
    var dangerHover: Color { self[.dangerHover] }
    var dangerSubtle: Color { self[.dangerSubtle] }
    var dangerBorder: Color { self[.dangerBorder] }
    var info: Color { self[.info] }
    var infoSubtle: Color { self[.infoSubtle] }
    var infoBorder: Color { self[.infoBorder] }
    var neutralSubtle: Color { self[.neutralSubtle] }
    var neutralBorder: Color { self[.neutralBorder] }
    var statusIdle: Color { self[.statusIdle] }
    var statusStarting: Color { self[.statusStarting] }
    var statusRunning: Color { self[.statusRunning] }
    var statusFailed: Color { self[.statusFailed] }
    var statusStopping: Color { self[.statusStopping] }
    var buttonSecondaryBg: Color { self[.buttonSecondaryBg] }
    var buttonSecondaryHover: Color { self[.buttonSecondaryHover] }
    var buttonSecondaryBorder: Color { self[.buttonSecondaryBorder] }
    var switchOff: Color { self[.switchOff] }
    var scrim: Color { self[.scrim] }
    var shadow: Color { self[.shadow] }
    var consoleBg: Color { self[.consoleBg] }
    var consoleText: Color { self[.consoleText] }
    var consoleSystem: Color { self[.consoleSystem] }
    var consoleStderr: Color { self[.consoleStderr] }
    var consoleSelection: Color { self[.consoleSelection] }
    var searchMatch: Color { self[.searchMatch] }
    var searchMatchCurrent: Color { self[.searchMatchCurrent] }
    var scrollbar: Color { self[.scrollbar] }
    var graphLane0: Color { self[.graphLane0] }
    var graphLane1: Color { self[.graphLane1] }
    var graphLane2: Color { self[.graphLane2] }
    var graphLane3: Color { self[.graphLane3] }
    var graphLane4: Color { self[.graphLane4] }
    var graphLane5: Color { self[.graphLane5] }
    var graphLane6: Color { self[.graphLane6] }
    var graphLane7: Color { self[.graphLane7] }
    var diffAddedBg: Color { self[.diffAddedBg] }
    var diffAddedText: Color { self[.diffAddedText] }
    var diffRemovedBg: Color { self[.diffRemovedBg] }
    var diffRemovedText: Color { self[.diffRemovedText] }
    var diffHunkBg: Color { self[.diffHunkBg] }
    var brandLemon: Color { self[.brandLemon] }
}
