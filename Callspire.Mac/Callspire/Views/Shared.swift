import SwiftUI
import AppKit

// MARK: - Design tokens (system macOS, no custom brand chrome)

enum MacTheme {
    static let contentMaxWidth: CGFloat = 360
    static let keypadKey: CGFloat = 52
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

/// Soft rounded key (FaceTime / Phone–adjacent), not a stroked circle.
struct KeypadKey: View {
    let label: String
    var letters: String? = nil
    var size: CGFloat = MacTheme.keypadKey
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 0) {
                Text(label)
                    .font(.system(size: size * 0.42, weight: .regular, design: .rounded))
                if let letters, !letters.isEmpty {
                    Text(letters)
                        .font(.system(size: 8, weight: .semibold))
                        .foregroundStyle(.secondary)
                        .tracking(1.2)
                }
            }
            .frame(width: size, height: size)
            .background(.quaternary.opacity(0.55), in: Circle())
            .contentShape(Circle())
        }
        .buttonStyle(.plain)
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
            VStack(spacing: 3) {
                Image(systemName: systemImage)
                    .font(.system(size: 22, weight: .semibold))
                if let title, !title.isEmpty {
                    Text(title).font(.caption2).lineLimit(1)
                }
            }
            .foregroundStyle(.white)
            .frame(width: title == nil ? MacTheme.callButton : 88, height: MacTheme.callButton)
            .background(enabled ? color : Color.gray.opacity(0.45), in: Circle())
            .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
    }
}

enum Pasteboard {
    static func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
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
