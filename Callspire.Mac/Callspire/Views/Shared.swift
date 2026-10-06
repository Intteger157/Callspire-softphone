import SwiftUI
import AppKit

// MARK: - Common building blocks shared by the main, call, settings and details views.

struct Identified<T>: Identifiable {
    let id = UUID()
    let value: T
    init(_ value: T) { self.value = value }
}

/// Rounded card on the window background — the macOS stand-in for the WPF bordered panels.
struct Card<Content: View>: View {
    var padding: CGFloat = 16
    @ViewBuilder var content: () -> Content
    var body: some View {
        content()
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Color(nsColor: .separatorColor), lineWidth: 1))
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

struct StatusDot: View {
    enum Tone { case online, offline, error, warning }
    var tone: Tone
    var body: some View {
        Circle().fill(color).frame(width: 8, height: 8)
    }
    private var color: Color {
        switch tone {
        case .online: return .green
        case .offline: return .secondary
        case .error: return .red
        case .warning: return .orange
        }
    }
}

/// "WebRTC" / "SIP" pill as in the Windows history list.
struct TransportBadge: View {
    let label: String
    var isWebRtc: Bool
    var body: some View {
        Text(label.isEmpty ? (isWebRtc ? "WebRTC" : "SIP") : label)
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 6).padding(.vertical, 2)
            .foregroundStyle(.white)
            .background(isWebRtc ? Color.accentColor : Color.gray, in: RoundedRectangle(cornerRadius: 4))
    }
}

/// Caption label above a form field (Windows "Connection Name" style).
struct FieldLabel: View {
    let title: String
    var body: some View { Text(title).font(.caption).foregroundStyle(.secondary) }
}

/// Labelled field block: caption + editor.
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

/// Setting row: title + description on the left, control on the right (Audio / Appearance pages).
struct SettingRow<Control: View>: View {
    let title: String
    var detail: String? = nil
    @ViewBuilder var control: () -> Control
    var body: some View {
        HStack(alignment: .center) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.body.weight(.medium))
                if let detail { Text(detail).font(.caption).foregroundStyle(.secondary) }
            }
            Spacer(minLength: 24)
            control().frame(maxWidth: 360)
        }
        .padding(.vertical, 6)
    }
}

/// Toggle with title + description (Windows "Kommo Integration" style switch).
struct SwitchRow: View {
    let title: String
    var detail: String? = nil
    @Binding var isOn: Bool
    var body: some View {
        Toggle(isOn: $isOn) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.body.weight(.medium))
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

/// Round icon button used by the dialer / call window.
struct RoundIconButton: View {
    let symbol: String
    var size: CGFloat = 56
    var fill: Color = Color.primary.opacity(0.08)
    var foreground: Color = .primary
    var active: Bool = false
    var help: String? = nil
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: size * 0.36, weight: .medium))
                .frame(width: size, height: size)
                .foregroundStyle(active ? Color.white : foreground)
                .background(active ? Color.accentColor : fill, in: Circle())
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .help(help ?? "")
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
