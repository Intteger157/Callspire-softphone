import SwiftUI
import AppKit

// MARK: - Design tokens (system macOS, no custom brand chrome)

enum MacTheme {
    static let contentMaxWidth: CGFloat = 360
    static let keypadKey: CGFloat = 64
    static let callButton: CGFloat = 64
    static let corner: CGFloat = 10
    static let controlFill = Color(nsColor: .controlBackgroundColor)
    static let windowFill = Color(nsColor: .windowBackgroundColor)
    static let separator = Color(nsColor: .separatorColor)
    static let quaternary = Color(nsColor: .quaternaryLabelColor)
}

struct Identified<T>: Identifiable {
    let id = UUID()
    let value: T
    init(_ value: T) { self.value = value }
}

// MARK: - Surfaces

/// Soft inset surface — use sparingly; prefer Form/List for settings.
struct Card<Content: View>: View {
    var padding: CGFloat = 14
    @ViewBuilder var content: () -> Content
    var body: some View {
        content()
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(MacTheme.controlFill, in: RoundedRectangle(cornerRadius: MacTheme.corner, style: .continuous))
    }
}

struct SectionHeader: View {
    let title: String
    var subtitle: String? = nil
    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.title2.weight(.semibold))
            if let subtitle { Text(subtitle).font(.callout).foregroundStyle(.secondary) }
        }
    }
}

// MARK: - Status

struct StatusDot: View {
    enum Tone { case online, offline, error, warning }
    var tone: Tone
    var body: some View {
        Circle().fill(color).frame(width: 7, height: 7)
    }
    private var color: Color {
        switch tone {
        case .online: return .green
        case .offline: return Color(nsColor: .tertiaryLabelColor)
        case .error: return .red
        case .warning: return .orange
        }
    }
}

/// Compact status chip used in dialer / settings (system capsule, not WPF badge).
struct StatusPill: View {
    var text: String
    var tone: StatusDot.Tone = .offline
    var body: some View {
        HStack(spacing: 6) {
            StatusDot(tone: tone)
            Text(text)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .background(.quaternary.opacity(0.5), in: Capsule())
    }
}

struct TransportBadge: View {
    let label: String
    var isWebRtc: Bool
    var body: some View {
        Text(label.isEmpty ? (isWebRtc ? "WebRTC" : "SIP") : label)
            .font(.caption2.weight(.medium))
            .padding(.horizontal, 7).padding(.vertical, 2)
            .foregroundStyle(.primary)
            .background(.quaternary, in: Capsule())
    }
}

// MARK: - Form helpers (settings)

struct FieldLabel: View {
    let title: String
    var body: some View { Text(title).font(.caption).foregroundStyle(.secondary) }
}

struct LabeledField<Content: View>: View {
    let title: String
    @ViewBuilder var content: () -> Content
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            FieldLabel(title: title)
            content()
        }
    }
}

struct SettingRow<Control: View>: View {
    let title: String
    var detail: String? = nil
    @ViewBuilder var control: () -> Control
    var body: some View {
        HStack(alignment: .center) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                if let detail { Text(detail).font(.caption).foregroundStyle(.secondary) }
            }
            Spacer(minLength: 16)
            control().frame(maxWidth: 280)
        }
    }
}

struct SwitchRow: View {
    let title: String
    var detail: String? = nil
    @Binding var isOn: Bool
    var body: some View {
        Toggle(isOn: $isOn) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                if let detail { Text(detail).font(.caption).foregroundStyle(.secondary) }
            }
        }
        .toggleStyle(.switch)
    }
}

struct KeyValueRow: View {
    let key: String
    let value: String
    var valueColor: Color? = nil
    var keyWidth: CGFloat = 150
    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(key).foregroundStyle(.secondary).frame(width: keyWidth, alignment: .leading)
            Text(value).foregroundStyle(valueColor ?? .primary).textSelection(.enabled)
            Spacer(minLength: 0)
        }
        .font(.callout)
    }
}

// MARK: - Dialer / call controls

/// Shrinks slightly while pressed — gives keypad / call buttons tactile feedback.
struct PressScaleStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.93 : 1)
            .opacity(configuration.isPressed ? 0.8 : 1)
            .animation(.easeOut(duration: 0.1), value: configuration.isPressed)
    }
}

/// Phone-style key: digit + letters on a soft filled circle with hover highlight.
struct KeypadKey: View {
    let label: String
    var letters: String? = nil
    var size: CGFloat = MacTheme.keypadKey
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            VStack(spacing: 1) {
                Text(label)
                    .font(.system(size: size * (label == "*" ? 0.5 : 0.4), weight: .regular))
                    .foregroundStyle(.primary)
                Text(letters ?? " ")
                    .font(.system(size: 9, weight: .medium))
                    .tracking(1.5)
                    .foregroundStyle(.secondary)
                    .opacity(letters == nil ? 0 : 1)
            }
            .frame(width: size, height: size)
            .background(Circle().fill(Color.primary.opacity(hover ? 0.14 : 0.08)))
            .contentShape(Circle())
        }
        .buttonStyle(PressScaleStyle())
        .onHover { hover = $0 }
    }
}

