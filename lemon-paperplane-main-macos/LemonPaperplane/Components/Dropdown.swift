import PaperplaneKit
import SwiftUI

/// 下拉选择（自绘）。选项多于 12 个时弹出层带筛选框。
struct PPDropdown<Value: Hashable>: View {
    struct Option: Identifiable {
        let value: Value
        let title: String
        var icon: IconSource?
        var detail: String?
        var id: Value { value }
    }

    let options: [Option]
    @Binding var selection: Value
    var placeholder = ""
    var size: PPButtonSize = .regular
    var minWidth: CGFloat = 120
    var maxWidth: CGFloat? = nil
    var fullWidth = false
    var borderless = false

    @State private var open = false
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        let current = options.first { $0.value == selection }
        Button { open.toggle() } label: {
            HStack(spacing: M.sm) {
                if let icon = current?.icon { PPIcon(ref: icon, size: M.iconSmall) }
                Text(current?.title ?? placeholder)
                    .foregroundColor(current == nil ? theme.textTertiary : theme.textPrimary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: M.xs)
                PPIcon("chevrons-up-down", size: M.iconSmall, color: theme.iconSecondary)
            }
            .font(.pp(size == .small ? .small : .body))
            .padding(.horizontal, M.md)
            .frame(height: size.height)
            .frame(minWidth: minWidth, maxWidth: fullWidth ? .infinity : maxWidth, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(borderless ? Color.clear : (enabled ? theme.bgInput : theme.bgInputDisabled)))
            .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(borderless ? Color.clear : (open ? theme.borderFocus : theme.borderStrong), lineWidth: 1))
            .contentShape(Rectangle())
            .opacity(enabled ? 1 : 0.6)
        }
        .buttonStyle(.plain)
        .ppPopover(isPresented: $open, wantsKey: options.count > 12) {
            DropdownList(options: options, selection: selection) { v in
                selection = v
                open = false
            }
        }
    }
}

private struct DropdownList<Value: Hashable>: View {
    let options: [PPDropdown<Value>.Option]
    let selection: Value
    let pick: (Value) -> Void
    @State private var filter = ""
    @State private var hover: Value?
    @Environment(\.theme) private var theme

    var body: some View {
        let shown = filter.isEmpty ? options : options.filter { $0.title.localizedCaseInsensitiveContains(filter) }
        VStack(alignment: .leading, spacing: 0) {
            if options.count > 12 {
                PPTextField(tr("common.filter"), text: $filter, icon: "search", height: M.controlHeightSmall)
                    .padding(M.xs)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(shown) { o in
                        let hl = hover == o.value
                        HStack(spacing: M.md) {
                            PPIcon("check", size: M.iconSmall, color: hl ? theme.textOnAccent : theme.accent)
                                .opacity(o.value == selection ? 1 : 0)
                            if let icon = o.icon { PPIcon(ref: icon, size: M.icon) }
                            Text(o.title).lineLimit(1)
                            Spacer(minLength: M.lg)
                            if let d = o.detail {
                                Text(d).font(.pp(.small)).foregroundColor(hl ? theme.textOnAccent.opacity(0.85) : theme.textTertiary)
                            }
                        }
                        .foregroundColor(hl ? theme.textOnAccent : theme.textPrimary)
                        .padding(.horizontal, M.md)
                        .frame(height: M.rowHeight)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(hl ? theme.accent : .clear))
                        .contentShape(Rectangle())
                        .onHover { if $0 { hover = o.value } }
                        .onTapGesture { pick(o.value) }
                    }
                }
                .padding(M.xs)
                .fixedSize(horizontal: true, vertical: false)
            }
            .frame(maxHeight: 320)
        }
        .frame(minWidth: 200, maxWidth: 420)
    }
}
