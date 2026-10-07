import SwiftUI

/// Dialer — phone-style layout: line status, large number display, keypad, call button.
struct DialerView: View {
    @EnvironmentObject var state: AppState
    @FocusState private var numberFocused: Bool

    private let keys: [(String, String?)] = [
        ("1", nil), ("2", "ABC"), ("3", "DEF"),
        ("4", "GHI"), ("5", "JKL"), ("6", "MNO"),
        ("7", "PQRS"), ("8", "TUV"), ("9", "WXYZ"),
        ("*", nil), ("0", "+"), ("#", nil),
    ]

    var body: some View {
        VStack(spacing: 0) {
            statusArea
                .padding(.top, 16)
                .padding(.horizontal, 24)

            Spacer(minLength: 16)

            VStack(spacing: 22) {
                numberDisplay
                keypad
                callActions
            }

            Spacer(minLength: 24)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MacTheme.panelFill)
        .onAppear { numberFocused = true }
    }

    // MARK: - Status

    @ViewBuilder
    private var statusArea: some View {
        let main = state.main.main
        VStack(spacing: 8) {
            if !main.isConfigured && !main.isOnline {
                setupBanner
            } else {
                lineRow(main, slot: "main")
                if state.main.secondary.isConfigured {
                    lineRow(state.main.secondary, slot: "secondary")
                }
            }
            if state.main.amoCrmConfigured {
                HStack(spacing: 6) {
                    StatusDot(tone: state.main.amoCrmOnline ? .online : .warning)
                    Text("Kommo")
                        .font(.caption.weight(.medium))
                    Text(state.main.amoCrmStatusText)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            if let banner = state.main.sipAuthFailureText, !banner.isEmpty {
                Label(banner, systemImage: "exclamationmark.triangle.fill")
                    .font(.callout)
                    .foregroundStyle(.red)
            }
        }
        .frame(maxWidth: .infinity)
    }

    private var setupBanner: some View {
        HStack(spacing: 12) {
            Image(systemName: "phone.badge.plus")
                .font(.title2)
                .symbolRenderingMode(.hierarchical)
                .foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: 2) {
                Text("No line configured").font(.callout.weight(.semibold))
                Text("Add your SIP or WebRTC account to start calling.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 12)
            Button("Set Up…") { state.openSettings(panel: .connection) }
                .buttonStyle(.borderedProminent)
                .controlSize(.small)
        }
        .padding(12)
        .frame(maxWidth: 440)
        .background(MacTheme.controlFill, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func lineRow(_ c: ConnectionStatus, slot: String) -> some View {
        HStack(spacing: 6) {
            StatusDot(tone: c.isError ? .error : (c.isOnline ? .online : .offline))
            Text(c.label.isEmpty ? (slot == "main" ? "Main line" : "Second line") : c.label)
                .font(.caption.weight(.medium))
            Text(c.text.isEmpty ? (c.isOnline ? "Connected" : "Not connected") : c.text)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            if !c.isOnline {
                Button("Reconnect") { state.reconnect(slot: slot) }
                    .buttonStyle(.link)
                    .font(.caption)
            }
        }
    }

    // MARK: - Number

    private var numberDisplay: some View {
        VStack(spacing: 12) {
            NumberField(
                text: Binding(get: { state.main.phoneNumber }, set: { state.setPhone($0) }),
                focused: $numberFocused,
                onSubmit: { state.placeCall() },
                onBackspace: { state.setPhone(String(state.main.phoneNumber.dropLast())) },
                onClear: { state.setPhone("") }
            )

            if state.main.showCallerIdPicker && !state.main.callerIds.isEmpty {
                CallerIdPicker(
                    items: state.main.callerIds,
                    selected: state.main.selectedCallerId ?? state.main.callerIds.first?.number,
                    onSelect: { state.selectCallerId($0) }
                )
            }
        }
    }

    // MARK: - Keypad / Call

    private var keypad: some View {
        let rows = stride(from: 0, to: keys.count, by: 3).map { Array(keys[$0..<min($0 + 3, keys.count)]) }
        return VStack(spacing: 14) {
            ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                HStack(spacing: 22) {
                    ForEach(row, id: \.0) { key in
                        KeypadKey(label: key.0, letters: key.1) {
                            state.setPhone(state.main.phoneNumber + key.0)
                            numberFocused = true
                        }
                    }
                }
            }
        }
    }

    private var callActions: some View {
        HStack(spacing: 16) {
            if state.main.showSplitCallButtons {
                CallActionButton(title: state.main.splitPrimaryLabel, enabled: canDial) {
                    state.placeCall(slot: "main")
                }
                .keyboardShortcut(.defaultAction)
                CallActionButton(title: state.main.splitSecondaryLabel, enabled: canDial) {
                    state.placeCall(slot: "secondary")
                }
                .keyboardShortcut(.return, modifiers: .shift)
            } else {
                CallActionButton(enabled: canDial) { state.placeCall() }
                    .keyboardShortcut(.defaultAction)
                    .help(state.main.canCall ? "Call" : "No connection")
            }
        }
    }

    private var canDial: Bool {
        state.main.canDialFromKeypad && !state.main.phoneNumber.trimmingCharacters(in: .whitespaces).isEmpty
    }
}

// MARK: - Number field

/// Rounded input with the backspace button inside. Backspace keeps its slot when hidden so the
/// centred number does not shift; a long press clears the whole number.
private struct NumberField: View {
    @Binding var text: String
    var focused: FocusState<Bool>.Binding
    var onSubmit: () -> Void
    var onBackspace: () -> Void
    var onClear: () -> Void

    @State private var hover = false

    private var isFocused: Bool { focused.wrappedValue }

    var body: some View {
        HStack(spacing: 0) {
            Color.clear.frame(width: 40, height: 1)

            TextField("", text: $text, prompt: Text("Enter number").foregroundColor(Color.secondary.opacity(0.55)))
                .textFieldStyle(.plain)
                .font(.system(size: 28, weight: .regular).monospacedDigit())
                .multilineTextAlignment(.center)
                .lineLimit(1)
                .focused(focused)
                .onSubmit(onSubmit)

            BackspaceButton(onTap: onBackspace, onLongPress: onClear)
                .opacity(text.isEmpty ? 0 : 1)
                .disabled(text.isEmpty)
                .frame(width: 40)
        }
        .padding(.horizontal, 6)
        .frame(width: 320, height: 56)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(Color.primary.opacity(hover || isFocused ? 0.07 : 0.045))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(isFocused ? Color.accentColor.opacity(0.55) : MacTheme.separator.opacity(0.6),
                              lineWidth: isFocused ? 1.5 : 1)
        )
        .contentShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .onTapGesture { focused.wrappedValue = true }
        .onHover { hover = $0 }
        .animation(.easeOut(duration: 0.12), value: text.isEmpty)
        .animation(.easeOut(duration: 0.12), value: isFocused)
    }
}

private struct BackspaceButton: View {
    var onTap: () -> Void
    var onLongPress: () -> Void
    @State private var hover = false

    var body: some View {
        Image(systemName: "delete.backward.fill")
            .symbolRenderingMode(.hierarchical)
            .font(.system(size: 17, weight: .medium))
            .foregroundStyle(hover ? Color.primary : Color.secondary)
            .frame(width: 32, height: 32)
            .background(Circle().fill(Color.primary.opacity(hover ? 0.1 : 0)))
            .contentShape(Circle())
            .onTapGesture(perform: onTap)
            .onLongPressGesture(minimumDuration: 0.5, perform: onLongPress)
            .onHover { hover = $0 }
            .help("Delete (hold to clear)")
            .accessibilityLabel("Delete")
            .accessibilityAddTraits(.isButton)
    }
}

// MARK: - Caller ID picker

private struct CallerIdPicker: View {
    let items: [CallerIdItem]
    let selected: String?
    var onSelect: (String) -> Void

    @State private var open = false
    @State private var hover = false

    private var current: CallerIdItem? {
        items.first(where: { $0.number == selected }) ?? items.first
    }

    var body: some View {
        Button { open.toggle() } label: {
            HStack(spacing: 8) {
                Image(systemName: "person.crop.circle.fill")
                    .symbolRenderingMode(.hierarchical)
                    .font(.system(size: 15))
                    .foregroundStyle(Color.accentColor)
                Text("From")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let current {
                    Text(Self.title(current))
                        .font(.callout.weight(.medium))
                        .lineLimit(1)
                    if !current.number.isEmpty, !Self.title(current).contains(current.number) {
                        Text(current.number)
                            .font(.callout.monospacedDigit())
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                } else {
                    Text("Caller ID").font(.callout.weight(.medium))
                }
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(.secondary)
            }
            .padding(.leading, 8)
            .padding(.trailing, 12)
            .padding(.vertical, 6)
            .background(Capsule().fill(Color.primary.opacity(hover || open ? 0.1 : 0.06)))
            .overlay(Capsule().strokeBorder(MacTheme.separator.opacity(0.5), lineWidth: 1))
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help("Outbound Caller ID")
        .popover(isPresented: $open, arrowEdge: .bottom) {
            list
        }
    }

    private var list: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("Outbound Caller ID")
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 10)
                .padding(.top, 4)
                .padding(.bottom, 2)
            ScrollView {
                VStack(spacing: 2) {
                    ForEach(items) { item in
                        CallerIdRow(item: item, isSelected: item.number == current?.number) {
                            onSelect(item.number)
                            open = false
                        }
                    }
                }
            }
            .frame(maxHeight: 280)
        }
        .padding(6)
        .frame(width: 300)
    }

    static func title(_ item: CallerIdItem) -> String {
        if !item.name.isEmpty { return item.name }
        if !item.displayText.isEmpty { return item.displayText }
        return item.number
    }
}

private struct CallerIdRow: View {
    let item: CallerIdItem
    let isSelected: Bool
    var action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: "checkmark")
                    .font(.system(size: 11, weight: .bold))
                    .foregroundStyle(Color.accentColor)
                    .opacity(isSelected ? 1 : 0)
                    .frame(width: 14)
                VStack(alignment: .leading, spacing: 1) {
                    Text(CallerIdPicker.title(item))
                        .font(.callout.weight(isSelected ? .semibold : .regular))
                        .lineLimit(1)
                    if !item.number.isEmpty, !CallerIdPicker.title(item).contains(item.number) {
                        Text(item.number)
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 6)
            .background(RoundedRectangle(cornerRadius: 6, style: .continuous)
                .fill(hover ? Color.accentColor.opacity(0.15) : Color.clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}