struct RoundIconButton: View {
    let symbol: String
    var size: CGFloat = 52
    var fill: Color = Color.primary.opacity(0.08)
    var foreground: Color = .primary
    var active: Bool = false
    var help: String? = nil
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .symbolRenderingMode(.hierarchical)
                .font(.system(size: size * 0.34, weight: .medium))
                .frame(width: size, height: size)
                .foregroundStyle(active ? Color.white : foreground)
                .background(active ? Color.accentColor : fill, in: Circle())
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .help(help ?? "")
    }
}

struct CallActionButton: View {
    var title: String? = nil
    var enabled: Bool
    var systemImage: String = "phone.fill"
    var color: Color = .green
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Group {
                if let title, !title.isEmpty {
                    HStack(spacing: 6) {
                        Image(systemName: systemImage).font(.system(size: 16, weight: .semibold))
                        Text(title).font(.callout.weight(.medium)).lineLimit(1)
                    }
                    .padding(.horizontal, 18)
                    .frame(height: 48)
                    .background(color, in: Capsule())
                } else {
                    Image(systemName: systemImage)
                        .font(.system(size: 24, weight: .semibold))
                        .frame(width: MacTheme.callButton, height: MacTheme.callButton)
                        .background(color, in: Circle())
                }
            }
            .foregroundStyle(.white)
            .opacity(enabled ? 1 : 0.35)
            .contentShape(Rectangle())
        }
        .buttonStyle(PressScaleStyle())
        .disabled(!enabled)
    }
}

/// Native sidebar vibrancy behind SwiftUI content.
struct VisualEffectBackground: NSViewRepresentable {
    var material: NSVisualEffectView.Material = .sidebar
    func makeNSView(context: Context) -> NSVisualEffectView {
        let v = NSVisualEffectView()
        v.material = material
        v.blendingMode = .behindWindow
        v.state = .followsWindowActiveState
        return v
    }
    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {
        nsView.material = material
    }
}

enum Pasteboard {
    static func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }
}

// MARK: - Outbound call (history / call details)

/// Green call control: dropdown of PBX Gateway Caller IDs when assigned, otherwise a direct call or line sheet.
struct OutboundCallButton: View {
    @EnvironmentObject var state: AppState
    let phoneNumber: String
    var title: String = "Call"
    var compact: Bool = false
    var prominent: Bool = false
    var onBeforeCall: (() -> Void)?

    private var enabled: Bool { state.main.canPlaceOutbound }

    var body: some View {
        Group {
            if state.main.showSplitCallButtons {
                callButton(forceSelection: true)
            } else if state.main.hasGatewayCallerIds {
                Menu {
                    ForEach(state.main.callerIds) { item in
                        Button(callerMenuLabel(item)) {
                            startCall(callerId: item.number, forceSelection: false)
                        }
                    }
                    if state.main.callerIds.count > 1 {
                        Divider()
                        Button("Choose line & number…") {
                            startCall(forceSelection: true)
                        }
                    }
                } label: {
                    labelContent
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
            } else {
                callButton(forceSelection: true)
            }
        }
        .disabled(!enabled)
        .help(enabled ? "Call \(phoneNumber)" : "No line connected")
    }

    @ViewBuilder
    private func callButton(forceSelection: Bool) -> some View {
        Button { startCall(forceSelection: forceSelection) } label: {
            labelContent
        }
        .buttonStyle(prominent ? .borderedProminent : .plain)
        .tint(prominent ? .green : nil)
    }

    @ViewBuilder
    private var labelContent: some View {
        if compact {
            Image(systemName: "phone.fill")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 28, height: 28)
                .background(enabled ? Color.green : Color.gray.opacity(0.45), in: Circle())
        } else {
            Label(title, systemImage: "phone.fill")
        }
    }

    private func callerMenuLabel(_ item: CallerIdItem) -> String {
        if !item.displayText.isEmpty { return item.displayText }
        if !item.name.isEmpty { return "\(item.name)  \(item.number)" }
        return item.number
    }

    private func startCall(callerId: String? = nil, forceSelection: Bool) {
        onBeforeCall?()
        if prominent {
            state.scheduleOutboundCall(number: phoneNumber, forceSelection: forceSelection, callerId: callerId)
        } else {
            state.placeCall(number: phoneNumber, forceSelection: forceSelection, callerId: callerId)
        }
    }
}

extension Date {
    var historyStamp: String {
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm:ss"; return f.string(from: self)
    }
    var timeMillis: String {
        let f = DateFormatter(); f.dateFormat = "HH:mm:ss.SSS"; return f.string(from: self)
    }
}
